using IaBimeta.Audit.Contracts;
using IaBimeta.Channels.Application;
using IaBimeta.Channels.Contracts;
using IaBimeta.Channels.Domain;
using IaBimeta.Channels.Infrastructure;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Channels;

/// <summary>
/// O cadastro de contas e números (instâncias de canal) com PostgreSQL real: vários por tenant, cada número de um só tenant e
/// de uma só organização, unidades autorizadas (várias por número), padrões que obedecem à lista, ativação e auditoria.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ChannelsRegistryTests(PostgresFixture banco)
{
    [Fact]
    public async Task Um_tenant_tem_varias_contas_e_varios_numeros_cada_um_na_organizacao_da_conta()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "canaisvarios");
        var contaA = await c.CriarContaAsync();
        var contaB = await c.CriarContaAsync();

        var numeroA1 = await c.RegistrarNumeroAsync(contaA.Id);
        var numeroA2 = await c.RegistrarNumeroAsync(contaA.Id);
        var numeroB1 = await c.RegistrarNumeroAsync(contaB.Id);

        Assert.Equal(3, new[] { numeroA1.Id, numeroA2.Id, numeroB1.Id }.Distinct().Count());
        Assert.All(new[] { numeroA1, numeroA2, numeroB1 }, n => Assert.Equal(c.OrganizacaoId, n.OrganizationId));
        Assert.Equal((contaA.Id, contaA.Id, contaB.Id), (numeroA1.AccountId, numeroA2.AccountId, numeroB1.AccountId));
        Assert.All(new[] { numeroA1, numeroA2, numeroB1 }, n => Assert.Equal((ChannelStatus.Inactive, 1), (n.Status, n.Version)));

        var lista = await c.ComoAsync(c.ContextoDeSistema, sp => sp.GetRequiredService<ChannelInstanceService>().ListAsync());
        Assert.Equal(new[] { numeroA1.Id, numeroA2.Id, numeroB1.Id }.Order(), lista.Select(n => n.Id).Order());
    }

    [Fact]
    public async Task O_cadastro_persiste_e_a_leitura_devolve_exatamente_o_que_foi_gravado()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "canaispersiste");
        var unidade = await c.CriarUnidadeAsync(c.OrganizacaoId, "Matriz", matriz: true);
        var setor = await c.CriarSetorAsync(unidade, "Atendimento");
        var canal = await c.CriarCanalAsync(new[] { unidade }, unidadePadrao: unidade, setorPadrao: setor, idiomaPadrao: "es-ES");

        var numero = await c.LerNumeroAsync(canal.Value);

        Assert.NotNull(numero);
        Assert.Equal((ChannelStatus.Active, Locale.Parse("es-ES"), unidade, setor), (numero.Status, numero.DefaultLocale, numero.DefaultBusinessUnitId, numero.DefaultDepartmentId));
        Assert.Equal(new[] { unidade }, numero.AuthorizedBusinessUnitIds);
    }

    [Fact]
    public async Task Um_numero_pode_servir_varias_unidades_autorizadas_da_mesma_organizacao()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "canaisunidades");
        var matriz = await c.CriarUnidadeAsync(c.OrganizacaoId, "Matriz", matriz: true);
        var filialA = await c.CriarUnidadeAsync(c.OrganizacaoId, "Filial A");
        var filialB = await c.CriarUnidadeAsync(c.OrganizacaoId, "Filial B");
        var numero = await c.NovoNumeroAsync();

        await c.AutorizarAsync(numero.Id, matriz);
        await c.AutorizarAsync(numero.Id, filialA);
        var depois = await c.AutorizarAsync(numero.Id, filialB);

        Assert.Equal(new[] { matriz, filialA, filialB }.Order(), depois.AuthorizedBusinessUnitIds);
        var relido = await c.LerNumeroAsync(numero.Id);
        Assert.Equal(new[] { matriz, filialA, filialB }.Order(), relido!.AuthorizedBusinessUnitIds);
    }

    [Fact]
    public async Task Autorizar_a_mesma_unidade_de_novo_nao_muda_nada_e_nao_audita_de_novo()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "canaisidem");
        var unidade = await c.CriarUnidadeAsync(c.OrganizacaoId, "Matriz", matriz: true);
        var numero = await c.NovoNumeroAsync();

        var primeira = await c.AutorizarAsync(numero.Id, unidade);
        var segunda = await c.AutorizarAsync(numero.Id, unidade);

        Assert.Equal(primeira.Version, segunda.Version);
        Assert.Single(await c.AuditoriaAsync("channels.instance.unit.authorize", $"{numero.Id}:{unidade}"));
    }

    [Fact]
    public async Task Unidade_de_outra_organizacao_ou_inexistente_nao_pode_ser_autorizada()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "canaisorg");
        var outraOrganizacao = await banco.CriarOrganizacaoAsync(c.Tenant, "Outra organização");
        var unidadeDeOutra = await c.CriarUnidadeAsync(outraOrganizacao.Id, "Filial da outra", matriz: true);
        var numero = await c.NovoNumeroAsync();

        await Assert.ThrowsAsync<ChannelsValidationException>(() => c.AutorizarAsync(numero.Id, unidadeDeOutra));
        await Assert.ThrowsAsync<ChannelsValidationException>(() => c.AutorizarAsync(numero.Id, Guid.NewGuid()));
        await Assert.ThrowsAsync<ChannelsValidationException>(() => c.AutorizarAsync(numero.Id, Guid.Empty));

        Assert.Empty((await c.LerNumeroAsync(numero.Id))!.AuthorizedBusinessUnitIds);
    }

    [Fact]
    public async Task Unidade_de_outro_tenant_nao_pode_ser_autorizada()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "canaistenanta");
        var outro = await CenarioDeConversas.CriarAsync(banco, "canaistenantb");
        var unidadeDoOutro = await outro.CriarUnidadeAsync(outro.OrganizacaoId, "Matriz do outro", matriz: true);
        var numero = await c.NovoNumeroAsync();

        await Assert.ThrowsAsync<ChannelsValidationException>(() => c.AutorizarAsync(numero.Id, unidadeDoOutro));
    }

    [Fact]
    public async Task A_unidade_padrao_precisa_estar_entre_as_autorizadas_e_nenhuma_outra_e_escolhida_no_lugar()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "canaispadrao");
        var autorizada = await c.CriarUnidadeAsync(c.OrganizacaoId, "Matriz", matriz: true);
        var naoAutorizada = await c.CriarUnidadeAsync(c.OrganizacaoId, "Filial");
        var numero = await c.NovoNumeroAsync();
        await c.AutorizarAsync(numero.Id, autorizada);

        await Assert.ThrowsAsync<ChannelsValidationException>(() => c.ComoAsync(
            c.ContextoDeSistema, sp => sp.GetRequiredService<ChannelInstanceService>().SetDefaultsAsync(numero.Id, naoAutorizada, null)));

        var depois = await c.LerNumeroAsync(numero.Id);
        Assert.Null(depois!.DefaultBusinessUnitId);
        Assert.Equal(numero.Version + 1, depois.Version); // só a autorização mudou o canal; o padrão recusado não deixou rastro
    }

    [Fact]
    public async Task O_setor_padrao_precisa_ser_da_unidade_padrao()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "canaissetor");
        var matriz = await c.CriarUnidadeAsync(c.OrganizacaoId, "Matriz", matriz: true);
        var filial = await c.CriarUnidadeAsync(c.OrganizacaoId, "Filial");
        var setorDaMatriz = await c.CriarSetorAsync(matriz, "Recepção");
        var setorDaFilial = await c.CriarSetorAsync(filial, "Coleta");
        var numero = await c.NovoNumeroAsync();
        await c.AutorizarAsync(numero.Id, matriz);
        await c.AutorizarAsync(numero.Id, filial);

        // Setor de outra unidade, setor inexistente e setor sem unidade são recusados.
        await Assert.ThrowsAsync<ChannelsValidationException>(() => Padroes(c, numero.Id, matriz, setorDaFilial));
        await Assert.ThrowsAsync<ChannelsValidationException>(() => Padroes(c, numero.Id, matriz, Guid.NewGuid()));
        await Assert.ThrowsAsync<ChannelsValidationException>(() => Padroes(c, numero.Id, null, setorDaMatriz));

        var valido = await Padroes(c, numero.Id, matriz, setorDaMatriz);
        Assert.Equal((matriz, setorDaMatriz), (valido.DefaultBusinessUnitId, valido.DefaultDepartmentId));
    }

    private static Task<ChannelInstanceView> Padroes(CenarioDeConversas c, Guid numero, Guid? unidade, Guid? setor) =>
        c.ComoAsync(c.ContextoDeSistema, sp => sp.GetRequiredService<ChannelInstanceService>().SetDefaultsAsync(numero, unidade, setor));

    [Fact]
    public async Task Nao_se_retira_a_unidade_padrao_nem_a_ultima_de_um_canal_ativo_e_apos_trocar_o_padrao_a_retirada_funciona()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "canaisretirar");
        var matriz = await c.CriarUnidadeAsync(c.OrganizacaoId, "Matriz", matriz: true);
        var filial = await c.CriarUnidadeAsync(c.OrganizacaoId, "Filial");
        var canal = await c.CriarCanalAsync(new[] { matriz, filial }, unidadePadrao: matriz);

        await Assert.ThrowsAsync<ChannelsValidationException>(() => c.RetirarAsync(canal.Value, matriz));
        await Padroes(c, canal.Value, filial, null);
        var depois = await c.RetirarAsync(canal.Value, matriz);

        Assert.Equal(new[] { filial }, depois.AuthorizedBusinessUnitIds);
        Assert.Equal(filial, depois.DefaultBusinessUnitId);
        await Assert.ThrowsAsync<ChannelsValidationException>(() => c.RetirarAsync(canal.Value, filial)); // a última de um canal ativo
        Assert.Single((await c.LerNumeroAsync(canal.Value))!.AuthorizedBusinessUnitIds);
    }

    [Fact]
    public async Task Ativar_exige_unidade_e_o_ciclo_ativar_suspender_reativar_e_auditado()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "canaisciclo");
        var unidade = await c.CriarUnidadeAsync(c.OrganizacaoId, "Matriz", matriz: true);
        var numero = await c.NovoNumeroAsync();

        await Assert.ThrowsAsync<ChannelsValidationException>(() => Mudar(c, numero.Id, s => s.ActivateAsync(numero.Id)));
        await c.AutorizarAsync(numero.Id, unidade);
        Assert.Equal(ChannelStatus.Active, (await Mudar(c, numero.Id, s => s.ActivateAsync(numero.Id))).Status);
        Assert.Equal(ChannelStatus.Suspended, (await Mudar(c, numero.Id, s => s.SuspendAsync(numero.Id))).Status);
        Assert.Equal(ChannelStatus.Active, (await Mudar(c, numero.Id, s => s.ActivateAsync(numero.Id))).Status);

        Assert.Equal(2, (await c.AuditoriaAsync("channels.instance.activate", numero.Id.ToString())).Count);
        Assert.Single(await c.AuditoriaAsync("channels.instance.suspend", numero.Id.ToString()));
    }

    private static Task<ChannelInstanceView> Mudar(CenarioDeConversas c, Guid numero, Func<ChannelInstanceService, Task<ChannelInstanceView>> acao) =>
        c.ComoAsync(c.ContextoDeSistema, sp => acao(sp.GetRequiredService<ChannelInstanceService>()));

    [Fact]
    public async Task O_idioma_padrao_do_numero_muda_e_pode_ser_removido()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "canaisidioma");
        var numero = await c.NovoNumeroAsync(idioma: Locale.Parse("pt-BR"));

        var en = await Mudar(c, numero.Id, s => s.SetDefaultLocaleAsync(numero.Id, Locale.Parse("en-US")));
        var nenhum = await Mudar(c, numero.Id, s => s.SetDefaultLocaleAsync(numero.Id, null));

        Assert.Equal(Locale.Parse("en-US"), en.DefaultLocale);
        Assert.Null(nenhum.DefaultLocale);
        Assert.Equal(2, (await c.AuditoriaAsync("channels.instance.locale.set", numero.Id.ToString())).Count);
    }

    [Fact]
    public async Task Toda_mudanca_de_associacao_entre_canal_e_unidade_entra_na_auditoria_com_a_unidade_no_recurso()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "canaisauditoria");
        var matriz = await c.CriarUnidadeAsync(c.OrganizacaoId, "Matriz", matriz: true);
        var filial = await c.CriarUnidadeAsync(c.OrganizacaoId, "Filial");
        var admin = c.Administrador();
        var numero = await c.NovoNumeroAsync(admin);

        await c.AutorizarAsync(numero.Id, matriz, admin);
        await c.AutorizarAsync(numero.Id, filial, admin);
        await Padroes(c, numero.Id, matriz, null);
        await c.RetirarAsync(numero.Id, filial, admin);

        var autorizou = await c.AuditoriaAsync("channels.instance.unit.authorize");
        Assert.Contains(autorizou, e => e.ResourceId == $"{numero.Id}:{matriz}" && e.ResourceType == "channel-instance-unit" && e.Outcome == AuditOutcome.Success);
        Assert.Contains(autorizou, e => e.ResourceId == $"{numero.Id}:{filial}");
        var retirou = Assert.Single(await c.AuditoriaAsync("channels.instance.unit.revoke", $"{numero.Id}:{filial}"));
        Assert.Equal(AuditOutcome.Success, retirou.Outcome);
        Assert.Single(await c.AuditoriaAsync("channels.instance.defaults.set", numero.Id.ToString()));
        Assert.Contains(await c.AuditoriaAsync("channels.instance.register", numero.Id.ToString()), e => e.ActorUserId is not null);
    }

    [Fact]
    public async Task A_autorizacao_registra_quem_autorizou()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "canaisautor");
        var unidade = await c.CriarUnidadeAsync(c.OrganizacaoId, "Matriz", matriz: true);
        var admin = c.Administrador();
        var numero = await c.NovoNumeroAsync(admin);
        await c.AutorizarAsync(numero.Id, unidade, admin);

        var autor = await c.ComoAsync(c.ContextoDeSistema, sp =>
            sp.GetRequiredService<ChannelsDbContext>().InstanceUnits.Where(u => u.BusinessUnitId == unidade).Select(u => u.AuthorizedBy).SingleAsync());

        Assert.Equal(admin.Principal!.UserId, autor);
    }

    [Fact]
    public async Task Duas_alteracoes_simultaneas_do_mesmo_canal_nao_se_sobrepoem_em_silencio()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "canaisconcorrencia");
        var unidadeA = await c.CriarUnidadeAsync(c.OrganizacaoId, "Matriz", matriz: true);
        var unidadeB = await c.CriarUnidadeAsync(c.OrganizacaoId, "Filial");
        var numero = await c.NovoNumeroAsync();

        // Dois escopos leem a mesma versão do canal e cada um muda a lista; o segundo a gravar perde a corrida.
        await using var escopoA = banco.NovoEscopo(c.ContextoDeSistema);
        await using var escopoB = banco.NovoEscopo(c.ContextoDeSistema);
        var repositorioA = escopoA.ServiceProvider.GetRequiredService<IChannelRepository>();
        var repositorioB = escopoB.ServiceProvider.GetRequiredService<IChannelRepository>();
        var canalA = (await repositorioA.FindInstanceAsync(new ChannelInstanceId(numero.Id), default))!;
        var canalB = (await repositorioB.FindInstanceAsync(new ChannelInstanceId(numero.Id), default))!;
        canalA.AuthorizeUnit(unidadeA, null, DateTimeOffset.UtcNow);
        canalB.AuthorizeUnit(unidadeB, null, DateTimeOffset.UtcNow);

        await repositorioA.SaveChangesAsync(default);
        await Assert.ThrowsAsync<ChannelsConflictException>(() => repositorioB.SaveChangesAsync(default));

        var final = await c.LerNumeroAsync(numero.Id);
        Assert.Equal(new[] { unidadeA }, final!.AuthorizedBusinessUnitIds);
    }
}
