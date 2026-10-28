using IaBimeta.Audit.Contracts;
using IaBimeta.Channels.Application;
using IaBimeta.Channels.Contracts;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Channels;

/// <summary>
/// Quem pode ler e gerir canais, avaliado sobre a organização do recurso carregado (TEN-2): sem leitura o recurso responde
/// como inexistente, com leitura sem a permissão de gerir a operação é negada e auditada, e o cadastro de um tenant nunca
/// aparece para outro. Também: o identificador de um número ou de uma conta é único na plataforma toda.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ChannelsAuthorizationTests(PostgresFixture banco)
{
    [Fact]
    public async Task Quem_gere_canais_da_organizacao_cria_conta_e_numero()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "canaisadmin");
        var admin = c.Administrador();

        var conta = await c.CriarContaAsync(admin);
        var numero = await c.RegistrarNumeroAsync(conta.Id, admin);

        Assert.Equal(c.OrganizacaoId, conta.OrganizationId);
        Assert.Equal(conta.Id, numero.AccountId);
        Assert.Contains(await c.AuditoriaAsync("channels.account.create"), e => e.ResourceId == conta.Id.ToString() && e.ActorUserId == admin.Principal!.UserId);
    }

    [Fact]
    public async Task Quem_so_le_canais_nao_cria_nem_altera_e_a_negacao_e_auditada()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "canaisleitor");
        var unidade = await c.CriarUnidadeAsync(c.OrganizacaoId, "Matriz", matriz: true);
        var conta = await c.CriarContaAsync();
        var numero = await c.RegistrarNumeroAsync(conta.Id);
        var leitor = c.Leitor();

        await Assert.ThrowsAsync<AccessDeniedException>(() => c.CriarContaAsync(leitor));
        await Assert.ThrowsAsync<AccessDeniedException>(() => c.RegistrarNumeroAsync(conta.Id, leitor));
        await Assert.ThrowsAsync<AccessDeniedException>(() => c.AutorizarAsync(numero.Id, unidade, leitor));
        await Assert.ThrowsAsync<AccessDeniedException>(() => c.ComoAsync(leitor, sp => sp.GetRequiredService<ChannelInstanceService>().ActivateAsync(numero.Id)));
        await Assert.ThrowsAsync<AccessDeniedException>(() => c.ComoAsync(
            leitor, sp => sp.GetRequiredService<ChannelAccountService>().SetSecretAsync(conta.Id, IaBimeta.Channels.Domain.ChannelSecretKind.AccessToken, "valor-sintetico")));

        var lido = await c.LerNumeroAsync(numero.Id, leitor);
        Assert.NotNull(lido);
        Assert.Empty(lido.AuthorizedBusinessUnitIds);
        var negada = Assert.Single(await c.AuditoriaAsync("channels.instance.unit.authorize", numero.Id.ToString()));
        Assert.Equal((AuditOutcome.Denied, leitor.Principal!.UserId), (negada.Outcome, negada.ActorUserId));
    }

    [Fact]
    public async Task Sem_leitura_na_organizacao_o_recurso_responde_como_inexistente()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "canaissemleitura");
        var unidade = await c.CriarUnidadeAsync(c.OrganizacaoId, "Matriz", matriz: true);
        var conta = await c.CriarContaAsync();
        var numero = await c.RegistrarNumeroAsync(conta.Id);
        var outraOrganizacao = await banco.CriarOrganizacaoAsync(c.Tenant, "Outra organização");
        var deOutraOrganizacao = c.Administrador(outraOrganizacao.Id);

        Assert.Null(await c.LerNumeroAsync(numero.Id, deOutraOrganizacao));
        Assert.Null(await c.ComoAsync(deOutraOrganizacao, sp => sp.GetRequiredService<ChannelAccountService>().GetAsync(conta.Id)));
        await Assert.ThrowsAsync<ChannelsResourceNotFoundException>(() => c.AutorizarAsync(numero.Id, unidade, deOutraOrganizacao));
        await Assert.ThrowsAsync<ChannelsResourceNotFoundException>(() => c.RegistrarNumeroAsync(conta.Id, deOutraOrganizacao));
        await Assert.ThrowsAsync<ChannelsResourceNotFoundException>(() => c.CriarContaAsync(deOutraOrganizacao, organizacao: c.OrganizacaoId));
        await Assert.ThrowsAsync<ChannelsResourceNotFoundException>(() => c.ComoAsync(
            deOutraOrganizacao, sp => sp.GetRequiredService<ChannelAccountService>().SetSecretAsync(conta.Id, IaBimeta.Channels.Domain.ChannelSecretKind.AppSecret, "valor-sintetico")));
        Assert.Empty((await c.LerNumeroAsync(numero.Id))!.AuthorizedBusinessUnitIds);
    }

    [Fact]
    public async Task Organizacao_inexistente_responde_como_nao_encontrada_ao_criar_conta()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "canaisorginexistente");

        await Assert.ThrowsAsync<ChannelsResourceNotFoundException>(() => c.CriarContaAsync(organizacao: Guid.NewGuid()));
    }

    [Fact]
    public async Task As_listas_so_trazem_as_organizacoes_com_leitura_e_sem_leitura_alguma_a_operacao_e_negada()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "canaislista");
        var outra = await banco.CriarOrganizacaoAsync(c.Tenant, "Outra organização");
        var contaA = await c.CriarContaAsync();
        var contaB = await c.CriarContaAsync(organizacao: outra.Id);
        var numeroA = await c.RegistrarNumeroAsync(contaA.Id);
        var numeroB = await c.RegistrarNumeroAsync(contaB.Id);

        var soA = c.Administrador();
        var soB = c.Administrador(outra.Id);
        var doTenant = c.Usuario(null, Permission.OrganizationRead, Permission.ChannelRead);
        var semLeitura = c.Usuario(c.OrganizacaoId, Permission.OrganizationRead);

        Assert.Equal(new[] { numeroA.Id }, (await Instancias(c, soA)).Select(n => n.Id));
        Assert.Equal(new[] { numeroB.Id }, (await Instancias(c, soB)).Select(n => n.Id));
        Assert.Equal(new[] { numeroA.Id, numeroB.Id }.Order(), (await Instancias(c, doTenant)).Select(n => n.Id).Order());
        Assert.Equal(new[] { contaA.Id }, (await c.ComoAsync(soA, sp => sp.GetRequiredService<ChannelAccountService>().ListAsync())).Select(a => a.Id));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Instancias(c, semLeitura));
        await Assert.ThrowsAsync<AccessDeniedException>(() => c.ComoAsync(semLeitura, sp => sp.GetRequiredService<ChannelAccountService>().ListAsync()));
    }

    private static Task<IReadOnlyList<ChannelInstanceView>> Instancias(CenarioDeConversas c, RequestContext contexto) =>
        c.ComoAsync(contexto, sp => sp.GetRequiredService<ChannelInstanceService>().ListAsync());

    [Fact]
    public async Task O_cadastro_de_um_tenant_nunca_aparece_para_outro_nem_com_permissao_de_escopo_do_tenant()
    {
        var mauro = await CenarioDeConversas.CriarAsync(banco, "canaismauro");
        var pedro = await CenarioDeConversas.CriarAsync(banco, "canaispedro");
        var unidadeDePedro = await pedro.CriarUnidadeAsync(pedro.OrganizacaoId, "Matriz do Pedro", matriz: true);
        var contaDoMauro = await mauro.CriarContaAsync();
        var numeroDoMauro = await mauro.RegistrarNumeroAsync(contaDoMauro.Id);
        var adminDoPedro = pedro.Usuario(null, Permission.OrganizationRead, Permission.ChannelRead, Permission.ChannelManage);

        Assert.Null(await pedro.LerNumeroAsync(numeroDoMauro.Id, adminDoPedro));
        Assert.Null(await pedro.ComoAsync(adminDoPedro, sp => sp.GetRequiredService<ChannelAccountService>().GetAsync(contaDoMauro.Id)));
        Assert.Empty(await Instancias(pedro, adminDoPedro));
        await Assert.ThrowsAsync<ChannelsResourceNotFoundException>(() => pedro.AutorizarAsync(numeroDoMauro.Id, unidadeDePedro, adminDoPedro));
        await Assert.ThrowsAsync<ChannelsResourceNotFoundException>(() => pedro.RegistrarNumeroAsync(contaDoMauro.Id, adminDoPedro));
        Assert.Empty((await mauro.LerNumeroAsync(numeroDoMauro.Id))!.AuthorizedBusinessUnitIds);
    }

    [Fact]
    public async Task Sem_contexto_de_tenant_toda_operacao_do_modulo_falha_fechada()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "canaissemcontexto");
        var id = Guid.NewGuid();

        await Assert.ThrowsAsync<TenantContextRequiredException>(() => c.ComoAsync(null, sp => sp.GetRequiredService<ChannelAccountService>().ListAsync()));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => c.ComoAsync(null, sp => sp.GetRequiredService<ChannelInstanceService>().ListAsync()));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => c.ComoAsync(null, sp => sp.GetRequiredService<ChannelInstanceService>().GetAsync(id)));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => c.ComoAsync(null, sp => sp.GetRequiredService<ChannelInstanceService>().ActivateAsync(id)));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => c.ComoAsync(
            null, sp => sp.GetRequiredService<ChannelAccountService>().CreateAsync(c.OrganizacaoId, ChannelProvider.WhatsAppCloud, "x", "x")));
        await Assert.ThrowsAsync<TenantContextRequiredException>(() => c.ComoAsync(null, sp => sp.GetRequiredService<IaBimeta.Channels.Contracts.Queries.IChannelRouting>().FindAsync(c.CanalId)));
    }

    [Fact]
    public async Task O_identificador_do_numero_e_unico_na_plataforma_e_a_recusa_nao_revela_de_quem_e()
    {
        var mauro = await CenarioDeConversas.CriarAsync(banco, "canaisunicoa");
        var pedro = await CenarioDeConversas.CriarAsync(banco, "canaisunicob");
        var contaDoMauro = await mauro.CriarContaAsync();
        var contaDoPedro = await pedro.CriarContaAsync();
        var numero = ChannelsTestSupport.Unico("num");
        await mauro.RegistrarNumeroAsync(contaDoMauro.Id, numero: numero);

        var outroTenant = await Assert.ThrowsAsync<ChannelIdentifierInUseException>(() => pedro.RegistrarNumeroAsync(contaDoPedro.Id, numero: numero));
        var mesmoTenant = await Assert.ThrowsAsync<ChannelIdentifierInUseException>(() => mauro.RegistrarNumeroAsync(contaDoMauro.Id, numero: numero));

        Assert.Equal(outroTenant.Message, mesmoTenant.Message);
        Assert.DoesNotContain(mauro.Tenant.Slug, outroTenant.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(numero, outroTenant.Message, StringComparison.Ordinal);
        Assert.Empty(await Instancias(pedro, pedro.ContextoDeSistema));
    }

    [Fact]
    public async Task O_identificador_da_conta_no_provedor_e_unico_na_plataforma()
    {
        var mauro = await CenarioDeConversas.CriarAsync(banco, "canaiswabaa");
        var pedro = await CenarioDeConversas.CriarAsync(banco, "canaiswabab");
        var waba = ChannelsTestSupport.Unico("waba");
        await mauro.CriarContaAsync(contaNoProvedor: waba);

        await Assert.ThrowsAsync<ChannelIdentifierInUseException>(() => pedro.CriarContaAsync(contaNoProvedor: waba));
        await Assert.ThrowsAsync<ChannelIdentifierInUseException>(() => mauro.CriarContaAsync(contaNoProvedor: waba));
        Assert.Empty(await pedro.ComoAsync(pedro.ContextoDeSistema, sp => sp.GetRequiredService<ChannelAccountService>().ListAsync()));
    }

    [Fact]
    public async Task Um_cadastro_recusado_por_identificador_em_uso_nao_deixa_resto_e_o_numero_pode_ser_registrado_com_outro_identificador()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "canaisresto");
        var conta = await c.CriarContaAsync();
        var numero = ChannelsTestSupport.Unico("num");
        await c.RegistrarNumeroAsync(conta.Id, numero: numero);

        await Assert.ThrowsAsync<ChannelIdentifierInUseException>(() => c.RegistrarNumeroAsync(conta.Id, numero: numero));
        var outro = await c.RegistrarNumeroAsync(conta.Id);

        Assert.Equal(2, (await Instancias(c, c.ContextoDeSistema)).Count);
        Assert.NotEqual(numero, outro.PhoneNumberId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("com espaco")]
    [InlineData("a/b")]
    [InlineData("'; DROP TABLE channels.channel_routes; --")]
    public async Task Identificador_de_numero_invalido_e_recusado_sem_tocar_o_banco(string numero)
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "canaisidinvalido");
        var conta = await c.CriarContaAsync();

        await Assert.ThrowsAsync<IaBimeta.Channels.Domain.ChannelsValidationException>(() => c.RegistrarNumeroAsync(conta.Id, numero: numero));

        Assert.Empty(await Instancias(c, c.ContextoDeSistema));
    }
}
