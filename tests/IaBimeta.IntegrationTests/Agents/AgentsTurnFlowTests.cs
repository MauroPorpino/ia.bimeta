using IaBimeta.Agents.Application;
using IaBimeta.Agents.Domain;
using IaBimeta.AiGateway.Contracts;
using IaBimeta.AiGateway.Domain;
using IaBimeta.AiGateway.Infrastructure;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.IntegrationTests.ContactCenter;
using IaBimeta.IntegrationTests.Messaging;
using IaBimeta.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Agents;

/// <summary>
/// O fluxo do turno de IA com PostgreSQL real (ADR 0017, seção G; CONVERSATION-LIFECYCLE.md): a mensagem do cliente vira um turno agendado pelo handler (que nunca chama o modelo), o executor o roda
/// depois do agrupamento, o modelo SUGERE e o Conversations decide pela aceitação da resposta (as cercas). O único dublê é o provedor de IA, sem rede e sem chave real.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AgentsTurnFlowTests(PostgresFixture banco)
{
    // O handler só grava

    [Fact]
    public async Task O_handler_do_evento_so_grava_o_turno_e_nunca_chama_o_modelo_nem_o_executor_leva_o_turno_antes_da_janela_quieta()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agh1");
        var entrada = await a.EntrarAsync("mauro", "Preciso de jejum para o exame A?");

        var relay = await a.RelayAsync();

        Assert.True(relay.Processed >= 1);
        var turno = await a.UnicoTurnoAsync(entrada.SessionId);
        Assert.Equal((AiTurnState.Scheduled, 0, 0), (turno.State, turno.Attempts, turno.SupersedeCount));
        Assert.Equal(0, a.ChamadasAoModelo);

        // Dentro da janela quieta (3 s) o turno ainda não está devido, e o executor não leva nada.
        Assert.Empty(await a.ExecutarAsync());
        Assert.Equal(0, a.ChamadasAoModelo);
    }

    // O caminho feliz: WhatsApp (simulado) para a IA, a resposta aceita e o consumo registrado

    [Fact]
    public async Task Mensagem_de_texto_vira_uma_resposta_da_IA_aceita_pelo_Conversations_com_o_contexto_certo_e_o_consumo_registrado()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agfeliz");
        a.Roteiro = _ => a.Sucesso(CenarioDeAgentes.Responder("Em geral não é necessário jejum para o hemograma."));
        var entrada = await a.EntrarAsync("mauro", "Preciso de jejum para o hemograma?");
        await a.RelayAsync();

        a.Avancar(TimeSpan.FromSeconds(4));
        var resultado = Assert.Single(await a.ExecutarAsync());

        Assert.Equal(TurnRunOutcome.Replied, resultado.Outcome);

        // A resposta foi aceita pelo Conversations, cobre a mensagem e saiu para o canal (o evento de saída existe).
        var resposta = Assert.Single(await a.RespostasDaIAAsync(entrada.SessionId));
        Assert.Equal(("Em geral não é necessário jejum para o hemograma.", ReplyKind.Generated), (resposta.Text, resposta.ReplyKind));
        var sessao = (await a.C.SessaoAsync(entrada.SessionId))!;
        Assert.Equal((HandlingMode.AiAutonomous, entrada.InboundSeq), (sessao.HandlingMode, sessao.AnsweredThroughSeq));
        Assert.Contains(await a.C.LinhasDoOutboxDaSessaoAsync(entrada.SessionId), o => o.EventType == "conversations.outbound-message-requested");

        // O modelo viu a instrução do tenant, o FAQ e a mensagem do cliente (só dele), em saída estruturada, com a credencial DESTE tenant.
        var pedido = Assert.Single(a.PedidosAoModelo);
        Assert.Contains(a.CanarioDasInstrucoes, pedido.SystemInstruction);
        Assert.Contains("Preciso de jejum para o hemograma?", pedido.Messages[^1].Text);
        Assert.Equal((AiMessageRole.User, AiOutputMode.StructuredJson, a.Canario), (pedido.Messages[^1].Role, pedido.OutputMode, pedido.Credential.Secret));
        Assert.Equal((await a.UnicoTurnoAsync(entrada.SessionId)).Id.ToString(), pedido.CorrelationId);

        // O turno terminou, e o consumo ficou registrado no AiGateway (sem prompt nem resposta).
        var turno = await a.UnicoTurnoAsync(entrada.SessionId);
        Assert.Equal((AiTurnState.Completed, "replied"), (turno.State, turno.EndReason));
        var registro = Assert.Single(await a.C.ComoAsync(a.C.ContextoDeSistema, sp => sp.GetRequiredService<AiGatewayDbContext>().Requests.AsNoTracking().ToListAsync()));
        Assert.Equal((AiRequestState.Confirmed, turno.Id.ToString()), (registro.State, registro.CorrelationId));

        // Nada mais devido: o executor não repete a resposta.
        a.Avancar(TimeSpan.FromMinutes(5));
        Assert.Empty(await a.ExecutarAsync());
        Assert.Single(await a.RespostasDaIAAsync(entrada.SessionId));
    }

    // Idempotência

    [Fact]
    public async Task O_mesmo_evento_entregue_duas_vezes_gera_um_unico_turno_e_uma_unica_resposta()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agdup");
        var entrada = await a.EntrarAsync("mauro", "Oi, tudo bem?");
        await a.RelayAsync();
        var evento = Assert.Single(await a.C.LinhasDoOutboxDaSessaoAsync(entrada.SessionId), o => o.EventType == "conversations.inbound-message-registered");

        // O relay entrega pelo menos uma vez: o mesmo evento volta (a linha do outbox é recolocada como pendente).
        await a.C.AlterarOutboxAsync(evento.Id, o =>
        {
            o.State = OutboxState.Pending;
            o.ProcessedAt = null;
            o.Attempts = 0;
            o.NextAttemptAt = a.Relogio.GetUtcNow();
        });
        await a.RelayAsync();

        Assert.Single(await a.TurnosAsync(entrada.SessionId));
        a.Avancar(TimeSpan.FromSeconds(4));
        Assert.Single(await a.ExecutarAsync());
        Assert.Single(await a.RespostasDaIAAsync(entrada.SessionId));
        Assert.Equal(1, a.ChamadasAoModelo);
    }

    // O teste de aceitação Mauro e Pedro, com a IA

    [Fact]
    public async Task Mauro_e_Pedro_a_requisicao_de_Mauro_so_tem_o_contexto_do_exame_A_e_a_resposta_so_vai_para_o_atendimento_dele()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agmp");
        a.Roteiro = p => a.Sucesso(CenarioDeAgentes.Responder("Resposta sobre: " + p.Messages[^1].Text));

        var mauro = await a.EntrarAsync("mauro", "Qual o jejum do exame A?");
        var pedro = await a.EntrarAsync("pedro", "Qual o preço do exame B?");
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));
        Assert.Equal(2, (await a.ExecutarAsync()).Count(r => r.Outcome == TurnRunOutcome.Replied));

        // Mauro pergunta de novo, sem dizer qual exame: o contexto dele é o do exame A.
        await a.EntrarAsync("mauro", "Preciso ficar em jejum para esse exame?");
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));
        var terceira = Assert.Single(await a.ExecutarAsync());
        Assert.Equal(TurnRunOutcome.Replied, terceira.Outcome);

        var pedidos = a.PedidosAoModelo;
        Assert.Equal(3, pedidos.Count);
        var pedidoDeMauro = pedidos[2];
        var tudoDeMauro = string.Join("\n", pedidoDeMauro.Messages.Select(m => m.Text)) + pedidoDeMauro.SystemInstruction;
        Assert.Contains("exame A", tudoDeMauro);
        Assert.Contains("esse exame", tudoDeMauro);
        Assert.DoesNotContain("exame B", tudoDeMauro);

        // A camada base do prompt (que vai em TODO pedido) fala de preço em geral; o contexto da CONVERSA de Mauro, não.
        var soAConversaDeMauro = string.Join("\n", pedidoDeMauro.Messages.Select(m => m.Text));
        Assert.DoesNotContain("preço", soAConversaDeMauro);
        Assert.DoesNotContain("Pedro", soAConversaDeMauro);

        // A resposta de Mauro vai só ao atendimento dele; o de Pedro tem uma resposta só, sobre o exame B.
        var deMauro = await a.RespostasDaIAAsync(mauro.SessionId);
        Assert.Equal(2, deMauro.Count);
        Assert.Equal("Resposta sobre: Preciso ficar em jejum para esse exame?", deMauro[1].Text);
        var dePedro = Assert.Single(await a.RespostasDaIAAsync(pedro.SessionId));
        Assert.Equal("Resposta sobre: Qual o preço do exame B?", dePedro.Text);
        Assert.NotEqual(mauro.SessionId, pedro.SessionId);
    }

    // Mensagem nova com a chamada em voo (interrupção e cerca de entrada)

    [Fact]
    public async Task Mensagem_nova_durante_a_chamada_com_o_handler_rodado_troca_o_turno_e_uma_unica_resposta_cobre_as_duas()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "aginter");
        a.RoteiroAssincrono = async p =>
        {
            if (a.ChamadasAoModelo == 1)
            {
                // No meio da chamada chega outra mensagem, e o relay entrega o evento (T6: o turno em andamento é trocado pelo sucessor).
                await a.EntrarAsync("mauro", "Tenho outra dúvida");
                await a.RelayAsync();
            }

            return a.Sucesso(CenarioDeAgentes.Responder("resposta " + a.ChamadasAoModelo));
        };
        var primeira = await a.EntrarAsync("mauro", "Oi");
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));

        var primeiraRodada = Assert.Single(await a.ExecutarAsync());

        // O turno antigo perdeu a vez: nada foi registrado, e o sucessor espera a janela quieta.
        Assert.Equal(TurnRunOutcome.Lost, primeiraRodada.Outcome);
        Assert.Empty(await a.RespostasDaIAAsync(primeira.SessionId));
        var turnos = await a.TurnosAsync(primeira.SessionId);
        Assert.Equal(new[] { AiTurnState.Superseded, AiTurnState.Scheduled }, turnos.Select(t => t.State).ToArray());
        Assert.Equal(1, turnos[1].SupersedeCount);

        a.Avancar(TimeSpan.FromSeconds(4));
        Assert.Equal(TurnRunOutcome.Replied, Assert.Single(await a.ExecutarAsync()).Outcome);

        // Uma só resposta, que cobre as duas mensagens: o modelo viu as duas juntas na segunda chamada.
        var resposta = Assert.Single(await a.RespostasDaIAAsync(primeira.SessionId));
        Assert.Equal("resposta 2", resposta.Text);
        var segunda = a.PedidosAoModelo[1];
        Assert.Contains("Oi", segunda.Messages[^1].Text);
        Assert.Contains("Tenho outra dúvida", segunda.Messages[^1].Text);
        Assert.Equal(2, a.ChamadasAoModelo);
        Assert.Equal(AiTurnState.Completed, (await a.TurnosAsync(primeira.SessionId))[1].State);
    }

    [Fact]
    public async Task Mensagem_nova_antes_de_o_handler_rodar_a_resposta_defasada_e_recusada_o_sucessor_cobre_e_o_evento_tardio_nao_chama_o_modelo()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agstale");
        a.RoteiroAssincrono = async p =>
        {
            if (a.ChamadasAoModelo == 1)
            {
                // Chega a mensagem, mas o evento ainda não foi entregue: só a cerca de entrada do Conversations sabe (StaleInput).
                await a.EntrarAsync("mauro", "Mais uma coisa");
            }

            return a.Sucesso(CenarioDeAgentes.Responder("resposta " + a.ChamadasAoModelo));
        };
        var primeira = await a.EntrarAsync("mauro", "Primeira pergunta");
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));

        var recusada = Assert.Single(await a.ExecutarAsync());

        // A resposta defasada foi SEMPRE recusada (nunca enviada); o turno foi descartado e o sucessor nasceu devido já.
        Assert.Equal((TurnRunOutcome.Discarded, "stale-input"), (recusada.Outcome, recusada.Code));
        Assert.Empty(await a.RespostasDaIAAsync(primeira.SessionId));
        var cobre = Assert.Single(await a.ExecutarAsync());
        Assert.Equal(TurnRunOutcome.Replied, cobre.Outcome);
        Assert.Equal("resposta 2", Assert.Single(await a.RespostasDaIAAsync(primeira.SessionId)).Text);
        var viu = a.PedidosAoModelo[1].Messages[^1].Text;
        Assert.Contains("Primeira pergunta", viu);
        Assert.Contains("Mais uma coisa", viu);

        // O evento da segunda mensagem chega tarde: o turno novo descobre pelo snapshot que não há nada pendente e NÃO chama o modelo (CON-9).
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));
        var tardio = Assert.Single(await a.ExecutarAsync());
        Assert.Equal((TurnRunOutcome.Discarded, "nothing-pending"), (tardio.Outcome, tardio.Code));
        Assert.Equal(2, a.ChamadasAoModelo);
        Assert.Single(await a.RespostasDaIAAsync(primeira.SessionId));
    }

    // O humano assume: nenhuma resposta automática depois

    [Fact]
    public async Task Humano_assume_com_a_chamada_em_voo_a_resposta_da_IA_e_recusada_e_nada_sai()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "aghum1");
        var sessao = Guid.Empty;
        a.RoteiroAssincrono = async _ =>
        {
            // O atendente assume (a fila, no ContactCenter, ou um usuário): o estado muda no Conversations enquanto o modelo ainda pensa.
            await a.C.TransicionarAsync(sessao, HandlingMode.HumanAssisted, null, await a.C.VersaoDaSessaoAsync(sessao));
            return a.Sucesso(CenarioDeAgentes.Responder("resposta que NÃO pode sair"));
        };
        var entrada = await a.EntrarAsync("mauro", "Preciso de ajuda");
        sessao = entrada.SessionId;
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));

        var resultado = Assert.Single(await a.ExecutarAsync());

        Assert.Equal(TurnRunOutcome.Discarded, resultado.Outcome);
        Assert.Empty(await a.RespostasDaIAAsync(sessao));
        Assert.Equal(HandlingMode.HumanAssisted, (await a.C.SessaoAsync(sessao))!.HandlingMode);

        // Quando o evento da transição chega, não há turno aberto para cancelar e a IA continua sem responder.
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromMinutes(1));
        Assert.Empty(await a.ExecutarAsync());
        Assert.Equal(1, a.ChamadasAoModelo);
    }

    [Fact]
    public async Task Humano_assume_e_o_evento_cancela_o_turno_em_andamento_o_executor_sai_sem_registrar_nada()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "aghum2");
        var sessao = Guid.Empty;
        a.RoteiroAssincrono = async _ =>
        {
            await a.C.TransicionarAsync(sessao, HandlingMode.HumanAssisted, null, await a.C.VersaoDaSessaoAsync(sessao));
            await a.RelayAsync();
            return a.Sucesso(CenarioDeAgentes.Responder("resposta que NÃO pode sair"));
        };
        var entrada = await a.EntrarAsync("mauro", "Preciso de ajuda");
        sessao = entrada.SessionId;
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));

        var resultado = Assert.Single(await a.ExecutarAsync());

        Assert.Equal(TurnRunOutcome.Lost, resultado.Outcome);
        Assert.Empty(await a.RespostasDaIAAsync(sessao));
        var turno = await a.UnicoTurnoAsync(sessao);
        Assert.Equal((AiTurnState.Cancelled, "mode-changed"), (turno.State, turno.EndReason));
    }

    [Fact]
    public async Task Com_o_humano_no_comando_a_mensagem_nova_abre_um_turno_que_e_cancelado_sem_chamar_o_modelo()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "aghum3");
        var entrada = await a.EntrarAsync("mauro", "Oi", modoInicial: HandlingMode.HumanAssisted);
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));

        var resultado = Assert.Single(await a.ExecutarAsync());

        Assert.Equal((TurnRunOutcome.Cancelled, "not-ai-responder"), (resultado.Outcome, resultado.Code));
        Assert.Equal(0, a.ChamadasAoModelo);
        Assert.Empty(await a.RespostasDaIAAsync(entrada.SessionId));
    }

    [Fact]
    public async Task Sessao_encerrada_cancela_o_turno_aberto_e_nada_e_respondido()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agfecha");
        var entrada = await a.EntrarAsync("mauro", "Oi");
        await a.RelayAsync();

        await a.C.EncerrarSessaoAsync(entrada.SessionId);
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));

        var turno = await a.UnicoTurnoAsync(entrada.SessionId);
        Assert.Equal((AiTurnState.Cancelled, "session-closed"), (turno.State, turno.EndReason));
        Assert.Empty(await a.ExecutarAsync());
        Assert.Equal(0, a.ChamadasAoModelo);
    }

    // Processo morto

    [Fact]
    public async Task Processo_morto_no_meio_do_turno_o_lease_vence_o_turno_e_reivindicado_de_novo_e_so_uma_resposta_sai()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agmorto");
        var entrada = await a.EntrarAsync("mauro", "Oi");
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));

        // Um executor reivindica o turno e "morre": nada roda.
        var reivindicados = await a.ReivindicarAsync();
        Assert.Single(reivindicados);
        Assert.Equal(AiTurnState.Running, (await a.UnicoTurnoAsync(entrada.SessionId)).State);

        // Com o lease ainda vigente, ninguém mais o leva (e o tenant não passa do teto).
        Assert.Empty(await a.ExecutarAsync());

        a.Avancar(TimeSpan.FromSeconds(91));
        var retomado = Assert.Single(await a.ExecutarAsync());

        Assert.Equal(TurnRunOutcome.Replied, retomado.Outcome);
        Assert.Equal(1, (await a.UnicoTurnoAsync(entrada.SessionId)).Attempts); // o lease perdido gasta uma tentativa
        Assert.Single(await a.RespostasDaIAAsync(entrada.SessionId));
        Assert.Empty(await a.ExecutarAsync());
        Assert.Equal(1, a.ChamadasAoModelo);
    }

    [Fact]
    public async Task Processo_morto_depois_de_a_resposta_ser_aceita_o_turno_retomado_nao_responde_de_novo()
    {
        var a = await CenarioDeAgentes.CriarAsync(banco, "agmorto2");
        var entrada = await a.EntrarAsync("mauro", "Oi");
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(4));

        // O executor chegou a registrar a resposta no Conversations e morreu antes de marcar o turno como concluído.
        var turnoReivindicado = Assert.Single(await a.TurnosAsync(entrada.SessionId));
        var reivindicado = Assert.Single(await a.ReivindicarAsync());
        var linha = await a.UnicoTurnoAsync(entrada.SessionId);
        var inicio = await a.C.IniciarTurnoAsync(entrada.SessionId, reivindicado, linha.LaneToken!.Value);
        var registro = await a.C.ResponderAsync(entrada.SessionId, reivindicado, inicio, entrada.ConversationId, "Resposta que já tinha sido aceita");
        Assert.Equal(RegisterReplyOutcome.Accepted, registro.Outcome);
        Assert.Equal(turnoReivindicado.Id, reivindicado);

        a.Avancar(TimeSpan.FromSeconds(91));
        var retomado = Assert.Single(await a.ExecutarAsync());

        // O snapshot mostra que não há nada pendente: não chama o modelo e não responde de novo.
        Assert.Equal((TurnRunOutcome.Discarded, "nothing-pending"), (retomado.Outcome, retomado.Code));
        Assert.Single(await a.RespostasDaIAAsync(entrada.SessionId));
        Assert.Equal(0, a.ChamadasAoModelo);
    }
}
