using IaBimeta.ContactCenter.Application;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.ContactCenter.Infrastructure;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Contracts.Queries;
using IaBimeta.IntegrationTests.Conversations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.ContactCenter;

/// <summary>
/// A intenção que não tem a base completa (o estado de atendimento e a geração da colocação sobre os quais foi autorizada) NÃO tem autoridade para alterar o
/// Conversations: o executor valida isso ANTES de qualquer cerca ou comando (nem a <c>FenceAsync</c>), e o que o Conversations JÁ aplicou é reconhecido por
/// EVIDÊNCIA de leitura (a consulta de efeito, pelo identificador que a intenção guardou), nunca por um comando modificador usado como consulta. A verificação é
/// no PRÓPRIO executor, independente do que o Conversations aceitaria: o Conversations aqui é real e aceitaria os comandos. PostgreSQL real.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ExecutorAuthorityGateTests(PostgresFixture banco)
{
    private sealed record Cena(CenarioDeConversas C, ServiceQueueView Fila, Agente Ana, Agente Beto, Guid Sessao, QueueItemView Item);

    private async Task<Cena> MontarAsync(string prefixo)
    {
        var c = await CenarioDeConversas.CriarAsync(banco, prefixo);
        var fila = await c.CriarFilaAsync("A");
        var ana = await c.AgenteNaFilaAsync(banco, fila.Id);
        var beto = await c.AgenteNaFilaAsync(banco, fila.Id);
        var sessao = await c.NovaConversaAsync();
        var item = await c.NaFilaAsync(sessao, fila.Id); // a conversa espera na fila, em atendimento humano, sem responsável
        return new Cena(c, fila, ana, beto, sessao, item);
    }

    /// <summary>Grava só a INTENÇÃO de atribuir ao usuário, com a base que a função der (uma intenção anterior à base completa, ou completa).</summary>
    private static Task<PendingOperation> GravarIntencaoAsync(Cena s, Guid usuario, Func<SessionView, HandlingBasis?> baseDe) =>
        s.C.ComoAsync(s.C.ContextoDeSistema, async sp =>
        {
            var itens = sp.GetRequiredService<IQueueItemRepository>();
            var item = await itens.FindItemAsync(s.Item.Id, default) ?? throw new InvalidOperationException("O item não existe.");
            var sessao = (await sp.GetRequiredService<IConversationGateway>().GetSessionAsync(s.Sessao, default))!;
            item.BeginAssign(usuario, Guid.NewGuid(), usuario, "claim", DateTimeOffset.UtcNow, basis: baseDe(sessao));
            await itens.SaveChangesAsync(default);
            return item.Pending!;
        });

    private static HandlingBasis? SemBase(SessionView sessao) => null;

    private static HandlingBasis? SemGeracao(SessionView sessao) => new(sessao.HandlingPeriod, sessao.ResponderRef, false, null);

    private static HandlingBasis? Completa(SessionView sessao) => new(sessao.HandlingPeriod, sessao.ResponderRef, false, sessao.PlacementEpoch);

    /// <summary>Executa a operação pendente com o executor REAL sobre uma porta que conta (e, se o teste quiser, intercala) as chamadas ao Conversations.</summary>
    private static Task<(QueueItemOperationResult Resultado, Interceptador<IConversationGateway> Controle)> ExecutarAsync(
        Cena s, Action<Interceptador<IConversationGateway>>? ponto = null) =>
        s.C.ComoAsync(s.C.ContextoDeSistema, async sp =>
        {
            var (gateway, controle) = Interceptador<IConversationGateway>.Envolver(sp.GetRequiredService<IConversationGateway>());
            ponto?.Invoke(controle);
            var item = await sp.GetRequiredService<IQueueItemRepository>().FindItemAsync(s.Item.Id, default);
            var resultado = await ActivatorUtilities.CreateInstance<HandlingExecutor>(sp, gateway).DriveAsync(item!, default);
            return (resultado, controle);
        });

    private static Task<QueueItem> LinhaAsync(Cena s) =>
        s.C.ComoAsync(s.C.ContextoDeSistema, sp => sp.GetRequiredService<ContactCenterDbContext>().Items.AsNoTracking().SingleAsync(i => i.Id == s.Item.Id));

    private static void NenhumaEscritaNoConversations(Interceptador<IConversationGateway> controle)
    {
        Assert.Equal(0, controle.Chamadas(nameof(IConversationGateway.FenceAsync)));
        Assert.Equal(0, controle.Chamadas(nameof(IConversationGateway.TransitionAsync)));
        Assert.Equal(0, controle.Chamadas(nameof(IConversationGateway.TransferAsync)));
    }

    // T1: a validação da base vem antes da cerca e de qualquer comando

    [Theory]
    [InlineData("sem-base")]
    [InlineData("sem-geracao")]
    public async Task T1_a_intencao_sem_a_base_completa_nao_emite_a_cerca_nem_comando_algum_mesmo_com_um_Conversations_que_aceitaria(string variante)
    {
        var s = await MontarAsync($"gate1{variante.Length}");
        await GravarIntencaoAsync(s, s.Beto.Id, variante == "sem-base" ? SemBase : SemGeracao);
        var antes = (await s.C.SessaoAsync(s.Sessao))!;
        var tokenAnterior = (await LinhaAsync(s)).ExecutorFenceToken; // o da entrada que levou o item à fila: a intenção nova não emite outro

        var (resultado, controle) = await ExecutarAsync(s);

        // Nenhuma cerca, nenhuma transição, nenhum encaminhamento: o executor nem tenta, e o token de cerca do item não foi emitido.
        NenhumaEscritaNoConversations(controle);
        Assert.Equal(OperationOutcome.Pending, resultado.Outcome);
        var linha = await LinhaAsync(s);
        Assert.Equal(tokenAnterior, linha.ExecutorFenceToken);
        Assert.Equal((PendingKind.Assign, "BaseIncomplete"), (linha.PendingKindValue, linha.PendingLastError));

        // O Conversations ficou exatamente como estava: nada foi escrito por quem não tinha autoridade.
        var depois = (await s.C.SessaoAsync(s.Sessao))!;
        Assert.Equal((antes.StateVersion, antes.ResponderRef, antes.HandlingPeriod), (depois.StateVersion, depois.ResponderRef, depois.HandlingPeriod));
        await s.C.LimparPendenciasAsync(); // a intenção inerte não termina sozinha: não deixa trabalho elegível no banco compartilhado
    }

    [Fact]
    public async Task T1_a_intencao_com_a_base_completa_continua_executando_com_a_cerca_e_o_comando()
    {
        var s = await MontarAsync("gate1c");
        await GravarIntencaoAsync(s, s.Beto.Id, Completa);

        var (resultado, controle) = await ExecutarAsync(s);

        Assert.Equal(OperationOutcome.Completed, resultado.Outcome);
        Assert.Equal(1, controle.Chamadas(nameof(IConversationGateway.FenceAsync)));
        Assert.Equal(1, controle.Chamadas(nameof(IConversationGateway.TransitionAsync)));
        Assert.Equal(s.Beto.Id, (await s.C.SessaoAsync(s.Sessao))!.ResponderRef);
    }

    // T2: o efeito já aplicado é reconhecido por evidência, sem comando e sem depender da base

    [Fact]
    public async Task T2_o_efeito_ja_aplicado_pelo_identificador_da_intencao_conclui_o_item_sem_nenhum_comando_mesmo_sem_a_base_completa()
    {
        var s = await MontarAsync("gate2a");
        var pendente = await GravarIntencaoAsync(s, s.Beto.Id, SemGeracao);

        // O Conversations aplicou a transição da intenção (a resposta se perdeu): o registro existe, com o identificador que a intenção guardou.
        var resultadoRemoto = await s.C.ComoAsync(s.C.ContextoDeSistema, async sp => await sp.GetRequiredService<IConversationGateway>().TransitionAsync(
            new TransitionHandlingModeCommand(s.Sessao, HandlingMode.HumanAssisted, s.Beto.Id, (await s.C.SessaoAsync(s.Sessao))!.StateVersion, pendente.TransitionId, "claim"), default));
        Assert.Equal(TransitionOutcome.Applied, resultadoRemoto.Outcome);
        var versaoRemota = await s.C.VersaoDaSessaoAsync(s.Sessao);

        var (resultado, controle) = await ExecutarAsync(s);

        NenhumaEscritaNoConversations(controle);
        Assert.True(controle.Chamadas(nameof(IConversationGateway.GetEffectAsync)) >= 1);
        Assert.Equal(OperationOutcome.Completed, resultado.Outcome);
        var item = (await s.C.ItemAsync(s.Item.Id))!;
        Assert.Equal((QueueItemStatus.Assigned, s.Beto.Id, null), (item.Status, item.AssignedUserId, item.Pending));
        Assert.Equal(versaoRemota, await s.C.VersaoDaSessaoAsync(s.Sessao)); // nada foi escrito depois
    }

    [Fact]
    public async Task T2_a_consulta_que_falha_preserva_a_pendencia_e_nada_e_concluido_nem_repetido()
    {
        var s = await MontarAsync("gate2b");
        await GravarIntencaoAsync(s, s.Beto.Id, SemGeracao);

        var (resultado, controle) = await ExecutarAsync(s, g => g.Substituir(nameof(IConversationGateway.GetEffectAsync), _ => throw new InvalidOperationException("fora do ar")));

        NenhumaEscritaNoConversations(controle);
        Assert.Equal(OperationOutcome.Pending, resultado.Outcome);
        var linha = await LinhaAsync(s);
        Assert.Equal((PendingKind.Assign, nameof(EffectOutcome.Unavailable)), (linha.PendingKindValue, linha.PendingLastError));
        Assert.Equal(QueueItemStatus.Waiting, linha.Status);
        await s.C.LimparPendenciasAsync();
    }

    [Fact]
    public async Task T2_o_registro_sem_efeito_e_indeterminado_a_evidencia_e_preservada_e_o_item_nao_e_concluido()
    {
        var s = await MontarAsync("gate2c");
        var pendente = await GravarIntencaoAsync(s, s.Beto.Id, SemGeracao);

        // O Beto JÁ é o responsável, por outra via; a operação (com o identificador da intenção) é então avaliada e NÃO muda nada: o registro "sem efeito" existe.
        await s.C.TransicionarAsync(s.Sessao, HandlingMode.HumanAssisted, s.Beto.Id, await s.C.VersaoDaSessaoAsync(s.Sessao));
        var semEfeito = await s.C.ComoAsync(s.C.ContextoDeSistema, async sp => await sp.GetRequiredService<IConversationGateway>().TransitionAsync(
            new TransitionHandlingModeCommand(s.Sessao, HandlingMode.HumanAssisted, s.Beto.Id, (await s.C.SessaoAsync(s.Sessao))!.StateVersion, pendente.TransitionId, "claim"), default));
        Assert.Equal(TransitionOutcome.InvalidTransition, semEfeito.Outcome);

        var (resultado, controle) = await ExecutarAsync(s);

        // Recibo sem alteração não prova uma mudança: nada é concluído (nem repetido), a pendência e a evidência ficam.
        NenhumaEscritaNoConversations(controle);
        Assert.Equal(OperationOutcome.Pending, resultado.Outcome);
        var linha = await LinhaAsync(s);
        Assert.Equal((PendingKind.Assign, nameof(EffectOutcome.Indeterminate), QueueItemStatus.Waiting), (linha.PendingKindValue, linha.PendingLastError, linha.Status));
        await s.C.LimparPendenciasAsync();
    }

    // T12: compatibilidade do Conversations (sem rejeição global): a base completa é exigida no ContactCenter

    [Fact]
    public async Task T12_o_Conversations_continua_aceitando_a_pre_condicao_sem_a_geracao_e_confere_a_geracao_quando_ela_vem()
    {
        var s = await MontarAsync("gate12");
        var sessao = (await s.C.SessaoAsync(s.Sessao))!;

        var semGeracao = await s.C.ComoAsync(s.C.ContextoDeSistema, async sp => await sp.GetRequiredService<IConversationGateway>().TransitionAsync(
            new TransitionHandlingModeCommand(s.Sessao, HandlingMode.HumanAssisted, s.Ana.Id, sessao.StateVersion, Guid.NewGuid(), "claim", null,
                new HandlingPrecondition(null, sessao.HandlingPeriod, HandlingMode.HumanAssisted, null)), default));
        Assert.Equal(TransitionOutcome.Applied, semGeracao.Outcome); // compatibilidade documentada: sem a geração, a colocação não é conferida

        var versao = await s.C.VersaoDaSessaoAsync(s.Sessao);
        var geracaoErrada = await s.C.ComoAsync(s.C.ContextoDeSistema, async sp => await sp.GetRequiredService<IConversationGateway>().TransitionAsync(
            new TransitionHandlingModeCommand(s.Sessao, HandlingMode.HumanAssisted, s.Beto.Id, versao, Guid.NewGuid(), "claim", null,
                new HandlingPrecondition(s.Ana.Id, sessao.HandlingPeriod, HandlingMode.HumanAssisted, sessao.PlacementEpoch + 7)), default));
        Assert.Equal(TransitionOutcome.HandlingChanged, geracaoErrada.Outcome);
    }
}
