using IaBimeta.Audit.Contracts;
using IaBimeta.ContactCenter.Application;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.IntegrationTests.Channels;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.IntegrationTests.Messaging;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.ContactCenter;

/// <summary>
/// A autorização das operações que mudam o atendimento (assumir, passar a outro, devolver à fila, transferir, devolver à IA) é sobre a
/// autoridade EFETIVA: o responsável é o do Conversations, e o estado sobre o qual se autorizou vai com o comando como pré-condição,
/// conferida dentro da trava da sessão. O que o ContactCenter ainda mostra (a projeção local) não dá permissão sobre um atendimento que já
/// é de outro; renovar a versão esperada não renova a autorização; a repetição de uma operação já confirmada continua reconhecida.
/// PostgreSQL real; as intercalações são determinísticas.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class OperationAuthorityTests(PostgresFixture banco)
{
    /// <summary>A fila A (supervisor, Ana, Carla, Beto) e a fila B (Dora). O item está na fila A.</summary>
    private sealed record Cena(
        CenarioDeConversas C, ServiceQueueView FilaA, ServiceQueueView FilaB, Agente Supervisor, Agente Ana, Agente Carla, Agente Beto, Agente Dora,
        string Contato, Guid Sessao, QueueItemView Item);

    private async Task<Cena> MontarAsync(string prefixo, bool assumida = true, bool mensageria = false)
    {
        if (mensageria)
        {
            banco.RelogioDaMensageria.Sincronizar();
        }

        var c = await CenarioDeConversas.CriarAsync(banco, prefixo, mensageria: mensageria);
        var filaA = await c.CriarFilaAsync("A");
        var filaB = await c.CriarFilaAsync("B");
        var supervisor = await c.AgenteNaFilaAsync(banco, filaA.Id, QueueMemberRole.Supervisor);
        var ana = await c.AgenteNaFilaAsync(banco, filaA.Id);
        var carla = await c.AgenteNaFilaAsync(banco, filaA.Id);
        var beto = await c.AgenteNaFilaAsync(banco, filaA.Id);
        var dora = await c.AgenteNaFilaAsync(banco, filaB.Id);
        var contato = ContactCenterTestSupport.Unico("contato");
        var sessao = (await c.EntrarAsync(contato, texto: "antes")).SessionId;
        var item = await c.NaFilaAsync(sessao, filaA.Id);
        if (assumida)
        {
            await c.AssumirAsync(item.Id, ana.Contexto);
        }

        return new Cena(c, filaA, filaB, supervisor, ana, carla, beto, dora, contato, sessao, item);
    }

    /// <summary>Uma conversa a mais na fila A, com a Ana como responsável.</summary>
    private static async Task<QueueItemView> OutraDaAnaAsync(Cena s)
    {
        var item = await s.C.NaFilaAsync(await s.C.NovaConversaAsync(), s.FilaA.Id);
        Assert.Equal(OperationOutcome.Completed, (await s.C.AssumirAsync(item.Id, s.Ana.Contexto)).Outcome);
        return item;
    }

    /// <summary>O responsável muda DIRETO no Conversations (por fora do ContactCenter): o item local continua como estava, sem intenção pendente.</summary>
    private static async Task MudarPorForaAsync(Cena s, Guid? responsavel, Guid? sessao = null)
    {
        var alvo = sessao ?? s.Sessao;
        Assert.Equal(
            TransitionOutcome.Applied,
            (await s.C.TransicionarAsync(alvo, HandlingMode.HumanAssisted, responsavel, await s.C.VersaoDaSessaoAsync(alvo))).Outcome);
    }

    private static async Task EstadoAsync(Cena s, HandlingMode modo, Guid? responsavel, Guid? sessao = null)
    {
        var visao = (await s.C.SessaoAsync(sessao ?? s.Sessao))!;
        Assert.Equal((modo, responsavel), (visao.HandlingMode, visao.ResponderRef));
    }

    private static async Task ItemAsync(Cena s, QueueItemStatus situacao, Guid? responsavel, Guid? item = null)
    {
        var atual = (await s.C.ItemAsync(item ?? s.Item.Id))!;
        Assert.Equal((situacao, responsavel, null), (atual.Status, atual.AssignedUserId, atual.Pending));
    }

    private static async Task NegacoesAsync(Cena s, string operacao, Agente autor, int esperadas)
    {
        var negadas = (await s.C.AuditoriaAsync(operacao, s.Item.Id.ToString())).Where(e => e.ActorUserId == autor.Id && e.Outcome == AuditOutcome.Denied).ToList();
        Assert.Equal(esperadas, negadas.Count);
    }

    /// <summary>Roda a operação com uma porta do Conversations (a do SERVIÇO, que autoriza) em que o teste intercala outra operação.</summary>
    private static Task<T> NaAutorizacaoAsync<T>(Cena s, RequestContext ator, Action<Interceptador<IConversationGateway>> ponto, Func<QueueItemService, Task<T>> operacao) =>
        s.C.ComoAsync(ator, async sp =>
        {
            var (gateway, controle) = Interceptador<IConversationGateway>.Envolver(sp.GetRequiredService<IConversationGateway>());
            ponto(controle);
            return await operacao(ActivatorUtilities.CreateInstance<QueueItemService>(sp, gateway));
        });

    /// <summary>Roda a operação com uma porta do Conversations (a do EXECUTOR, que escreve) em que o teste intercala outra operação.</summary>
    private static Task<T> NaExecucaoAsync<T>(Cena s, RequestContext ator, Action<Interceptador<IConversationGateway>> ponto, Func<QueueItemService, Task<T>> operacao) =>
        s.C.ComoAsync(ator, async sp =>
        {
            var (gateway, controle) = Interceptador<IConversationGateway>.Envolver(sp.GetRequiredService<IConversationGateway>());
            ponto(controle);
            var executor = ActivatorUtilities.CreateInstance<HandlingExecutor>(sp, gateway);
            return await operacao(ActivatorUtilities.CreateInstance<QueueItemService>(sp, executor));
        });

    private static async Task ReconciliarAsync(Cena s)
    {
        await s.C.ComoAsync(s.C.ContextoDeSistema, async sp =>
        {
            await sp.GetRequiredService<IaBimeta.ContactCenter.Infrastructure.ContactCenterDbContext>().Database
                .ExecuteSqlAsync($"UPDATE contact_center.queue_items SET pending_next_attempt_at = NULL WHERE id = {s.Item.Id}");
            return 0;
        });
        Assert.Equal(1, await s.C.ReconciliarAsync());
    }

    // O atendente anterior

    [Fact]
    public async Task A_Ana_que_perdeu_a_conversa_para_a_Carla_direto_no_Conversations_nao_consegue_devolve_la_a_IA()
    {
        var s = await MontarAsync("opreturn");
        await MudarPorForaAsync(s, s.Carla.Id);
        await ItemAsync(s, QueueItemStatus.Assigned, s.Ana.Id); // a projeção local ainda diz Ana, sem intenção pendente

        // O item local autorizaria a Ana (o responsável pelo AssignedUserId); o Conversations diz que o responsável é a Carla.
        await Assert.ThrowsAsync<QueueAccessDeniedException>(() => s.C.DevolverAIAAsync(s.Item.Id, s.Ana.Contexto));

        // O atendimento da Carla não foi interrompido e nada ficou pendente.
        await EstadoAsync(s, HandlingMode.HumanAssisted, s.Carla.Id);
        await ItemAsync(s, QueueItemStatus.Assigned, s.Ana.Id);
        await NegacoesAsync(s, "contact-center.item.return-to-ai", s.Ana, 1);
    }

    [Fact]
    public async Task A_Ana_que_perdeu_a_conversa_para_a_Carla_direto_no_Conversations_nao_consegue_devolve_la_a_fila()
    {
        var s = await MontarAsync("oprelease");
        await MudarPorForaAsync(s, s.Carla.Id);

        await Assert.ThrowsAsync<QueueAccessDeniedException>(() => s.C.DevolverAFilaAsync(s.Item.Id, s.Ana.Contexto));

        // A conversa não voltou a esperar: ela não ficou aberta para os outros atendentes da fila lerem e assumirem.
        await EstadoAsync(s, HandlingMode.HumanAssisted, s.Carla.Id);
        await ItemAsync(s, QueueItemStatus.Assigned, s.Ana.Id);
        await NegacoesAsync(s, "contact-center.item.release", s.Ana, 1);
    }

    [Fact]
    public async Task A_Ana_que_perdeu_a_conversa_nao_consegue_passa_la_a_outro_atendente_nem_transferi_la_de_fila()
    {
        var s = await MontarAsync("opdemais");
        await MudarPorForaAsync(s, s.Carla.Id);

        await Assert.ThrowsAsync<QueueAccessDeniedException>(() => s.C.AtribuirAsync(s.Item.Id, s.Beto.Id, s.Ana.Contexto));
        await Assert.ThrowsAsync<QueueAccessDeniedException>(() => s.C.TransferirParaFilaAsync(s.Item.Id, s.FilaB.Id, s.Dora.Id, s.Ana.Contexto));

        await EstadoAsync(s, HandlingMode.HumanAssisted, s.Carla.Id);
        await ItemAsync(s, QueueItemStatus.Assigned, s.Ana.Id);
        await NegacoesAsync(s, "contact-center.item.assign", s.Ana, 1);
        await NegacoesAsync(s, "contact-center.item.transfer", s.Ana, 1);
    }

    // Assumir: só a conversa que o Conversations confirma que espera

    [Fact]
    public async Task Assumir_uma_conversa_localmente_em_espera_mas_ja_atribuida_no_Conversations_nao_toma_a_conversa_de_quem_a_tem()
    {
        var s = await MontarAsync("opclaim", assumida: false);
        await MudarPorForaAsync(s, s.Carla.Id);
        await ItemAsync(s, QueueItemStatus.Waiting, null); // a projeção local ainda diz "em espera"

        // O Beto é da fila e o item aparece "em espera" para ele; o Conversations já tem a Carla como responsável.
        var conflito = await Assert.ThrowsAsync<QueueItemConflictException>(() => s.C.AssumirAsync(s.Item.Id, s.Beto.Contexto));
        Assert.Equal(QueueItemConflict.AlreadyAssigned, conflito.Conflict);
        await EstadoAsync(s, HandlingMode.HumanAssisted, s.Carla.Id);

        // Pegar "a próxima da fila" também não a leva: a candidata (em espera, localmente) é a mesma.
        Assert.Null(await s.C.AssumirProximaAsync(s.FilaA.Id, s.Beto.Contexto));
        await EstadoAsync(s, HandlingMode.HumanAssisted, s.Carla.Id);

        // A própria Carla assumir é "já é sua", sem mudar nada.
        Assert.Equal(OperationOutcome.AlreadyApplied, (await s.C.AssumirAsync(s.Item.Id, s.Carla.Contexto)).Outcome);
        await EstadoAsync(s, HandlingMode.HumanAssisted, s.Carla.Id);
    }

    [Fact]
    public async Task Assumir_exige_que_o_Conversations_e_o_item_concordem_que_a_conversa_espera_e_na_divergencia_nega()
    {
        var s = await MontarAsync("opclaimlivre");
        await MudarPorForaAsync(s, null); // a Ana foi retirada por fora: no Conversations a conversa espera, mas o item local ainda diz Ana

        // As duas autoridades divergem: nenhuma amplia a permissão da outra. O Beto não assume até a projeção chegar; nada muda.
        var conflito = await Assert.ThrowsAsync<QueueItemConflictException>(() => s.C.AssumirAsync(s.Item.Id, s.Beto.Contexto));
        Assert.Equal(QueueItemConflict.AlreadyAssigned, conflito.Conflict);
        await EstadoAsync(s, HandlingMode.HumanAssisted, null);
        await ItemAsync(s, QueueItemStatus.Assigned, s.Ana.Id);
    }

    // Mudança entre a autorização e a execução

    [Fact]
    public async Task Mudanca_de_responsavel_entre_a_autorizacao_e_a_execucao_desfaz_a_operacao_sem_alterar_a_conversa_da_Carla()
    {
        var s = await MontarAsync("opaut");
        var pedido = Guid.NewGuid();
        var intercalou = false;

        // A Ana é autorizada (o Conversations a mostra como responsável); logo depois, a conversa vai para a Carla por fora.
        var recusa = await Assert.ThrowsAsync<ContactCenterRuleException>(() => NaAutorizacaoAsync(
            s, s.Ana.Contexto,
            g => g.Depois(nameof(IConversationGateway.GetSessionAsync), async () =>
            {
                await MudarPorForaAsync(s, s.Carla.Id);
                intercalou = true;
            }),
            svc => svc.ReturnToAiAsync(s.Item.Id, pedido)));

        Assert.True(intercalou);
        Assert.Equal(ContactCenterRule.ConversationChanged, recusa.Rule);
        await EstadoAsync(s, HandlingMode.HumanAssisted, s.Carla.Id); // o atendimento da Carla não foi interrompido
        await ItemAsync(s, QueueItemStatus.Assigned, s.Ana.Id); // a intenção foi desfeita
        await NegacoesAsync(s, "contact-center.item.return-to-ai", s.Ana, 1);

        // Repetir o mesmo pedido devolve a mesma recusa, sem tentar de novo.
        var repetida = await Assert.ThrowsAsync<ContactCenterRuleException>(() => s.C.DevolverAIAAsync(s.Item.Id, s.Ana.Contexto, pedido));
        Assert.Equal(ContactCenterRule.ConversationChanged, repetida.Rule);
        await EstadoAsync(s, HandlingMode.HumanAssisted, s.Carla.Id);
    }

    [Fact]
    public async Task Mudanca_de_responsavel_entre_a_consulta_do_executor_e_a_aplicacao_nao_deixa_a_versao_renovada_aplicar_a_operacao()
    {
        var s = await MontarAsync("opexec");
        var intercalou = false;

        // O executor registra a cerca e lê a sessão (a Ana é a responsável); ANTES de ele aplicar, a conversa vai para a Carla por fora. A
        // versão que ele leu fica velha: o caminho antigo relia a sessão, renovava a versão e aplicava sobre a conversa da Carla.
        var recusa = await Assert.ThrowsAsync<ContactCenterRuleException>(() => NaExecucaoAsync(
            s, s.Ana.Contexto,
            g => g.Depois(nameof(IConversationGateway.FenceAsync), async () =>
            {
                await MudarPorForaAsync(s, s.Carla.Id);
                intercalou = true;
            }),
            svc => svc.ReleaseAsync(s.Item.Id, Guid.NewGuid())));

        Assert.True(intercalou);
        Assert.Equal(ContactCenterRule.ConversationChanged, recusa.Rule);
        await EstadoAsync(s, HandlingMode.HumanAssisted, s.Carla.Id); // continua com a Carla, não voltou a esperar
        await ItemAsync(s, QueueItemStatus.Assigned, s.Ana.Id);
    }

    [Fact]
    public async Task Assumir_enquanto_a_conversa_e_atribuida_por_fora_entre_a_autorizacao_e_a_execucao_desfaz_a_operacao()
    {
        var s = await MontarAsync("opclaimcorrida", assumida: false);
        var intercalou = false;

        // O Beto é autorizado a assumir (o Conversations a mostra esperando); antes de aplicar, a Carla é a responsável.
        var recusa = await Assert.ThrowsAsync<ContactCenterRuleException>(() => NaAutorizacaoAsync(
            s, s.Beto.Contexto,
            g => g.Depois(nameof(IConversationGateway.GetSessionAsync), async () =>
            {
                await MudarPorForaAsync(s, s.Carla.Id);
                intercalou = true;
            }),
            svc => svc.ClaimAsync(s.Item.Id, Guid.NewGuid())));

        Assert.True(intercalou);
        Assert.Equal(ContactCenterRule.ConversationChanged, recusa.Rule);
        await EstadoAsync(s, HandlingMode.HumanAssisted, s.Carla.Id);
        await ItemAsync(s, QueueItemStatus.Waiting, null);
    }

    // Operações legítimas

    [Fact]
    public async Task O_responsavel_atual_opera_a_propria_conversa_em_todas_as_operacoes()
    {
        var s = await MontarAsync("oplegitima");

        // Devolve à fila.
        Assert.Equal(OperationOutcome.Completed, (await s.C.DevolverAFilaAsync(s.Item.Id, s.Ana.Contexto)).Outcome);
        await EstadoAsync(s, HandlingMode.HumanAssisted, null);
        await ItemAsync(s, QueueItemStatus.Waiting, null);

        // Devolve à IA.
        var paraIa = await OutraDaAnaAsync(s);
        Assert.Equal(OperationOutcome.Completed, (await s.C.DevolverAIAAsync(paraIa.Id, s.Ana.Contexto)).Outcome);
        await EstadoAsync(s, HandlingMode.AiAutonomous, null, paraIa.SessionId);
        Assert.Equal(QueueItemStatus.Closed, (await s.C.ItemAsync(paraIa.Id))!.Status);

        // Passa a outro atendente; o novo responsável, já efetivo nos dois módulos, opera.
        var paraCarla = await OutraDaAnaAsync(s);
        Assert.Equal(OperationOutcome.Completed, (await s.C.AtribuirAsync(paraCarla.Id, s.Carla.Id, s.Ana.Contexto)).Outcome);
        await EstadoAsync(s, HandlingMode.HumanAssisted, s.Carla.Id, paraCarla.SessionId);
        await ItemAsync(s, QueueItemStatus.Assigned, s.Carla.Id, paraCarla.Id);
        await Assert.ThrowsAsync<QueueAccessDeniedException>(() => s.C.DevolverAFilaAsync(paraCarla.Id, s.Ana.Contexto)); // a Ana já não é a responsável
        Assert.Equal(OperationOutcome.Completed, (await s.C.DevolverAFilaAsync(paraCarla.Id, s.Carla.Contexto)).Outcome);
        await EstadoAsync(s, HandlingMode.HumanAssisted, null, paraCarla.SessionId);

        // Transfere para a fila B, com a Dora.
        var paraB = await OutraDaAnaAsync(s);
        Assert.Equal(OperationOutcome.Completed, (await s.C.TransferirParaFilaAsync(paraB.Id, s.FilaB.Id, s.Dora.Id, s.Ana.Contexto)).Outcome);
        await EstadoAsync(s, HandlingMode.HumanAssisted, s.Dora.Id, paraB.SessionId);
        var naFilaB = (await s.C.ItemAsync(paraB.Id))!;
        Assert.Equal((s.FilaB.Id, s.Dora.Id), (naFilaB.QueueId, naFilaB.AssignedUserId));
    }

    [Fact]
    public async Task Depois_da_projecao_da_mudanca_externa_o_novo_responsavel_opera_e_o_anterior_continua_negado()
    {
        var s = await MontarAsync("opprojeta", mensageria: true);
        await s.C.RodarRelayAsync(); // as operações do próprio ContactCenter já foram entregues
        await MudarPorForaAsync(s, s.Carla.Id);
        await s.C.RodarRelayAsync(); // a projeção chega: o item passa a espelhar o responsável do Conversations
        await ItemAsync(s, QueueItemStatus.Assigned, s.Carla.Id);

        await Assert.ThrowsAsync<QueueAccessDeniedException>(() => s.C.DevolverAFilaAsync(s.Item.Id, s.Ana.Contexto));
        Assert.Equal(OperationOutcome.Completed, (await s.C.DevolverAFilaAsync(s.Item.Id, s.Carla.Contexto)).Outcome);
        await EstadoAsync(s, HandlingMode.HumanAssisted, null);
    }

    [Fact]
    public async Task O_supervisor_da_fila_mantem_as_permissoes_sobre_a_conversa_inclusive_quando_o_responsavel_mudou_por_fora()
    {
        var s = await MontarAsync("opsuper");

        // Sobre a conversa da Ana: passa a outro, devolve à fila, transfere e devolve à IA, como os contratos preveem.
        Assert.Equal(OperationOutcome.Completed, (await s.C.AtribuirAsync(s.Item.Id, s.Beto.Id, s.Supervisor.Contexto)).Outcome);
        await EstadoAsync(s, HandlingMode.HumanAssisted, s.Beto.Id);
        Assert.Equal(OperationOutcome.Completed, (await s.C.DevolverAFilaAsync(s.Item.Id, s.Supervisor.Contexto)).Outcome);
        await EstadoAsync(s, HandlingMode.HumanAssisted, null);

        var transferida = await OutraDaAnaAsync(s);
        Assert.Equal(OperationOutcome.Completed, (await s.C.TransferirParaFilaAsync(transferida.Id, s.FilaB.Id, s.Dora.Id, s.Supervisor.Contexto)).Outcome);
        await EstadoAsync(s, HandlingMode.HumanAssisted, s.Dora.Id, transferida.SessionId);

        // A conversa passou da Ana para a Carla por fora: o item local ainda diz Ana, e o supervisor continua podendo operar (a permissão dele
        // não depende de quem é o responsável). A autorização é sobre o responsável de agora (a Carla).
        var porFora = await OutraDaAnaAsync(s);
        await MudarPorForaAsync(s, s.Carla.Id, porFora.SessionId);
        Assert.Equal(OperationOutcome.Completed, (await s.C.DevolverAIAAsync(porFora.Id, s.Supervisor.Contexto)).Outcome);
        await EstadoAsync(s, HandlingMode.AiAutonomous, null, porFora.SessionId);
    }

    // Repetição idempotente de uma operação já confirmada

    [Fact]
    public async Task Repetir_a_devolucao_a_IA_ja_confirmada_depois_de_perder_a_resposta_conclui_sem_repetir_o_efeito()
    {
        var s = await MontarAsync("oprepete");
        var pedido = Guid.NewGuid();

        // O Conversations CONFIRMA a devolução e a resposta se perde: a sessão já está em IA, o item ainda pendente.
        var interrompida = await NaExecucaoAsync(
            s, s.Ana.Contexto, g => g.Depois(nameof(IConversationGateway.TransitionAsync), () => throw new InvalidOperationException("a resposta se perdeu")),
            svc => svc.ReturnToAiAsync(s.Item.Id, pedido));
        Assert.Equal(OperationOutcome.Pending, interrompida.Outcome);
        await EstadoAsync(s, HandlingMode.AiAutonomous, null);
        var versao = await s.C.VersaoDaSessaoAsync(s.Sessao);

        // A Ana repete o MESMO pedido: a autorização que ela tinha (sobre ela) já não vale para o estado de agora, e a repetição é reconhecida
        // mesmo assim (não é uma operação nova): conclui o item, sem mexer de novo no Conversations.
        var repetida = await s.C.DevolverAIAAsync(s.Item.Id, s.Ana.Contexto, pedido);
        Assert.Equal(OperationOutcome.Completed, repetida.Outcome);
        Assert.Equal(QueueItemStatus.Closed, (await s.C.ItemAsync(s.Item.Id))!.Status);
        Assert.Equal(versao, await s.C.VersaoDaSessaoAsync(s.Sessao));

        // E depois de terminada, repetir devolve o resultado gravado.
        Assert.Equal(QueueItemStatus.Closed, (await s.C.DevolverAIAAsync(s.Item.Id, s.Ana.Contexto, pedido)).Item.Status);
    }

    [Fact]
    public async Task O_job_conclui_a_devolucao_a_fila_confirmada_cuja_resposta_se_perdeu()
    {
        var s = await MontarAsync("opjob");

        var interrompida = await NaExecucaoAsync(
            s, s.Ana.Contexto, g => g.Depois(nameof(IConversationGateway.TransitionAsync), () => throw new InvalidOperationException("a resposta se perdeu")),
            svc => svc.ReleaseAsync(s.Item.Id, Guid.NewGuid()));
        Assert.Equal(OperationOutcome.Pending, interrompida.Outcome);
        await EstadoAsync(s, HandlingMode.HumanAssisted, null); // já esperando no Conversations

        await ReconciliarAsync(s);

        await ItemAsync(s, QueueItemStatus.Waiting, null);
        await EstadoAsync(s, HandlingMode.HumanAssisted, null);
    }

    [Fact]
    public async Task Repetir_a_reatribuicao_confirmada_nao_desfaz_a_mudanca_de_responsavel_que_veio_depois()
    {
        var s = await MontarAsync("oprepete2");
        var pedido = Guid.NewGuid();

        // O supervisor passa a conversa para a Carla; o Conversations confirma e a resposta se perde.
        var interrompida = await NaExecucaoAsync(
            s, s.Supervisor.Contexto, g => g.Depois(nameof(IConversationGateway.TransitionAsync), () => throw new InvalidOperationException("a resposta se perdeu")),
            svc => svc.AssignAsync(s.Item.Id, s.Carla.Id, pedido));
        Assert.Equal(OperationOutcome.Pending, interrompida.Outcome);

        // Antes de a repetição chegar, a conversa vai da Carla para o Beto, por fora.
        await MudarPorForaAsync(s, s.Beto.Id);

        // A repetição é reconhecida (o mesmo identificador já foi aplicado) e conclui o que foi confirmado, sem tirar a conversa do Beto.
        var repetida = await s.C.AtribuirAsync(s.Item.Id, s.Carla.Id, s.Supervisor.Contexto, pedido);
        Assert.Equal(OperationOutcome.Completed, repetida.Outcome);
        await EstadoAsync(s, HandlingMode.HumanAssisted, s.Beto.Id);
    }
}
