using IaBimeta.Channels.Contracts;
using IaBimeta.Channels.Contracts.Queries;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Contracts.Events;
using IaBimeta.Conversations.Domain;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Channels;

/// <summary>
/// A lista de unidades autorizadas de cada número pertence ao Channels e é obrigatória tanto no roteamento inicial quanto nas
/// transferências posteriores. O Conversations pergunta por uma porta própria, implementada pelo host sobre o
/// <c>Channels.Contracts</c> (a matriz de dependências não liga os dois módulos). PostgreSQL real e a composição de "canais
/// reais" do host: nada de dublê que aceita tudo.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ChannelUnitAuthorizationTests(PostgresFixture banco)
{
    private sealed record Cenario(
        CenarioDeConversas C,
        Guid Matriz,
        Guid FilialA,
        Guid FilialB,
        Guid SetorDaFilialA,
        Guid SetorDaFilialB,
        ChannelInstanceId Canal);

    /// <summary>O número autoriza a matriz e a filial A; a filial B existe na organização mas NÃO é autorizada.</summary>
    private async Task<Cenario> MontarAsync(string prefixo)
    {
        var c = await CenarioDeConversas.CriarAsync(banco, prefixo, canaisReais: true);
        var matriz = await c.CriarUnidadeAsync(c.OrganizacaoId, "Matriz", matriz: true);
        var filialA = await c.CriarUnidadeAsync(c.OrganizacaoId, "Filial A");
        var filialB = await c.CriarUnidadeAsync(c.OrganizacaoId, "Filial B");
        var setorA = await c.CriarSetorAsync(filialA, "Exames A");
        var setorB = await c.CriarSetorAsync(filialB, "Exames B");
        var canal = await c.CriarCanalAsync(new[] { matriz, filialA });
        return new Cenario(c, matriz, filialA, filialB, setorA, setorB, canal);
    }

    private static Task<T> NoContexto<T>(CenarioDeConversas c, RequestContext? contexto, Func<IServiceProvider, Task<T>> acao) => c.ComoAsync(contexto, acao);

    // A consulta do Channels

    [Fact]
    public async Task O_roteamento_do_canal_traz_as_unidades_autorizadas_os_padroes_e_o_idioma()
    {
        var s = await MontarAsync("rotconsulta");
        await s.C.ComoAsync(s.C.ContextoDeSistema, sp => sp.GetRequiredService<IaBimeta.Channels.Application.ChannelInstanceService>()
            .SetDefaultsAsync(s.Canal.Value, s.FilialA, s.SetorDaFilialA));
        await s.C.ComoAsync(s.C.ContextoDeSistema, sp => sp.GetRequiredService<IaBimeta.Channels.Application.ChannelInstanceService>()
            .SetDefaultLocaleAsync(s.Canal.Value, Locale.Parse("es-ES")));

        var roteamento = await NoContexto(s.C, s.C.ContextoDeSistema, sp => sp.GetRequiredService<IChannelRouting>().FindAsync(s.Canal));

        Assert.NotNull(roteamento);
        Assert.Equal(s.Canal, roteamento.ChannelInstanceId);
        Assert.Equal((s.C.OrganizacaoId, ChannelStatus.Active, Locale.Parse("es-ES"), s.FilialA, s.SetorDaFilialA),
            (roteamento.OrganizationId, roteamento.Status, roteamento.DefaultLocale, roteamento.DefaultBusinessUnitId, roteamento.DefaultDepartmentId));
        Assert.Equal(new[] { s.Matriz, s.FilialA }.Order(), roteamento.AuthorizedBusinessUnitIds);
        Assert.Contains(roteamento.DefaultBusinessUnitId!.Value, roteamento.AuthorizedBusinessUnitIds); // o padrão sempre é uma das autorizadas
    }

    [Fact]
    public async Task A_pergunta_so_responde_sim_para_a_unidade_autorizada_e_falha_fechada_para_todo_o_resto()
    {
        var s = await MontarAsync("rotpergunta");
        var outro = await CenarioDeConversas.CriarAsync(banco, "rotperguntaoutro", canaisReais: true);

        Task<bool> Perguntar(CenarioDeConversas c, ChannelInstanceId canal, Guid unidade) =>
            c.ComoAsync(c.ContextoDeSistema, sp => sp.GetRequiredService<IChannelRouting>().IsBusinessUnitAuthorizedAsync(canal, unidade));

        Assert.True(await Perguntar(s.C, s.Canal, s.Matriz));
        Assert.True(await Perguntar(s.C, s.Canal, s.FilialA));
        Assert.False(await Perguntar(s.C, s.Canal, s.FilialB));                    // existe, mas não é autorizada
        Assert.False(await Perguntar(s.C, s.Canal, Guid.NewGuid()));               // inexistente
        Assert.False(await Perguntar(s.C, s.Canal, Guid.Empty));
        Assert.False(await Perguntar(s.C, ChannelInstanceId.New(), s.Matriz));     // canal inexistente
        Assert.False(await Perguntar(s.C, default, s.Matriz));                     // canal vazio
        Assert.False(await Perguntar(outro, s.Canal, s.Matriz));                   // o canal é de outro tenant
    }

    [Fact]
    public async Task Um_canal_sem_unidades_autorizadas_nao_aceita_unidade_alguma()
    {
        var s = await MontarAsync("rotvazio");
        var semUnidades = await s.C.CriarCanalAsync(ativar: false);

        var resposta = await s.C.ComoAsync(s.C.ContextoDeSistema, sp => sp.GetRequiredService<IChannelRouting>().IsBusinessUnitAuthorizedAsync(semUnidades, s.Matriz));
        var roteamento = await s.C.ComoAsync(s.C.ContextoDeSistema, sp => sp.GetRequiredService<IChannelRouting>().FindAsync(semUnidades));

        Assert.False(resposta);
        Assert.Empty(roteamento!.AuthorizedBusinessUnitIds);
    }

    [Fact]
    public async Task A_resposta_nao_depende_da_permissao_de_canais_do_usuario_nem_da_situacao_do_canal()
    {
        var s = await MontarAsync("rotsempermissao");
        var soConversas = s.C.Usuario(s.C.OrganizacaoId, Permission.ConversationRead, Permission.ConversationManage);
        await s.C.ComoAsync(s.C.ContextoDeSistema, sp => sp.GetRequiredService<IaBimeta.Channels.Application.ChannelInstanceService>().SuspendAsync(s.Canal.Value));

        var respostaDoUsuario = await s.C.ComoAsync(soConversas, sp => sp.GetRequiredService<IChannelRouting>().IsBusinessUnitAuthorizedAsync(s.Canal, s.Matriz));
        var roteamento = await s.C.ComoAsync(soConversas, sp => sp.GetRequiredService<IChannelRouting>().FindAsync(s.Canal));

        Assert.True(respostaDoUsuario);
        Assert.Equal(ChannelStatus.Suspended, roteamento!.Status);
    }

    // Roteamento inicial: a ingestão

    [Fact]
    public async Task A_ingestao_com_unidade_autorizada_cria_a_sessao_nessa_unidade()
    {
        var s = await MontarAsync("ingok");

        var entrada = await s.C.EntrarAsync("5511999990001", canal: s.Canal, unidade: s.FilialA);

        Assert.Equal(IngestOutcome.Registered, entrada.Outcome);
        var sessao = await s.C.SessaoAsync(entrada.SessionId);
        Assert.Equal((s.FilialA, s.Canal), (sessao!.BusinessUnitId, sessao.ChannelInstanceId));
    }

    [Fact]
    public async Task A_ingestao_com_unidade_nao_autorizada_e_recusada_sem_gravar_nada_e_sem_escolher_outra()
    {
        var s = await MontarAsync("ingnegada");
        var leitura = s.C.Usuario(s.C.OrganizacaoId, Permission.ConversationRead);
        var antes = (await s.C.ListarSessoesAsync(leitura)).Items.Count;

        await Assert.ThrowsAsync<ChannelUnitNotAuthorizedException>(() => s.C.EntrarAsync("5511999990001", canal: s.Canal, unidade: s.FilialB, evento: "evt-negado"));

        Assert.Equal(antes, (await s.C.ListarSessoesAsync(leitura)).Items.Count);
        // O mesmo contato, agora com unidade autorizada, entra normalmente: a recusa não deixou contato nem sessão pendurados.
        var depois = await s.C.EntrarAsync("5511999990001", canal: s.Canal, unidade: s.Matriz, evento: "evt-aceito");
        Assert.Equal(s.Matriz, (await s.C.SessaoAsync(depois.SessionId))!.BusinessUnitId);
    }

    [Fact]
    public async Task O_setor_de_uma_unidade_nao_autorizada_tambem_e_recusado_e_o_de_uma_autorizada_define_a_unidade()
    {
        var s = await MontarAsync("ingsetor");

        await Assert.ThrowsAsync<ChannelUnitNotAuthorizedException>(() => s.C.EntrarAsync("5511999990001", canal: s.Canal, setor: s.SetorDaFilialB));
        var entrada = await s.C.EntrarAsync("5511999990002", canal: s.Canal, setor: s.SetorDaFilialA);

        var sessao = await s.C.SessaoAsync(entrada.SessionId);
        Assert.Equal((s.FilialA, s.SetorDaFilialA), (sessao!.BusinessUnitId, sessao.DepartmentId));
    }

    [Fact]
    public async Task Canal_sem_unidades_ou_inexistente_ou_de_outro_tenant_recusa_a_unidade_falha_fechada()
    {
        var s = await MontarAsync("ingfechada");
        var outro = await CenarioDeConversas.CriarAsync(banco, "ingfechadaoutro", canaisReais: true);
        var unidadeDoOutro = await outro.CriarUnidadeAsync(outro.OrganizacaoId, "Matriz do outro", matriz: true);
        var canalDoOutro = await outro.CriarCanalAsync(new[] { unidadeDoOutro });
        var semUnidades = await s.C.CriarCanalAsync(ativar: false);

        await Assert.ThrowsAsync<ChannelUnitNotAuthorizedException>(() => s.C.EntrarAsync("5511999990001", canal: semUnidades, unidade: s.Matriz));
        await Assert.ThrowsAsync<ChannelUnitNotAuthorizedException>(() => s.C.EntrarAsync("5511999990002", canal: ChannelInstanceId.New(), unidade: s.Matriz));
        // O canal é do outro tenant: para este tenant ele não existe, mesmo com a unidade "autorizada" lá.
        await Assert.ThrowsAsync<ChannelUnitNotAuthorizedException>(() => s.C.EntrarAsync("5511999990003", canal: canalDoOutro, unidade: s.Matriz));
    }

    [Fact]
    public async Task A_autorizacao_e_consultada_na_hora_sem_copia_velha()
    {
        var s = await MontarAsync("ingdinamica");
        await Assert.ThrowsAsync<ChannelUnitNotAuthorizedException>(() => s.C.EntrarAsync("5511999990001", canal: s.Canal, unidade: s.FilialB, evento: "evt-1"));

        await s.C.AutorizarAsync(s.Canal.Value, s.FilialB);
        var aceita = await s.C.EntrarAsync("5511999990001", canal: s.Canal, unidade: s.FilialB, evento: "evt-2");
        await s.C.RetirarAsync(s.Canal.Value, s.FilialB);

        Assert.Equal(s.FilialB, (await s.C.SessaoAsync(aceita.SessionId))!.BusinessUnitId);
        await Assert.ThrowsAsync<ChannelUnitNotAuthorizedException>(() => s.C.EntrarAsync("5511999990009", canal: s.Canal, unidade: s.FilialB, evento: "evt-3"));
    }

    [Fact]
    public async Task Sem_unidade_na_mensagem_nada_e_perguntado_e_a_sessao_nasce_sem_unidade()
    {
        var s = await MontarAsync("ingsemunidade");

        var entrada = await s.C.EntrarAsync("5511999990001", canal: s.Canal);

        Assert.Null((await s.C.SessaoAsync(entrada.SessionId))!.BusinessUnitId);
    }

    [Fact]
    public async Task Sessao_ja_aberta_continua_recebendo_a_mensagem_do_cliente_mesmo_se_a_associacao_foi_retirada_depois()
    {
        var s = await MontarAsync("ingexistente");
        var primeira = await s.C.EntrarAsync("5511999990001", canal: s.Canal, unidade: s.FilialA, evento: "evt-1");
        await s.C.RetirarAsync(s.Canal.Value, s.FilialA);

        // A unidade da mensagem só vale para a sessão nova; a sessão aberta não é reencaminhada e o que o cliente disse não se perde.
        var segunda = await s.C.EntrarAsync("5511999990001", canal: s.Canal, unidade: s.FilialA, evento: "evt-2");
        var terceira = await s.C.EntrarAsync("5511999990001", canal: s.Canal, unidade: s.FilialB, evento: "evt-3");

        Assert.Equal(IngestOutcome.Registered, segunda.Outcome);
        Assert.Equal(IngestOutcome.Registered, terceira.Outcome);
        Assert.All(new[] { segunda.SessionId, terceira.SessionId }, id => Assert.Equal(primeira.SessionId, id));
        Assert.Equal(s.FilialA, (await s.C.SessaoAsync(primeira.SessionId))!.BusinessUnitId);
    }

    [Fact]
    public async Task O_evento_repetido_do_provedor_continua_idempotente_mesmo_com_a_associacao_retirada()
    {
        var s = await MontarAsync("ingduplicada");
        var original = await s.C.EntrarAsync("5511999990001", canal: s.Canal, unidade: s.FilialA, evento: "evt-repetido");
        await s.C.RetirarAsync(s.Canal.Value, s.FilialA);

        var repetida = await s.C.EntrarAsync("5511999990001", canal: s.Canal, unidade: s.FilialA, evento: "evt-repetido");

        Assert.Equal((IngestOutcome.Duplicate, original.MessageId), (repetida.Outcome, repetida.MessageId));
    }

    // Transferências posteriores

    [Fact]
    public async Task A_transferencia_para_unidade_autorizada_do_canal_e_aplicada()
    {
        var s = await MontarAsync("trfok");
        var entrada = await s.C.EntrarAsync("5511999990001", canal: s.Canal, unidade: s.Matriz);

        var resultado = await s.C.EncaminharAsync(entrada.SessionId, s.FilialA, s.SetorDaFilialA, versaoEsperada: 1);

        Assert.Equal(TransferOutcome.Applied, resultado.Outcome);
        Assert.Equal(s.FilialA, (await s.C.SessaoAsync(entrada.SessionId))!.BusinessUnitId);
    }

    [Fact]
    public async Task A_transferencia_para_unidade_nao_autorizada_e_recusada_sem_gravar_nada_nem_mudar_a_versao()
    {
        var s = await MontarAsync("trfnegada");
        var entrada = await s.C.EntrarAsync("5511999990001", canal: s.Canal, unidade: s.Matriz);
        var antes = (await s.C.SessaoAsync(entrada.SessionId))!;

        var resultado = await s.C.EncaminharAsync(entrada.SessionId, s.FilialB, s.SetorDaFilialB, antes.StateVersion);

        Assert.Equal((TransferOutcome.TargetNotAuthorizedForChannel, (long?)antes.StateVersion), (resultado.Outcome, resultado.StateVersion));
        var depois = (await s.C.SessaoAsync(entrada.SessionId))!;
        Assert.Equal((s.Matriz, antes.StateVersion), (depois.BusinessUnitId, depois.StateVersion));
        Assert.Empty((await s.C.RastroAsync(entrada.SessionId))!);
        Assert.Equal(0, s.C.EventosDe(ConversationEventTypes.SessionTransferred, entrada.SessionId));
        Assert.Empty(await s.C.AuditoriaAsync("conversations.session.transfer", entrada.SessionId.ToString()));
    }

    [Fact]
    public async Task A_transferencia_acompanha_a_lista_do_canal_autorizar_e_retirar_mudam_a_resposta_na_hora()
    {
        var s = await MontarAsync("trfdinamica");
        var entrada = await s.C.EntrarAsync("5511999990001", canal: s.Canal, unidade: s.Matriz);

        var antes = await s.C.EncaminharAsync(entrada.SessionId, s.FilialB, null, versaoEsperada: 1);
        await s.C.AutorizarAsync(s.Canal.Value, s.FilialB);
        var autorizada = await s.C.EncaminharAsync(entrada.SessionId, s.FilialB, null, versaoEsperada: 1);
        await s.C.RetirarAsync(s.Canal.Value, s.FilialB);
        var retirada = await s.C.EncaminharAsync(entrada.SessionId, s.Matriz, null, versaoEsperada: 2); // volta à matriz (autorizada)
        var denovo = await s.C.EncaminharAsync(entrada.SessionId, s.FilialB, null, versaoEsperada: 3);

        Assert.Equal(
            new[] { TransferOutcome.TargetNotAuthorizedForChannel, TransferOutcome.Applied, TransferOutcome.Applied, TransferOutcome.TargetNotAuthorizedForChannel },
            new[] { antes.Outcome, autorizada.Outcome, retirada.Outcome, denovo.Outcome });
    }

    [Fact]
    public async Task O_mesmo_pedido_repetido_continua_idempotente_mesmo_se_a_unidade_foi_retirada_depois()
    {
        var s = await MontarAsync("trfidem");
        var entrada = await s.C.EntrarAsync("5511999990001", canal: s.Canal, unidade: s.Matriz);
        var transferId = Guid.NewGuid();
        var primeira = await s.C.EncaminharAsync(entrada.SessionId, s.FilialA, null, versaoEsperada: 1, transferId: transferId);
        await s.C.RetirarAsync(s.Canal.Value, s.FilialA);

        var repetida = await s.C.EncaminharAsync(entrada.SessionId, s.FilialA, null, versaoEsperada: 1, transferId: transferId);

        Assert.Equal((TransferOutcome.Applied, TransferOutcome.AlreadyApplied), (primeira.Outcome, repetida.Outcome));
        Assert.Single((await s.C.RastroAsync(entrada.SessionId))!);
    }

    [Fact]
    public async Task Cada_sessao_e_avaliada_pelo_proprio_numero_a_mesma_pessoa_em_dois_numeros_nao_mistura_as_listas()
    {
        var s = await MontarAsync("trfdoisnumeros");
        var soMatriz = await s.C.CriarCanalAsync(new[] { s.Matriz });
        var pelo1 = await s.C.EntrarAsync("5511999990001", canal: s.Canal, unidade: s.Matriz);
        var pelo2 = await s.C.EntrarAsync("5511999990001", canal: soMatriz, unidade: s.Matriz);

        var noPrimeiro = await s.C.EncaminharAsync(pelo1.SessionId, s.FilialA, null, versaoEsperada: 1); // o primeiro número atende a filial A
        var noSegundo = await s.C.EncaminharAsync(pelo2.SessionId, s.FilialA, null, versaoEsperada: 1);  // o segundo só atende a matriz

        Assert.NotEqual(pelo1.SessionId, pelo2.SessionId);
        Assert.Equal((TransferOutcome.Applied, TransferOutcome.TargetNotAuthorizedForChannel), (noPrimeiro.Outcome, noSegundo.Outcome));
    }

    [Fact]
    public async Task Quem_transfere_so_com_permissao_de_conversas_e_validado_do_mesmo_jeito_e_o_canal_suspenso_continua_valendo()
    {
        var s = await MontarAsync("trfusuario");
        var gestor = s.C.Usuario(s.C.OrganizacaoId, Permission.ConversationRead, Permission.ConversationManage);
        var entrada = await s.C.EntrarAsync("5511999990001", canal: s.Canal, unidade: s.Matriz);
        await s.C.ComoAsync(s.C.ContextoDeSistema, sp => sp.GetRequiredService<IaBimeta.Channels.Application.ChannelInstanceService>().SuspendAsync(s.Canal.Value));

        var negada = await s.C.EncaminharAsync(entrada.SessionId, s.FilialB, null, versaoEsperada: 1, contexto: gestor);
        var aplicada = await s.C.EncaminharAsync(entrada.SessionId, s.FilialA, null, versaoEsperada: 1, contexto: gestor);

        Assert.Equal((TransferOutcome.TargetNotAuthorizedForChannel, TransferOutcome.Applied), (negada.Outcome, aplicada.Outcome));
        Assert.Equal(gestor.Principal!.UserId, Assert.Single((await s.C.RastroAsync(entrada.SessionId))!).RequestedBy);
    }
}
