using IaBimeta.Audit.Contracts;
using IaBimeta.ContactCenter.Application;
using IaBimeta.ContactCenter.Contracts;
using IaBimeta.ContactCenter.Domain;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Contracts.Events;
using IaBimeta.IntegrationTests.Channels;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.ContactCenter;

/// <summary>
/// A fachada <see cref="IHandoffRequests"/> (ADR 0017, seção B; plano C1.4f) sobre o <see cref="QueueItemService"/> REAL, com PostgreSQL real: o que o Agents verá ao pedir o
/// atendimento humano. Cada teste prova o resultado do CONTRATO (desfecho, item, fila, se a recusa é transitória e o código estável) e que o ContactCenter e o Conversations
/// ficaram como o desfecho diz: a fachada traduz, e não cria regra de fila, de roteamento nem de transferência. Cobre a distinção entre a recusa transitória e a definitiva
/// (repetir o MESMO identificador depois de a causa passar) e o isolamento TEN-2 (a conversa que não existe, a de outro tenant e a que o chamador não pode gerir têm o mesmo
/// resultado). O que não é recusa de negócio (tenant ausente) continua exceção.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class HandoffRequestsIntegrationTests(PostgresFixture banco)
{
    /// <summary>Pede o atendimento humano pela fachada, como o Agents pediria: num escopo próprio, com o contexto informado (de sistema, por padrão).</summary>
    private static Task<RequestHandoffResult> PedirAsync(
        CenarioDeConversas c, Guid sessao, Guid? fila = null, Guid? pedido = null, string motivo = "ai-decision.price", RequestContext? contexto = null) =>
        c.ComoAsync(
            contexto ?? c.ContextoDeSistema,
            sp => sp.GetRequiredService<IHandoffRequests>().RequestHandoffAsync(new RequestHandoffCommand(sessao, pedido ?? Guid.NewGuid(), motivo, fila)));

    private static async Task ConversaFicouComAIAAsync(CenarioDeConversas c, Guid sessao)
    {
        var estado = (await c.SessaoAsync(sessao))!;
        Assert.Equal((HandlingMode.AiAutonomous, 1L), (estado.HandlingMode, estado.StateVersion));
        Assert.Equal(0, c.EventosDe(ConversationEventTypes.HandlingModeChanged, sessao));
    }

    /// <summary>O atendente assume DIRETO no Conversations (por fora do ContactCenter): a conversa passa a ter responsável humano.</summary>
    private static async Task AssumirPorForaAsync(CenarioDeConversas c, Guid sessao, Guid responsavel) =>
        Assert.Equal(
            TransitionOutcome.Applied,
            (await c.TransicionarAsync(sessao, HandlingMode.HumanAssisted, responsavel, await c.VersaoDaSessaoAsync(sessao))).Outcome);

    // O caminho feliz

    [Fact]
    public async Task Conversa_nova_com_fila_que_a_cobre_vira_Completed_com_o_item_e_a_fila_e_a_sessao_vai_para_humano_pendente()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "hfentra");
        var fila = await c.CriarFilaAsync();
        var sessao = await c.NovaConversaAsync();
        var pedido = Guid.NewGuid();

        var resultado = await PedirAsync(c, sessao, fila.Id, pedido, "ai-decision.price");

        // O item existe no ContactCenter, em espera, e o resultado do contrato aponta para ele e para a fila.
        var item = Assert.Single(await c.ItensAsync());
        Assert.Equal(new RequestHandoffResult(RequestHandoffOutcome.Completed, item.Id, fila.Id, false, null), resultado);
        Assert.Equal((QueueItemStatus.Waiting, fila.Id, sessao, (Guid?)null), (item.Status, item.QueueId, item.SessionId, item.AssignedUserId));
        Assert.Null(item.Pending);

        // O estado da conversa é do Conversations: humano pendente, sem responsável, com a versão avançada uma vez e um único evento.
        var estado = (await c.SessaoAsync(sessao))!;
        Assert.Equal((HandlingMode.HumanAssisted, (Guid?)null, 2L), (estado.HandlingMode, estado.ResponderRef, estado.StateVersion));
        Assert.Equal(1, c.EventosDe(ConversationEventTypes.HandlingModeChanged, sessao));

        // O histórico do item guarda o identificador do pedido e o motivo que o Agents deu.
        var historico = Assert.Single((await c.HistoricoDoItemAsync(item.Id))!);
        Assert.Equal((HistoryKind.Entered, (Guid?)null, fila.Id, pedido, "ai-decision.price"), (historico.Kind, historico.FromQueueId, historico.ToQueueId, historico.OperationId, historico.Reason));
        Assert.Equal(2L, historico.StateVersionAfter);

        // E a IA deixa de responder nessa conversa.
        Assert.Equal(BeginTurnOutcome.NotAiResponder, (await c.IniciarTurnoAsync(sessao, Guid.NewGuid())).Outcome);
    }

    [Fact]
    public async Task O_contexto_de_sistema_sem_Principal_pede_o_atendimento_e_o_historico_fica_sem_autor_usuario()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "hfsistema");
        var fila = await c.CriarFilaAsync();
        var sessao = await c.NovaConversaAsync();
        Assert.Null(c.ContextoDeSistema.Principal); // é o contexto com que o Agents chamará, no Worker

        var resultado = await PedirAsync(c, sessao, fila.Id);

        Assert.Equal(RequestHandoffOutcome.Completed, resultado.Outcome);
        var historico = Assert.Single((await c.HistoricoDoItemAsync(resultado.QueueItemId!.Value))!);
        Assert.Null(historico.ActorUserId);
    }

    [Fact]
    public async Task Quem_gere_a_conversa_na_organizacao_tambem_pede_pela_fachada_e_fica_registrado_como_autor()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "hfgerente");
        var fila = await c.CriarFilaAsync();
        var sessao = await c.NovaConversaAsync();
        var gerente = await c.NovoAgenteAsync(banco, papel: IaBimeta.Identity.Domain.Role.Manager);

        var resultado = await PedirAsync(c, sessao, fila.Id, motivo: "customer-asked", contexto: gerente.Contexto);

        Assert.Equal(RequestHandoffOutcome.Completed, resultado.Outcome);
        var historico = Assert.Single((await c.HistoricoDoItemAsync(resultado.QueueItemId!.Value))!);
        Assert.Equal((gerente.Id, "customer-asked"), (historico.ActorUserId, historico.Reason));
    }

    // Idempotência e conversa que já está em fila

    [Fact]
    public async Task Repetir_o_mesmo_RequestId_devolve_AlreadyApplied_com_o_mesmo_item_sem_segundo_item_nem_nova_transicao()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "hfrepete");
        var fila = await c.CriarFilaAsync();
        var sessao = await c.NovaConversaAsync();
        var pedido = Guid.NewGuid();

        var primeiro = await PedirAsync(c, sessao, fila.Id, pedido);
        var segundo = await PedirAsync(c, sessao, fila.Id, pedido);

        var item = Assert.Single(await c.ItensAsync());
        Assert.Equal(new RequestHandoffResult(RequestHandoffOutcome.Completed, item.Id, fila.Id, false, null), primeiro);
        Assert.Equal(new RequestHandoffResult(RequestHandoffOutcome.AlreadyApplied, item.Id, fila.Id, false, null), segundo);
        Assert.Equal(1, c.EventosDe(ConversationEventTypes.HandlingModeChanged, sessao));
        Assert.Single((await c.HistoricoDoItemAsync(item.Id))!);
        Assert.Equal(2L, await c.VersaoDaSessaoAsync(sessao));
    }

    [Fact]
    public async Task Outro_RequestId_para_a_mesma_fila_ou_sem_fila_e_repeticao_e_para_outra_fila_e_recusa_definitiva_ja_esta_em_outra_fila()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "hfoutrafila");
        var fila = await c.CriarFilaAsync("Fila 1");
        var outra = await c.CriarFilaAsync("Fila 2");
        var sessao = await c.NovaConversaAsync();
        await PedirAsync(c, sessao, fila.Id);
        var item = Assert.Single(await c.ItensAsync());

        // O comportamento REAL do QueueItemService: a conversa que já está na fila pedida (ou que não pede fila nenhuma) recebe a repetição, e só a OUTRA fila é conflito.
        var mesmaFila = await PedirAsync(c, sessao, fila.Id);
        var semFila = await PedirAsync(c, sessao);
        var outraFila = await PedirAsync(c, sessao, outra.Id);

        Assert.Equal(new RequestHandoffResult(RequestHandoffOutcome.AlreadyApplied, item.Id, fila.Id, false, null), mesmaFila);
        Assert.Equal(new RequestHandoffResult(RequestHandoffOutcome.AlreadyApplied, item.Id, fila.Id, false, null), semFila);
        Assert.Equal(new RequestHandoffResult(RequestHandoffOutcome.Refused, null, null, false, "already-in-another-queue"), outraFila);
        Assert.Single(await c.ItensAsync());
        Assert.Equal(fila.Id, (await c.ItemAsync(item.Id))!.QueueId);
    }

    [Fact]
    public async Task O_identificador_ja_usado_para_outra_conversa_e_recusa_definitiva_request_id_reused_e_nada_muda_na_segunda()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "hfreuso");
        var fila = await c.CriarFilaAsync();
        var primeira = await c.NovaConversaAsync();
        var segunda = await c.NovaConversaAsync();
        var pedido = Guid.NewGuid();
        await PedirAsync(c, primeira, fila.Id, pedido);

        var reuso = await PedirAsync(c, segunda, fila.Id, pedido);

        Assert.Equal(new RequestHandoffResult(RequestHandoffOutcome.Refused, null, null, false, "request-id-reused"), reuso);
        Assert.Equal(primeira, Assert.Single(await c.ItensAsync()).SessionId);
        await ConversaFicouComAIAAsync(c, segunda);
    }

    // Recusas por regra: transitórias e definitivas

    [Fact]
    public async Task Sessao_encerrada_e_SessionEnded_definitivo_com_o_codigo_session_closed_e_sem_item()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "hfencerrada");
        var fila = await c.CriarFilaAsync();
        var sessao = await c.NovaConversaAsync();
        await c.EncerrarSessaoAsync(sessao);

        var resultado = await PedirAsync(c, sessao, fila.Id);

        Assert.Equal(new RequestHandoffResult(RequestHandoffOutcome.SessionEnded, null, null, false, "session-closed"), resultado);
        Assert.Empty(await c.ItensAsync());
    }

    [Fact]
    public async Task Fila_pausada_e_NotRoutable_transitoria_e_o_MESMO_RequestId_passa_depois_de_retomada()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "hfpausada");
        var fila = await c.CriarFilaAsync();
        await c.PausarAsync(fila.Id);
        var sessao = await c.NovaConversaAsync();
        var pedido = Guid.NewGuid();

        var recusa = await PedirAsync(c, sessao, fila.Id, pedido);

        Assert.Equal(new RequestHandoffResult(RequestHandoffOutcome.NotRoutable, null, null, true, "queue-paused"), recusa);
        Assert.Empty(await c.ItensAsync());
        await ConversaFicouComAIAAsync(c, sessao);

        // A causa passou: a recusa não gravou recibo, então repetir com o MESMO identificador conclui (é isso que Retryable promete ao consumidor).
        await c.RetomarAsync(fila.Id);
        var depois = await PedirAsync(c, sessao, fila.Id, pedido);

        var item = Assert.Single(await c.ItensAsync());
        Assert.Equal(new RequestHandoffResult(RequestHandoffOutcome.Completed, item.Id, fila.Id, false, null), depois);
    }

    [Fact]
    public async Task Fora_do_horario_com_a_politica_recusar_e_NotRoutable_transitoria_queue_closed_e_o_MESMO_RequestId_passa_com_a_fila_aberta()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "hfforadohorario");
        var fila = await c.CriarFilaAsync();
        await c.ConfigurarFilaAsync(fila.Id, null, OutOfHoursBehavior.Reject);
        await c.DefinirHorarioAsync(fila.Id, "UTC", HandoffTestSupport.FechadoHoje());
        var sessao = await c.NovaConversaAsync();
        var pedido = Guid.NewGuid();

        var recusa = await PedirAsync(c, sessao, fila.Id, pedido);

        Assert.Equal(new RequestHandoffResult(RequestHandoffOutcome.NotRoutable, null, null, true, "queue-closed"), recusa);
        Assert.Empty(await c.ItensAsync());
        await ConversaFicouComAIAAsync(c, sessao);

        await c.LimparHorarioAsync(fila.Id);
        var depois = await PedirAsync(c, sessao, fila.Id, pedido);

        Assert.Equal(RequestHandoffOutcome.Completed, depois.Outcome);
        Assert.Single(await c.ItensAsync());
    }

    [Fact]
    public async Task Pedido_sem_fila_e_sem_regra_que_sirva_e_NotRoutable_definitivo_no_route()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "hfsemrota");
        await c.CriarFilaAsync(); // existe fila, mas nenhuma regra de roteamento leva a conversa até ela
        var sessao = await c.NovaConversaAsync();

        var resultado = await PedirAsync(c, sessao);

        Assert.Equal(new RequestHandoffResult(RequestHandoffOutcome.NotRoutable, null, null, false, "no-route"), resultado);
        Assert.Empty(await c.ItensAsync());
        await ConversaFicouComAIAAsync(c, sessao);
    }

    [Fact]
    public async Task Fila_de_outra_unidade_nao_cobre_a_conversa_e_e_NotRoutable_definitivo_queue_does_not_cover_session()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "hfcobertura");
        var matriz = await c.CriarUnidadeAsync(c.OrganizacaoId, "Matriz", matriz: true);
        var filial = await c.CriarUnidadeAsync(c.OrganizacaoId, "Filial");
        var filaDaFilial = await c.CriarFilaAsync("Filial", unidade: filial);
        var sessao = await c.NovaConversaAsync(unidade: matriz);

        var resultado = await PedirAsync(c, sessao, filaDaFilial.Id);

        Assert.Equal(new RequestHandoffResult(RequestHandoffOutcome.NotRoutable, null, null, false, "queue-does-not-cover-session"), resultado);
        Assert.Empty(await c.ItensAsync());
        await ConversaFicouComAIAAsync(c, sessao);
    }

    [Fact]
    public async Task Unidade_que_o_numero_nao_autoriza_e_NotRoutable_definitivo_unit_not_authorized_for_channel_e_a_sessao_nao_muda()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "hfunidade", canaisReais: true);
        var matriz = await c.CriarUnidadeAsync(c.OrganizacaoId, "Matriz", matriz: true);
        var filial = await c.CriarUnidadeAsync(c.OrganizacaoId, "Filial");
        var outra = await c.CriarUnidadeAsync(c.OrganizacaoId, "Outra filial");
        var canal = await c.CriarCanalAsync(new[] { matriz, filial }); // o número NÃO autoriza a "Outra filial"
        var filaDaOutra = await c.CriarFilaAsync("Outra filial", unidade: outra);
        await c.CriarRegraAsync(filaDaOutra.Id, "Só a outra filial");
        var sessao = await c.NovaConversaAsync(unidade: matriz, canal: canal);

        var resultado = await PedirAsync(c, sessao);

        Assert.Equal(new RequestHandoffResult(RequestHandoffOutcome.NotRoutable, null, null, false, "unit-not-authorized-for-channel"), resultado);
        Assert.Empty(await c.ItensAsync());
        var estado = (await c.SessaoAsync(sessao))!;
        Assert.Equal((HandlingMode.AiAutonomous, matriz, 1L), (estado.HandlingMode, estado.BusinessUnitId, estado.StateVersion));
    }

    [Fact]
    public async Task Conversa_que_ja_tem_atendente_humano_definido_fora_do_ContactCenter_e_Refused_definitiva_already_handled_by_human()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "hfjahumano");
        var fila = await c.CriarFilaAsync();
        var sessao = await c.NovaConversaAsync();
        var humano = Guid.NewGuid();
        await AssumirPorForaAsync(c, sessao, humano);

        var resultado = await PedirAsync(c, sessao, fila.Id);

        Assert.Equal(new RequestHandoffResult(RequestHandoffOutcome.Refused, null, null, false, "already-handled-by-human"), resultado);
        Assert.Empty(await c.ItensAsync());
        Assert.Equal(humano, (await c.SessaoAsync(sessao))!.ResponderRef);
    }

    [Fact]
    public async Task Conversa_que_mudou_durante_o_pedido_e_Refused_transitoria_conversation_changed_e_repetir_o_pedido_enxerga_o_humano_e_recusa_de_vez()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "hfmudou");
        var fila = await c.CriarFilaAsync();
        var sessao = await c.NovaConversaAsync();
        var carla = Guid.NewGuid();
        var pedido = Guid.NewGuid();
        var intercalou = false;

        // O pedido lê a sessão (com a IA) e é autorizado; logo depois a atendente assume a conversa por fora. A fachada é montada sobre o QueueItemService com a porta do
        // Conversations interceptada (como os testes do ContactCenter fazem), e o resultado vem do serviço REAL.
        var resultado = await c.NaAutorizacaoAsync(
            c.ContextoDeSistema,
            g => g.Depois(nameof(IConversationGateway.GetSessionAsync), async () =>
            {
                await AssumirPorForaAsync(c, sessao, carla);
                intercalou = true;
            }),
            svc => new HandoffRequests(svc).RequestHandoffAsync(new RequestHandoffCommand(sessao, pedido, "ai-decision.price", fila.Id)));

        Assert.True(intercalou);
        Assert.Equal(new RequestHandoffResult(RequestHandoffOutcome.Refused, null, null, true, "conversation-changed"), resultado);
        Assert.Empty(await c.ItensAsync());
        Assert.Equal((HandlingMode.HumanAssisted, (Guid?)carla), ((await c.SessaoAsync(sessao))!.HandlingMode, (await c.SessaoAsync(sessao))!.ResponderRef)); // a atendente continua com a conversa

        // Retentar (o MESMO identificador) agora enxerga a atendente: a causa deixou de ser transitória e a recusa é definitiva.
        var depois = await PedirAsync(c, sessao, fila.Id, pedido);

        Assert.Equal(new RequestHandoffResult(RequestHandoffOutcome.Refused, null, null, false, "already-handled-by-human"), depois);
        Assert.Empty(await c.ItensAsync());
    }

    // Argumentos inválidos

    [Fact]
    public async Task Motivo_fora_do_formato_e_RequestId_vazio_sao_Refused_invalid_request_e_nao_excecao_e_nada_e_gravado()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "hfinvalido");
        var fila = await c.CriarFilaAsync();
        var sessao = await c.NovaConversaAsync();
        var esperado = new RequestHandoffResult(RequestHandoffOutcome.Refused, null, null, false, "invalid-request");

        Assert.Equal(esperado, await PedirAsync(c, sessao, fila.Id, motivo: "Texto livre com espaços"));
        Assert.Equal(esperado, await PedirAsync(c, sessao, fila.Id, motivo: string.Empty));
        Assert.Equal(esperado, await PedirAsync(c, sessao, fila.Id, motivo: "-comeca-com-hifen"));
        Assert.Equal(esperado, await PedirAsync(c, sessao, fila.Id, motivo: new string('a', 101)));
        Assert.Equal(esperado, await PedirAsync(c, sessao, fila.Id, Guid.Empty));

        Assert.Empty(await c.ItensAsync());
        await ConversaFicouComAIAAsync(c, sessao);

        // Um motivo no limite (100 caracteres, no formato) é aceito.
        Assert.Equal(RequestHandoffOutcome.Completed, (await PedirAsync(c, sessao, fila.Id, motivo: new string('a', 100))).Outcome);
    }

    // TEN-2 e contexto

    [Fact]
    public async Task A_conversa_de_outro_tenant_a_inexistente_e_a_que_o_chamador_nao_pode_gerir_tem_exatamente_o_mesmo_resultado_session_unavailable()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "hften2a");
        var outro = await CenarioDeConversas.CriarAsync(banco, "hften2b");
        var fila = await c.CriarFilaAsync();
        var sessao = await c.NovaConversaAsync();
        var semLeitura = c.Usuario(c.OrganizacaoId, Permission.OrganizationRead, Permission.QueueRead);
        var soLeitura = c.Usuario(c.OrganizacaoId, Permission.OrganizationRead, Permission.QueueRead, Permission.ConversationRead);
        var esperado = new RequestHandoffResult(RequestHandoffOutcome.Refused, null, null, false, "session-unavailable");

        var deOutroTenant = await PedirAsync(outro, sessao); // o sistema do OUTRO tenant não alcança a sessão deste
        var inexistente = await PedirAsync(c, Guid.NewGuid());
        var naoEnxerga = await PedirAsync(c, sessao, fila.Id, contexto: semLeitura); // o ContactCenter responde "inexistente" a quem não lê a conversa
        var enxergaESemPermissao = await PedirAsync(c, sessao, fila.Id, contexto: soLeitura); // quem lê e não gere é negado (e auditado), mas o contrato não diferencia

        // Nem o desfecho, nem o código, nem a classificação revelam qual das quatro situações ocorreu.
        Assert.Equal(esperado, deOutroTenant);
        Assert.Equal(esperado, inexistente);
        Assert.Equal(esperado, naoEnxerga);
        Assert.Equal(esperado, enxergaESemPermissao);

        // E nada foi gravado em nenhum dos dois tenants.
        Assert.Empty(await c.ItensAsync());
        Assert.Empty(await outro.ItensAsync());
        await ConversaFicouComAIAAsync(c, sessao);

        // A negação de quem lia e não podia gerir ficou na trilha de auditoria do tenant dono (interna, fora do contrato).
        var negada = Assert.Single(await c.AuditoriaAsync("contact-center.item.enter", sessao.ToString()));
        Assert.Equal((AuditOutcome.Denied, soLeitura.Principal!.UserId), (negada.Outcome, negada.ActorUserId));
    }

    [Fact]
    public async Task Fila_inexistente_ou_de_outra_organizacao_tambem_e_session_unavailable_sem_revelar_qual_dos_dois()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "hften2fila");
        var outraOrganizacao = await banco.CriarOrganizacaoAsync(c.Tenant, "Outra organização");
        var filaDaOutra = await c.CriarFilaAsync("Fila da outra", organizacao: outraOrganizacao.Id);
        var sessao = await c.NovaConversaAsync();
        var esperado = new RequestHandoffResult(RequestHandoffOutcome.Refused, null, null, false, "session-unavailable");

        Assert.Equal(esperado, await PedirAsync(c, sessao, filaDaOutra.Id));
        Assert.Equal(esperado, await PedirAsync(c, sessao, Guid.NewGuid()));
        Assert.Empty(await c.ItensAsync());
        await ConversaFicouComAIAAsync(c, sessao);
    }

    [Fact]
    public async Task Sem_contexto_de_tenant_continua_excecao_e_nunca_vira_resultado()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "hfsemctx");
        var fila = await c.CriarFilaAsync();
        var sessao = await c.NovaConversaAsync();

        await Assert.ThrowsAsync<TenantContextRequiredException>(() => c.ComoAsync(
            null, sp => sp.GetRequiredService<IHandoffRequests>().RequestHandoffAsync(new RequestHandoffCommand(sessao, Guid.NewGuid(), "ai-decision.price", fila.Id))));

        Assert.Empty(await c.ItensAsync());
        await ConversaFicouComAIAAsync(c, sessao);
    }
}
