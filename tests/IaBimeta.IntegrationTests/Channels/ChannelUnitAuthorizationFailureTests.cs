using System.Data.Common;
using IaBimeta.Api.Channels;
using IaBimeta.Channels.Contracts.Queries;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Contracts.Events;
using IaBimeta.Conversations.Domain;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.SharedKernel;

namespace IaBimeta.IntegrationTests.Channels;

/// <summary>
/// O adaptador do host (ADR 0008) só repassa a pergunta ao Channels: o "sim" permite, o "não" bloqueia e a exceção SOBE, sem virar
/// nenhum dos dois. Testes sem banco, com um Channels falso.
/// </summary>
public sealed class ChannelUnitAuthorizationAdapterTests
{
    private sealed class RoteamentoFalso(Func<Task<bool>> responder) : IChannelRouting
    {
        public List<(ChannelInstanceId Canal, Guid Unidade, CancellationToken Token)> Chamadas { get; } = new();

        public Task<ChannelRoutingView?> FindAsync(ChannelInstanceId channelInstanceId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("O adaptador só faz a pergunta de autorização.");

        public Task<bool> IsBusinessUnitAuthorizedAsync(ChannelInstanceId channelInstanceId, Guid businessUnitId, CancellationToken cancellationToken = default)
        {
            Chamadas.Add((channelInstanceId, businessUnitId, cancellationToken));
            return responder();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task O_adaptador_devolve_exatamente_a_resposta_do_Channels_e_repassa_instancia_unidade_e_cancelamento(bool resposta)
    {
        var roteamento = new RoteamentoFalso(() => Task.FromResult(resposta));
        var adaptador = new ChannelsBackedUnitAuthorization(roteamento);
        var canal = ChannelInstanceId.New();
        var unidade = Guid.NewGuid();
        using var cancelamento = new CancellationTokenSource();

        var devolvida = await adaptador.IsBusinessUnitAuthorizedAsync(canal, unidade, cancelamento.Token);

        Assert.Equal(resposta, devolvida);
        Assert.Equal((canal, unidade, cancelamento.Token), Assert.Single(roteamento.Chamadas));
    }

    [Theory]
    [InlineData(false)] // a tarefa devolvida falha
    [InlineData(true)]  // a chamada lança antes de devolver a tarefa
    public async Task A_excecao_do_Channels_sobe_pelo_adaptador_sem_virar_sim_nem_nao(bool lancaAntesDeDevolverATarefa)
    {
        var falha = new InvalidOperationException("Channels indisponível (falha sintética do teste).");
        var roteamento = new RoteamentoFalso(() => lancaAntesDeDevolverATarefa ? throw falha : Task.FromException<bool>(falha));
        var adaptador = new ChannelsBackedUnitAuthorization(roteamento);

        var capturada = await Assert.ThrowsAnyAsync<Exception>(() => adaptador.IsBusinessUnitAuthorizedAsync(ChannelInstanceId.New(), Guid.NewGuid()));

        Assert.Same(falha, capturada); // a MESMA exceção, sem embrulho e sem troca por um resultado
        Assert.Single(roteamento.Chamadas);
    }
}

/// <summary>
/// ADR 0008, decisão 5: se a consulta de autorização ao Channels falha (aqui, o banco do Channels fora do ar, de verdade), a
/// operação não acontece, a falha não é convertida em "sim" nem em "não" e a sessão não é alterada. PostgreSQL real; o Conversations
/// e o restante seguem funcionando, só o Channels não responde.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ChannelUnitAuthorizationFailureTests(PostgresFixture banco)
{
    private sealed record Cenario(
        CenarioDeConversas C,
        CenarioDeConversas Fora,
        Guid Matriz,
        Guid FilialA,
        Guid SetorDaFilialA,
        ChannelInstanceId Canal);

    /// <summary>O número autoriza a matriz e a filial A. <c>Fora</c> é o mesmo tenant com o Channels indisponível.</summary>
    private async Task<Cenario> MontarAsync(string prefixo)
    {
        var c = await CenarioDeConversas.CriarAsync(banco, prefixo, canaisReais: true);
        var matriz = await c.CriarUnidadeAsync(c.OrganizacaoId, "Matriz", matriz: true);
        var filialA = await c.CriarUnidadeAsync(c.OrganizacaoId, "Filial A");
        var setorA = await c.CriarSetorAsync(filialA, "Exames A");
        var canal = await c.CriarCanalAsync(new[] { matriz, filialA });
        return new Cenario(c, c.ComCanaisIndisponiveis(), matriz, filialA, setorA, canal);
    }

    /// <summary>A falha da consulta é a causa de verdade (do banco), não uma resposta fabricada pelo Conversations.</summary>
    private static void AssertFalhaDeVerdadeDoCanal(Exception falha)
    {
        Assert.IsNotType<ChannelUnitNotAuthorizedException>(falha); // não virou "não"
        var raiz = falha;
        while (raiz.InnerException is not null)
        {
            raiz = raiz.InnerException;
        }

        Assert.IsAssignableFrom<DbException>(raiz); // e a operação não passou: não virou "sim"
    }

    [Fact]
    public async Task Com_o_Channels_indisponivel_a_ingestao_de_sessao_nova_com_unidade_nao_e_aceita_nem_recusada_e_nada_e_gravado()
    {
        var s = await MontarAsync("indingest");
        var leitura = s.C.Usuario(s.C.OrganizacaoId, Permission.ConversationRead);
        var sessoesAntes = (await s.C.ListarSessoesAsync(leitura)).Items.Count;

        var falha = await Assert.ThrowsAnyAsync<Exception>(() => s.Fora.EntrarAsync("5511999990001", canal: s.Canal, unidade: s.Matriz, evento: "evt-fora"));

        AssertFalhaDeVerdadeDoCanal(falha);
        Assert.Equal(sessoesAntes, (await s.C.ListarSessoesAsync(leitura)).Items.Count); // nenhuma sessão ficou pendurada
        Assert.Equal(0, s.Fora.TotalDeEventosDoTenant);                                     // nenhum evento saiu
        // Com o Channels de volta, o MESMO evento entra como novo: a tentativa que falhou não deixou contato, sessão nem marca de idempotência.
        var depois = await s.C.EntrarAsync("5511999990001", canal: s.Canal, unidade: s.Matriz, evento: "evt-fora");
        Assert.Equal(IngestOutcome.Registered, depois.Outcome);
        Assert.Equal(s.Matriz, (await s.C.SessaoAsync(depois.SessionId))!.BusinessUnitId);
    }

    [Fact]
    public async Task Com_o_Channels_indisponivel_o_setor_tambem_nao_e_aceito_a_falha_sobe_e_nada_e_gravado()
    {
        var s = await MontarAsync("indsetor");
        var leitura = s.C.Usuario(s.C.OrganizacaoId, Permission.ConversationRead);
        var sessoesAntes = (await s.C.ListarSessoesAsync(leitura)).Items.Count;

        var falha = await Assert.ThrowsAnyAsync<Exception>(() => s.Fora.EntrarAsync("5511999990002", canal: s.Canal, setor: s.SetorDaFilialA));

        AssertFalhaDeVerdadeDoCanal(falha);
        Assert.Equal(sessoesAntes, (await s.C.ListarSessoesAsync(leitura)).Items.Count);
    }

    [Fact]
    public async Task Com_o_Channels_indisponivel_a_transferencia_falha_sem_alterar_a_sessao_e_o_mesmo_pedido_vale_depois()
    {
        var s = await MontarAsync("indtransf");
        var entrada = await s.C.EntrarAsync("5511999990001", canal: s.Canal, unidade: s.Matriz);
        var antes = (await s.C.SessaoAsync(entrada.SessionId))!;
        var transferId = Guid.NewGuid();

        var falha = await Assert.ThrowsAnyAsync<Exception>(
            () => s.Fora.EncaminharAsync(entrada.SessionId, s.FilialA, s.SetorDaFilialA, antes.StateVersion, transferId));

        AssertFalhaDeVerdadeDoCanal(falha); // lançou: não devolveu "aplicada" nem "não autorizada"
        var depois = (await s.C.SessaoAsync(entrada.SessionId))!;
        Assert.Equal((antes.BusinessUnitId, antes.DepartmentId, antes.StateVersion), (depois.BusinessUnitId, depois.DepartmentId, depois.StateVersion));
        Assert.Empty((await s.C.RastroAsync(entrada.SessionId))!);
        Assert.Equal(0, s.Fora.EventosDe(ConversationEventTypes.SessionTransferred, entrada.SessionId));
        Assert.Empty(await s.C.AuditoriaAsync("conversations.session.transfer", entrada.SessionId.ToString()));

        // O pedido não ficou consumido: com o Channels de volta, o MESMO pedido (mesmo transferId e mesma versão) é aplicado.
        var aplicada = await s.C.EncaminharAsync(entrada.SessionId, s.FilialA, s.SetorDaFilialA, antes.StateVersion, transferId);
        Assert.Equal(TransferOutcome.Applied, aplicada.Outcome);
        Assert.Equal(s.FilialA, (await s.C.SessaoAsync(entrada.SessionId))!.BusinessUnitId);
    }

    [Fact]
    public async Task Com_o_Channels_indisponivel_a_mensagem_em_sessao_aberta_e_a_sem_unidade_continuam_sendo_registradas()
    {
        var s = await MontarAsync("indresto");
        var primeira = await s.C.EntrarAsync("5511999990001", canal: s.Canal, unidade: s.Matriz, evento: "evt-1");

        // Sessão aberta: a unidade da mensagem não é perguntada, então o que o cliente diz não se perde. Sem unidade: nada a perguntar.
        var naSessaoAberta = await s.Fora.EntrarAsync("5511999990001", canal: s.Canal, unidade: s.Matriz, evento: "evt-2");
        var semUnidade = await s.Fora.EntrarAsync("5511999990003", canal: s.Canal, evento: "evt-3");

        Assert.Equal((IngestOutcome.Registered, primeira.SessionId), (naSessaoAberta.Outcome, naSessaoAberta.SessionId));
        Assert.Equal(IngestOutcome.Registered, semUnidade.Outcome);
        Assert.Null((await s.C.SessaoAsync(semUnidade.SessionId))!.BusinessUnitId);
    }
}
