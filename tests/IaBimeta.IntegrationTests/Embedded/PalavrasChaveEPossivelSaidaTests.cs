using System.Text.Json;
using IaBimeta.Agents.Domain;
using IaBimeta.Conversations.Application;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Contracts.Events;
using IaBimeta.Conversations.Contracts.Queries;
using IaBimeta.Conversations.Infrastructure;
using IaBimeta.IntegrationTests.Agents;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.IntegrationTests.Messaging;
using IaBimeta.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IaBimeta.IntegrationTests.Embedded;

/// <summary>
/// E2.5 (ADR 0041, seção 5; D3): o reconhecimento determinístico de PARAR, SAIR e CANCELAR NA INGESTÃO, com PostgreSQL real e a composição completa de mensageria e Agents (o único dublê é o provedor de IA). Prova a decisão
/// na transação da mensagem (revogação ou ocorrência de possível saída, auditoria e eventos juntos), a supressão da resposta automática, a idempotência na reentrega e na concorrência, a falha transacional, o isolamento por
/// organização e tenant, a resolução posterior pela estrutura da E2.4 e a sonda de operação.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PalavrasChaveEPossivelSaidaTests(PostgresFixture banco)
{
    private const string Fone = "5511999990505";

    private enum Saida
    {
        Nenhuma,
        IA,
        HumanoLivre,
        HumanoModelo,
        FluxoTextoLivre,
        FluxoSemTextoLivre,
    }

    // ------------------------------------------------------------------ apoio

    private sealed record Linha(long Seq, Guid Org, string Fone, short Finalidade, short Situacao, short Origem, string Evidencia, short AtorTipo, Guid? AtorId, Guid? Sessao, Guid? Mensagem);

    private static async Task<List<Linha>> ConsentimentosAsync(Guid tenant)
    {
        await using var conexao = new NpgsqlConnection(TestDatabase.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = new NpgsqlCommand(
            "select seq, organization_id, phone, purpose, status, origin, evidence, actor_kind, actor_id, session_id, source_message_id from conversations.contact_consents where tenant_id = @t order by seq", conexao);
        comando.Parameters.AddWithValue("t", tenant);
        var linhas = new List<Linha>();
        await using var leitor = await comando.ExecuteReaderAsync();
        while (await leitor.ReadAsync())
        {
            linhas.Add(new Linha(
                leitor.GetInt64(0), leitor.GetGuid(1), leitor.GetString(2), leitor.GetInt16(3), leitor.GetInt16(4), leitor.GetInt16(5), leitor.GetString(6), leitor.GetInt16(7),
                leitor.IsDBNull(8) ? null : leitor.GetGuid(8), leitor.IsDBNull(9) ? null : leitor.GetGuid(9), leitor.IsDBNull(10) ? null : leitor.GetGuid(10)));
        }

        return linhas;
    }

    private sealed record Ocorrencia(Guid Id, Guid Org, Guid Sessao, Guid Mensagem, short Motivo, string? Palavra, short? Decisao);

    private static async Task<List<Ocorrencia>> OcorrenciasAsync(Guid tenant)
    {
        await using var conexao = new NpgsqlConnection(TestDatabase.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = new NpgsqlCommand("select id, organization_id, session_id, message_id, reason, keyword, resolution from conversations.optout_reviews where tenant_id = @t order by opened_at, id", conexao);
        comando.Parameters.AddWithValue("t", tenant);
        var linhas = new List<Ocorrencia>();
        await using var leitor = await comando.ExecuteReaderAsync();
        while (await leitor.ReadAsync())
        {
            linhas.Add(new Ocorrencia(leitor.GetGuid(0), leitor.GetGuid(1), leitor.GetGuid(2), leitor.GetGuid(3), leitor.GetInt16(4), leitor.IsDBNull(5) ? null : leitor.GetString(5), leitor.IsDBNull(6) ? null : leitor.GetInt16(6)));
        }

        return linhas;
    }

    private static async Task<long> EscalarAsync(string sql, params (string Nome, object Valor)[] parametros)
    {
        await using var conexao = new NpgsqlConnection(TestDatabase.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = new NpgsqlCommand(sql, conexao);
        foreach (var (nome, valor) in parametros)
        {
            comando.Parameters.AddWithValue(nome, valor);
        }

        return Convert.ToInt64(await comando.ExecuteScalarAsync());
    }

    private static Task<long> MensagensDeEntradaAsync(Guid tenant) => EscalarAsync("select count(*) from conversations.messages where tenant_id = @t and direction = 1", ("t", tenant));

    /// <summary>As operações dos pedidos de auditoria do módulo no outbox do tenant, na ordem de gravação.</summary>
    private static async Task<List<string>> OperacoesAuditadasAsync(CenarioDeConversas c)
    {
        var linhas = await c.LinhasDoOutboxDoTenantAsync();
        return linhas.Where(o => o.EventType == ConversationAuditEvents.AuditRequested).Select(o => Texto(o.Payload, "operation")).ToList();
    }

    private static string Texto(string json, string nome)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject().First(p => string.Equals(p.Name, nome, StringComparison.OrdinalIgnoreCase)).Value.GetString()!;
    }

    private static bool Booleano(string json, string nome)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject().First(p => string.Equals(p.Name, nome, StringComparison.OrdinalIgnoreCase)).Value.GetBoolean();
    }

    private static int Inteiro(string json, string nome)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject().First(p => string.Equals(p.Name, nome, StringComparison.OrdinalIgnoreCase)).Value.GetInt32();
    }

    /// <summary>O evento de entrada de uma mensagem específica, e o que ele diz de comando de saída.</summary>
    private static async Task<bool> EhComandoDeSaidaAsync(CenarioDeConversas c, Guid sessao, Guid mensagem)
    {
        var evento = (await c.LinhasDoOutboxDaSessaoAsync(sessao)).Single(o => o.EventType == ConversationEventTypes.InboundMessageRegistered && Texto(o.Payload, "messageId") == mensagem.ToString());
        return Booleano(evento.Payload, "isOptOutCommand");
    }

    private static async Task<List<string>> AtencoesAsync(CenarioDeConversas c, Guid sessao) =>
        (await c.LinhasDoOutboxDaSessaoAsync(sessao)).Where(o => o.EventType == ConversationEventTypes.SessionAttentionRaised).Select(o => Texto(o.Payload, "reason")).ToList();

    private static Task<SessionView> LerSessaoAsync(CenarioDeConversas c, Guid sessao) =>
        c.ComoAsync(c.ContextoDeSistema, async sp => (await sp.GetRequiredService<IConversationQueries>().GetSessionAsync(sessao))!);

    /// <summary>Cria a sessão com uma primeira entrada e a última SAÍDA pedida (IA, humano livre, humano por modelo, ou fluxo com ou sem pergunta de texto livre).</summary>
    private static async Task<IngestInboundMessageResult> PrepararAsync(CenarioDeAgentes a, string contato, Saida saida)
    {
        var entrada = await a.C.EntrarAsync(contato, "Olá, preciso de ajuda");
        var sessao = entrada.SessionId;
        switch (saida)
        {
            case Saida.Nenhuma:
                break;

            case Saida.IA:
                {
                    var turno = Guid.NewGuid();
                    var inicio = await a.C.IniciarTurnoAsync(sessao, turno);
                    Assert.Equal(RegisterReplyOutcome.Accepted, (await a.C.ResponderAsync(sessao, turno, inicio, entrada.ConversationId, "Como posso ajudar? Deseja cancelar seu pedido?")).Outcome);
                    break;
                }

            case Saida.FluxoTextoLivre:
            case Saida.FluxoSemTextoLivre:
                {
                    var turno = Guid.NewGuid();
                    var inicio = await a.C.IniciarTurnoAsync(sessao, turno);
                    var grant = inicio.Grant!;
                    var registro = await a.C.ComoAsync(
                        a.C.ContextoDeSistema,
                        sp => sp.GetRequiredService<IFlowCommands>().RegisterFlowMessageAsync(
                            new RegisterFlowMessageCommand(sessao, turno, grant.Epoch, grant.StateVersion, grant.BasedOnInboundSeq, entrada.ConversationId, "Qual é o seu nome completo?", true, saida == Saida.FluxoTextoLivre)));
                    Assert.Equal(RegisterReplyOutcome.Accepted, registro.Outcome);
                    break;
                }

            default:
                {
                    var autor = Guid.NewGuid();
                    var antes = await LerSessaoAsync(a.C, sessao);
                    Assert.Equal(TransitionOutcome.Applied, (await a.C.TransicionarAsync(sessao, HandlingMode.HumanAssisted, autor, antes.StateVersion)).Outcome);
                    var s = await LerSessaoAsync(a.C, sessao);
                    ChannelData? bloco = saida == Saida.HumanoModelo
                        ? new ChannelData(1, new Dictionary<string, string> { ["tpl.id"] = "0197a1b2-0000-7000-8000-000000000001", ["tpl.name"] = "campanha_vacina", ["tpl.lang"] = "pt_BR" })
                        : null;
                    var resultado = await a.C.ComoAsync(
                        a.C.ContextoDeSistema,
                        sp => sp.GetRequiredService<IHandlingCommands>().RegisterHumanReplyAsync(new RegisterHumanReplyCommand(
                            sessao, Guid.NewGuid(), autor, new ReplyPrecondition(s.HandlingPeriod, s.PlacementEpoch, ResponderMatch.Exact, autor), s.StateVersion, 0, new ReplyContent("Posso encerrar o atendimento?", null, bloco))));
                    Assert.Equal(RegisterHumanReplyOutcome.Applied, resultado.Outcome);
                    break;
                }
        }

        return entrada;
    }

    /// <summary>Roda o que o Worker rodaria: entrega os eventos, deixa passar a janela de agrupamento e executa os turnos. Devolve os turnos e as chamadas ao modelo até agora.</summary>
    private static async Task<(int Turnos, int Chamadas)> ProcessarAsync(CenarioDeAgentes a, Guid sessao)
    {
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(5));
        await a.ExecutarAsync();
        await a.RelayAsync();
        return ((await a.TurnosAsync(sessao)).Count, a.ChamadasAoModelo);
    }

    private static Task<CenarioDeAgentes> CenaAsync(PostgresFixture banco, string prefixo) => CenarioDeAgentes.CriarAsync(banco, prefixo);

    // ------------------------------------------------------------------ comando confirmado: revoga, audita, suprime

    [Fact]
    public async Task PARAR_depois_de_IA_e_confirmado_revoga_todas_as_finalidades_na_transacao_audita_e_suprime_a_resposta_automatica()
    {
        var a = await CenaAsync(banco, "e25-parar-ia");
        var entrada = await PrepararAsync(a, Fone, Saida.IA);
        var (turnosAntes, chamadasAntes) = await ProcessarAsync(a, entrada.SessionId);

        var parar = await a.C.EntrarAsync(Fone, "PARAR");

        Assert.Equal(IngestOutcome.Registered, parar.Outcome);
        var tenant = a.Tenant.TenantId.Value;

        // Revogou as DUAS finalidades, na organização da sessão, com origem CustomerKeyword, autor do sistema e evidência = mensagem e palavra (nunca o texto livre).
        var linhas = await ConsentimentosAsync(tenant);
        Assert.Equal(2, linhas.Count);
        Assert.Equal(new short[] { 1, 2 }, linhas.Select(l => l.Finalidade).Order().ToArray());
        Assert.All(linhas, l =>
        {
            Assert.Equal((a.C.OrganizacaoId, "+" + Fone, (short)2, (short)3, (short)3), (l.Org, l.Fone, l.Situacao, l.Origem, l.AtorTipo));
            Assert.Null(l.AtorId);
            Assert.Equal((entrada.SessionId, parar.MessageId), (l.Sessao!.Value, l.Mensagem!.Value));
            Assert.Equal($"message:{parar.MessageId:N}:keyword:PARAR", l.Evidencia);
        });

        // O evento de entrada diz que é comando; a atenção é OptOutRevoked; a auditoria é exata (uma por linha), sem telefone nem texto.
        Assert.True(await EhComandoDeSaidaAsync(a.C, entrada.SessionId, parar.MessageId));
        Assert.Equal(new[] { "OptOutRevoked" }, (await AtencoesAsync(a.C, entrada.SessionId)).Where(r => r == "OptOutRevoked").ToArray());
        Assert.Equal(new[] { "consent.keyword-revoke", "consent.keyword-revoke" }, await OperacoesAuditadasAsync(a.C));
        var auditoria = (await a.C.LinhasDoOutboxDoTenantAsync()).Where(o => o.EventType == ConversationAuditEvents.AuditRequested).Select(o => o.Payload).ToList();
        Assert.All(auditoria, p =>
        {
            Assert.DoesNotContain("999990505", p);
            Assert.DoesNotContain("PARAR", p);
        });

        // SUPRESSÃO: nenhum turno novo de IA, nenhuma chamada ao modelo, nenhuma resposta nova.
        var (turnosDepois, chamadasDepois) = await ProcessarAsync(a, entrada.SessionId);
        Assert.Equal((turnosAntes, chamadasAntes), (turnosDepois, chamadasDepois));
        Assert.Single(await a.RespostasDaIAAsync(entrada.SessionId)); // só a resposta preparada

        // PROCESSAMENTO POSTERIOR: quando o cliente volta com uma mensagem comum, a IA responde a ELA, e o comando de saída não entra como entrada do modelo (nem agora nem depois).
        a.Roteiro = _ => a.Sucesso(CenarioDeAgentes.Responder("O horário é das 8 às 17 h."));
        await a.C.EntrarAsync(Fone, "Qual é o horário?");
        await ProcessarAsync(a, entrada.SessionId);
        var pedido = a.PedidosAoModelo.Last();
        Assert.Contains(pedido.Messages, m => m.Text.Contains("Qual é o horário?"));
        Assert.DoesNotContain(pedido.Messages, m => m.Text.Contains("PARAR"));
        Assert.Equal(2, (await ConsentimentosAsync(tenant)).Count); // nada reativou o consentimento

        // O histórico da conversa foi preservado: a mensagem do cliente está lá, visível ao atendimento.
        var historico = await a.C.HistoricoAsync(entrada.SessionId);
        Assert.Contains(historico!.Items, m => m.Id == parar.MessageId && m.Text == "PARAR");
    }

    [Fact]
    public async Task PARAR_depois_de_atendente_humano_e_confirmado_e_o_atendimento_humano_continua_possivel()
    {
        var a = await CenaAsync(banco, "e25-parar-hum");
        var entrada = await PrepararAsync(a, Fone, Saida.HumanoLivre);

        var parar = await a.C.EntrarAsync(Fone, "Parar!");

        Assert.Equal(2, (await ConsentimentosAsync(a.Tenant.TenantId.Value)).Count);
        Assert.True(await EhComandoDeSaidaAsync(a.C, entrada.SessionId, parar.MessageId));
        Assert.Empty(await OcorrenciasAsync(a.Tenant.TenantId.Value)); // confirmado: nenhuma ocorrência a revisar

        // A conversa segue no modo humano (nada foi encerrado nem transferido pela decisão) e a pessoa ainda pode responder.
        var sessao = await LerSessaoAsync(a.C, entrada.SessionId);
        Assert.Equal(HandlingMode.HumanAssisted, sessao.HandlingMode);
        Assert.Equal(SessionStatus.Open, sessao.Status);
    }

    [Fact]
    public async Task SAIR_em_resposta_a_modelo_de_mensagem_e_CANCELAR_sem_saida_anterior_sao_confirmados()
    {
        var a = await CenaAsync(banco, "e25-modelo");
        var comModelo = await PrepararAsync(a, Fone, Saida.HumanoModelo);
        var sair = await a.C.EntrarAsync(Fone, "Sair");
        Assert.True(await EhComandoDeSaidaAsync(a.C, comModelo.SessionId, sair.MessageId));
        Assert.Equal(2, (await ConsentimentosAsync(a.Tenant.TenantId.Value)).Count);

        var b = await CenaAsync(banco, "e25-semsaida");
        var aberta = await b.C.EntrarAsync("5511999990606", "Olá");
        var cancelar = await b.C.EntrarAsync("5511999990606", "CANCELAR");
        Assert.True(await EhComandoDeSaidaAsync(b.C, aberta.SessionId, cancelar.MessageId));
        Assert.Equal(2, (await ConsentimentosAsync(b.Tenant.TenantId.Value)).Count);
    }

    [Fact]
    public async Task Contato_de_referencia_opaca_com_comando_confirmado_suprime_a_resposta_mas_nao_ha_telefone_para_revogar()
    {
        var a = await CenaAsync(banco, "e25-opaco");
        var aberta = await a.C.EntrarAsync("BR.opaco-9f3k2", "Olá");
        var parar = await a.C.EntrarAsync("BR.opaco-9f3k2", "PARAR");

        Assert.True(await EhComandoDeSaidaAsync(a.C, aberta.SessionId, parar.MessageId)); // a resposta automática continua suprimida
        Assert.Empty(await ConsentimentosAsync(a.Tenant.TenantId.Value)); // sem telefone E.164 não há chave de consentimento
        Assert.Contains("OptOutRevoked", await AtencoesAsync(a.C, aberta.SessionId)); // o atendente é avisado
        Assert.Empty(await OperacoesAuditadasAsync(a.C));
    }

    // ------------------------------------------------------------------ ambiguidade: não revoga, não descarta, processa normal

    [Fact]
    public async Task SAIR_em_conversa_com_IA_e_ambiguo_nao_revoga_abre_a_revisao_levanta_atencao_e_o_processamento_segue_normal()
    {
        var a = await CenaAsync(banco, "e25-amb-ia");
        a.Roteiro = _ => a.Sucesso(CenarioDeAgentes.Responder("Entendi."));
        var entrada = await PrepararAsync(a, Fone, Saida.IA);
        var (turnosAntes, chamadasAntes) = await ProcessarAsync(a, entrada.SessionId);

        var sair = await a.C.EntrarAsync(Fone, "SAIR");

        var tenant = a.Tenant.TenantId.Value;
        Assert.Empty(await ConsentimentosAsync(tenant)); // invariante: nenhuma linha de consentimento pela decisão ambígua
        Assert.False(await EhComandoDeSaidaAsync(a.C, entrada.SessionId, sair.MessageId));
        var ocorrencia = Assert.Single(await OcorrenciasAsync(tenant));
        Assert.Equal((sair.MessageId, (short)2, "SAIR", (short?)null), (ocorrencia.Mensagem, ocorrencia.Motivo, ocorrencia.Palavra, ocorrencia.Decisao)); // ConversationalContext
        Assert.Equal((entrada.SessionId, a.C.OrganizacaoId), (ocorrencia.Sessao, ocorrencia.Org));
        Assert.Contains("PossibleOptOut", await AtencoesAsync(a.C, entrada.SessionId));
        Assert.Equal(new[] { "consent.possible-optout" }, await OperacoesAuditadasAsync(a.C));
        Assert.DoesNotContain("SAIR", (await a.C.LinhasDoOutboxDoTenantAsync()).Single(o => o.EventType == ConversationAuditEvents.AuditRequested).Payload);

        // PROCESSAMENTO NORMAL: a IA recebe a mensagem como qualquer outra (um turno novo e uma chamada ao modelo) e a mensagem original está na conversa.
        var (turnosDepois, chamadasDepois) = await ProcessarAsync(a, entrada.SessionId);
        Assert.True(turnosDepois > turnosAntes);
        Assert.True(chamadasDepois > chamadasAntes);
        Assert.Contains((await a.C.HistoricoAsync(entrada.SessionId))!.Items, m => m.Id == sair.MessageId && m.Text == "SAIR");
    }

    [Fact]
    public async Task Fluxo_aguardando_texto_livre_torna_as_tres_palavras_ambiguas_e_sem_o_marcador_PARAR_e_confirmado()
    {
        var a = await CenaAsync(banco, "e25-fluxo");
        var comMarcador = await PrepararAsync(a, Fone, Saida.FluxoTextoLivre);
        var parar = await a.C.EntrarAsync(Fone, "PARAR");

        Assert.Empty(await ConsentimentosAsync(a.Tenant.TenantId.Value));
        Assert.False(await EhComandoDeSaidaAsync(a.C, comMarcador.SessionId, parar.MessageId));
        var ocorrencia = Assert.Single(await OcorrenciasAsync(a.Tenant.TenantId.Value));
        Assert.Equal(((short)1, "PARAR"), (ocorrencia.Motivo, ocorrencia.Palavra)); // FlowFreeText

        // O marcador só vale para a PRIMEIRA entrada depois do fluxo: uma segunda palavra de saída (PARAR) já é confirmada.
        var segunda = await a.C.EntrarAsync(Fone, "PARAR");
        Assert.True(await EhComandoDeSaidaAsync(a.C, comMarcador.SessionId, segunda.MessageId));
        Assert.Equal(2, (await ConsentimentosAsync(a.Tenant.TenantId.Value)).Count);

        // O mesmo fluxo SEM pergunta de texto livre (por exemplo, só uma informação): PARAR é confirmado de imediato.
        var b = await CenaAsync(banco, "e25-fluxo2");
        var semMarcador = await PrepararAsync(b, Fone, Saida.FluxoSemTextoLivre);
        var parar2 = await b.C.EntrarAsync(Fone, "PARAR");
        Assert.True(await EhComandoDeSaidaAsync(b.C, semMarcador.SessionId, parar2.MessageId));
        Assert.Equal(2, (await ConsentimentosAsync(b.Tenant.TenantId.Value)).Count);
    }

    [Fact]
    public async Task Linguagem_natural_gera_ocorrencia_sem_revogar_e_negacao_ou_midia_nao_geram_nada()
    {
        var a = await CenaAsync(banco, "e25-natural");
        var entrada = await PrepararAsync(a, Fone, Saida.Nenhuma);

        var pare = await a.C.EntrarAsync(Fone, "Pare de me ligar, por favor");
        var nega = await a.C.EntrarAsync(Fone, "não quero parar");
        var sairDoPlano = await a.C.EntrarAsync(Fone, "sair do plano amanhã");
        var imagem = await a.C.EntrarAsync(Fone, "PARAR", tipo: MessageKind.Image);

        var tenant = a.Tenant.TenantId.Value;
        Assert.Empty(await ConsentimentosAsync(tenant));
        var ocorrencia = Assert.Single(await OcorrenciasAsync(tenant));
        Assert.Equal((pare.MessageId, (short)3, (string?)null), (ocorrencia.Mensagem, ocorrencia.Motivo, ocorrencia.Palavra)); // NaturalLanguageIntent, sem palavra
        Assert.Equal(new[] { "consent.possible-optout" }, await OperacoesAuditadasAsync(a.C));
        foreach (var comum in new[] { pare, nega, sairDoPlano, imagem })
        {
            Assert.False(await EhComandoDeSaidaAsync(a.C, entrada.SessionId, comum.MessageId)); // processamento normal para todas
        }

        Assert.Equal(1, (await AtencoesAsync(a.C, entrada.SessionId)).Count(r => r == "PossibleOptOut"));
        // A mensagem original nunca é descartada.
        Assert.Contains((await a.C.HistoricoAsync(entrada.SessionId))!.Items, m => m.Id == pare.MessageId && m.Text == "Pare de me ligar, por favor");
    }

    // ------------------------------------------------------------------ idempotência e concorrência

    [Fact]
    public async Task Reentrega_do_mesmo_evento_do_provedor_e_no_op_sem_nova_revogacao_nem_auditoria_espuria()
    {
        var a = await CenaAsync(banco, "e25-reent");
        await a.C.EntrarAsync(Fone, "Olá");

        var primeira = await a.C.EntrarAsync(Fone, "PARAR", evento: "evt-parar-fixo");
        var repetidas = new List<IngestInboundMessageResult>();
        for (var i = 0; i < 3; i++)
        {
            repetidas.Add(await a.C.EntrarAsync(Fone, "PARAR", evento: "evt-parar-fixo"));
        }

        Assert.Equal(IngestOutcome.Registered, primeira.Outcome);
        Assert.All(repetidas, r => Assert.Equal((IngestOutcome.Duplicate, primeira.MessageId), (r.Outcome, r.MessageId)));
        Assert.Equal(2, (await ConsentimentosAsync(a.Tenant.TenantId.Value)).Count);
        Assert.Equal(2, (await OperacoesAuditadasAsync(a.C)).Count);

        // A mesma ocorrência (ambígua) reentregue também não duplica.
        var b = await CenaAsync(banco, "e25-reent2");
        await PrepararAsync(b, Fone, Saida.IA);
        await b.C.EntrarAsync(Fone, "SAIR", evento: "evt-sair-fixo");
        await b.C.EntrarAsync(Fone, "SAIR", evento: "evt-sair-fixo");
        Assert.Single(await OcorrenciasAsync(b.Tenant.TenantId.Value));
        Assert.Single(await OperacoesAuditadasAsync(b.C));
    }

    [Fact]
    public async Task Entregas_concorrentes_da_mesma_mensagem_e_de_mensagens_diferentes_revogam_uma_so_vez_por_finalidade()
    {
        var a = await CenaAsync(banco, "e25-conc");
        await a.C.EntrarAsync(Fone, "Olá");

        // A MESMA mensagem em 8 entregas simultâneas: uma só é registrada.
        var mesmas = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => a.C.EntrarAsync(Fone, "PARAR", evento: "evt-conc-fixo"))));
        Assert.Equal(1, mesmas.Count(r => r.Outcome == IngestOutcome.Registered));
        Assert.Equal(2, (await ConsentimentosAsync(a.Tenant.TenantId.Value)).Count);

        // Mensagens DIFERENTES com o mesmo comando, em paralelo: a revogação já em vigor não se repete (nenhuma linha nova por finalidade).
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => a.C.EntrarAsync(Fone, "PARAR"))));
        var linhas = await ConsentimentosAsync(a.Tenant.TenantId.Value);
        Assert.Equal(2, linhas.Count);
        Assert.Equal(2, (await OperacoesAuditadasAsync(a.C)).Count);
        Assert.Equal(linhas.Count, linhas.Select(l => l.Seq).Distinct().Count());
    }

    // P1 da 1.ª auditoria da E2 (Codex): turno já AGENDADO por uma mensagem comum, PARAR chega antes de ele rodar. O turno pendente não pode chamar o modelo nem responder; uma mensagem comum depois reabre o processamento.
    [Fact]
    public async Task Turno_agendado_antes_do_PARAR_nao_chama_o_modelo_nem_responde_e_uma_mensagem_comum_depois_volta_a_ser_atendida()
    {
        var a = await CenaAsync(banco, "e26-pendente");
        a.Roteiro = _ => a.Sucesso(CenarioDeAgentes.Responder("O exame não exige jejum."));
        var pergunta = await a.C.EntrarAsync(Fone, "Preciso de jejum para o exame?");
        await a.RelayAsync(); // o turno fica AGENDADO (ainda dentro da janela de agrupamento)
        Assert.Single(await a.TurnosAsync(pergunta.SessionId));

        var parar = await a.C.EntrarAsync(Fone, "PARAR");
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(5));
        await a.ExecutarAsync();
        await a.RelayAsync();
        await a.ExecutarAsync(); // um eventual turno sucessor também não pode chamar o modelo

        Assert.Equal(0, a.ChamadasAoModelo);
        Assert.Empty(await a.RespostasDaIAAsync(pergunta.SessionId));
        Assert.Equal(2, (await ConsentimentosAsync(a.Tenant.TenantId.Value)).Count);
        Assert.Contains((await a.C.HistoricoAsync(pergunta.SessionId))!.Items, m => m.Id == parar.MessageId && m.Text == "PARAR"); // o comando segue visível

        // Uma mensagem comum depois reabre o processamento (sem bloqueio permanente) e a IA responde a ela.
        await a.C.EntrarAsync(Fone, "Obrigado, qual o horário de coleta?");
        await a.RelayAsync();
        a.Avancar(TimeSpan.FromSeconds(5));
        await a.ExecutarAsync();
        Assert.True(a.ChamadasAoModelo >= 1);
        Assert.NotEmpty(await a.RespostasDaIAAsync(pergunta.SessionId));
        Assert.DoesNotContain(a.PedidosAoModelo.SelectMany(p => p.Messages), m => m.Text.Contains("PARAR"));
    }

    [Fact]
    public async Task Revogacao_concorrente_a_uma_resposta_de_IA_em_curso_nao_reativa_o_consentimento_e_a_resposta_defasada_e_rejeitada()
    {
        var a = await CenaAsync(banco, "e25-ia-curso");
        var entrada = await a.C.EntrarAsync(Fone, "Olá");
        var turno = Guid.NewGuid();
        var inicio = await a.C.IniciarTurnoAsync(entrada.SessionId, turno);

        // Com o turno de IA em curso, o cliente manda PARAR (confirmado): a resposta que a IA monta agora é defasada e a regra fixa a rejeita.
        await a.C.EntrarAsync(Fone, "PARAR");
        var resposta = await a.C.ResponderAsync(entrada.SessionId, turno, inicio, entrada.ConversationId, "Aqui está a informação que você pediu.");

        Assert.NotEqual(RegisterReplyOutcome.Accepted, resposta.Outcome);
        var linhas = await ConsentimentosAsync(a.Tenant.TenantId.Value);
        Assert.Equal(2, linhas.Count);
        Assert.All(linhas, l => Assert.Equal((short)2, l.Situacao)); // nada reativou
    }

    [Fact]
    public async Task Mesmo_telefone_em_outra_organizacao_ou_outro_tenant_nunca_e_afetado_pela_revogacao()
    {
        var a = await CenaAsync(banco, "e25-iso");
        var orgB = (await banco.CriarOrganizacaoAsync(a.C.Tenant, "Organização B e25-iso")).Id;
        var unidadeB = await a.C.CriarUnidadeAsync(orgB, "Matriz B", matriz: true);
        var canalB = await a.C.CriarCanalAsync(new[] { unidadeB }, unidadeB, organizacao: orgB);
        var outro = await CenaAsync(banco, "e25-iso2");

        // O MESMO telefone fala nas duas organizações (sessões distintas) e noutro tenant; só a da organização A pede PARAR.
        await a.C.EntrarAsync(Fone, "Olá A");
        await a.C.EntrarAsync(Fone, "Olá B", organizacao: orgB, unidade: unidadeB, canal: canalB);
        await outro.C.EntrarAsync(Fone, "Olá outro tenant");
        await Task.WhenAll(
            Task.Run(() => a.C.EntrarAsync(Fone, "PARAR")),
            Task.Run(() => a.C.EntrarAsync(Fone, "Pode me ajudar?", organizacao: orgB, unidade: unidadeB, canal: canalB)),
            Task.Run(() => outro.C.EntrarAsync(Fone, "Bom dia")));

        var linhas = await ConsentimentosAsync(a.Tenant.TenantId.Value);
        Assert.Equal(2, linhas.Count);
        Assert.All(linhas, l => Assert.Equal(a.C.OrganizacaoId, l.Org)); // só a organização da sessão
        Assert.Empty(await ConsentimentosAsync(outro.Tenant.TenantId.Value));

        // Agora PARAR na organização B e no outro tenant: cada um revoga só o seu.
        await a.C.EntrarAsync(Fone, "PARAR", organizacao: orgB, unidade: unidadeB, canal: canalB);
        await outro.C.EntrarAsync(Fone, "PARAR");
        Assert.Equal(new[] { a.C.OrganizacaoId, orgB }.OrderBy(x => x), (await ConsentimentosAsync(a.Tenant.TenantId.Value)).Select(l => l.Org).Distinct().OrderBy(x => x));
        Assert.Equal(4, (await ConsentimentosAsync(a.Tenant.TenantId.Value)).Count);
        Assert.Equal(2, (await ConsentimentosAsync(outro.Tenant.TenantId.Value)).Count);
    }

    // ------------------------------------------------------------------ falha transacional

    [Fact]
    public async Task Falha_ao_gravar_o_efeito_desfaz_a_mensagem_a_revogacao_e_a_auditoria_e_a_retentativa_do_mesmo_evento_conclui_uma_vez()
    {
        var a = await CenaAsync(banco, "e25-falha");
        await a.C.EntrarAsync(Fone, "Olá");
        var tenant = a.Tenant.TenantId.Value;
        var entradasAntes = await MensagensDeEntradaAsync(tenant);

        var nome = "e25_" + Guid.NewGuid().ToString("N")[..8];
        await a.C.ExecutarSqlAsync(
            $@"CREATE FUNCTION conversations.fn_{nome}() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'falha injetada pelo teste'; END $$;
               CREATE TRIGGER tg_{nome} BEFORE INSERT ON conversations.contact_consents FOR EACH ROW WHEN (NEW.tenant_id = '{tenant}'::uuid) EXECUTE FUNCTION conversations.fn_{nome}();");
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => a.C.EntrarAsync(Fone, "PARAR", evento: "evt-falha-fixo"));
        }
        finally
        {
            await a.C.ExecutarSqlAsync($"DROP TRIGGER IF EXISTS tg_{nome} ON conversations.contact_consents; DROP FUNCTION IF EXISTS conversations.fn_{nome}();");
        }

        // Nada ficou: nem a mensagem (a mensagem do cliente NÃO existe sem a decisão), nem a revogação, nem a auditoria.
        Assert.Equal(entradasAntes, await MensagensDeEntradaAsync(tenant));
        Assert.Empty(await ConsentimentosAsync(tenant));
        Assert.Empty(await OperacoesAuditadasAsync(a.C));

        // A retentativa do MESMO evento do provedor (o Channels reenvia) conclui, uma vez.
        var certa = await a.C.EntrarAsync(Fone, "PARAR", evento: "evt-falha-fixo");
        Assert.Equal(IngestOutcome.Registered, certa.Outcome);
        Assert.Equal(2, (await ConsentimentosAsync(tenant)).Count);
        Assert.Equal(2, (await OperacoesAuditadasAsync(a.C)).Count);
    }

    [Fact]
    public async Task Falha_ao_gravar_a_ocorrencia_desfaz_a_mensagem_e_a_auditoria_e_a_retentativa_conclui()
    {
        var a = await CenaAsync(banco, "e25-falha2");
        await PrepararAsync(a, Fone, Saida.IA);
        var tenant = a.Tenant.TenantId.Value;
        var entradasAntes = await MensagensDeEntradaAsync(tenant);

        var nome = "e25b_" + Guid.NewGuid().ToString("N")[..8];
        await a.C.ExecutarSqlAsync(
            $@"CREATE FUNCTION conversations.fn_{nome}() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'falha injetada pelo teste'; END $$;
               CREATE TRIGGER tg_{nome} BEFORE INSERT ON conversations.optout_reviews FOR EACH ROW WHEN (NEW.tenant_id = '{tenant}'::uuid) EXECUTE FUNCTION conversations.fn_{nome}();");
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => a.C.EntrarAsync(Fone, "SAIR", evento: "evt-falha2-fixo"));
        }
        finally
        {
            await a.C.ExecutarSqlAsync($"DROP TRIGGER IF EXISTS tg_{nome} ON conversations.optout_reviews; DROP FUNCTION IF EXISTS conversations.fn_{nome}();");
        }

        Assert.Equal(entradasAntes, await MensagensDeEntradaAsync(tenant));
        Assert.Empty(await OcorrenciasAsync(tenant));
        Assert.Empty(await OperacoesAuditadasAsync(a.C));

        Assert.Equal(IngestOutcome.Registered, (await a.C.EntrarAsync(Fone, "SAIR", evento: "evt-falha2-fixo")).Outcome);
        Assert.Single(await OcorrenciasAsync(tenant));
        Assert.Single(await OperacoesAuditadasAsync(a.C));
    }

    // ------------------------------------------------------------------ resolução posterior (estrutura da E2.4) e operação

    private static RequestContext Supervisor(CenarioDeAgentes a, Guid? organizacao = null) =>
        PostgresFixture.ContextoDeUsuario(a.Tenant, Guid.NewGuid(), new PermissionGrant(Permission.ConsentManage, organizacao ?? a.C.OrganizacaoId));

    [Fact]
    public async Task A_ocorrencia_aberta_pela_ingestao_e_resolvida_pela_estrutura_da_E2_4_uma_so_vez_e_so_com_ConsentManage()
    {
        var a = await CenaAsync(banco, "e25-resolver");
        var entrada = await PrepararAsync(a, Fone, Saida.IA);
        var sair = await a.C.EntrarAsync(Fone, "CANCELAR");
        var supervisor = Supervisor(a);

        // Sem ConsentManage: negado, e a ocorrência continua pendente.
        var semPermissao = PostgresFixture.ContextoDeUsuario(a.Tenant, Guid.NewGuid(), new PermissionGrant(Permission.ConversationRead, a.C.OrganizacaoId));
        await Assert.ThrowsAsync<AccessDeniedException>(() => a.C.ComoAsync(semPermissao, sp => sp.GetRequiredService<IConsentCommands>().ResolveOptOutReviewAsync(entrada.SessionId, sair.MessageId, OptOutResolution.Revoked)));
        Assert.Null((await OcorrenciasAsync(a.Tenant.TenantId.Value)).Single().Decisao);

        var pendentes = await a.C.ComoAsync(supervisor, sp => sp.GetRequiredService<IConsentQueries>().ListOptOutReviewsAsync(entrada.SessionId));
        Assert.Equal((sair.MessageId, OptOutReviewReason.ConversationalContext, "CANCELAR"), (pendentes![0].MessageId, pendentes[0].Reason, pendentes[0].Keyword));

        var resolvida = await a.C.ComoAsync(supervisor, sp => sp.GetRequiredService<IConsentCommands>().ResolveOptOutReviewAsync(entrada.SessionId, sair.MessageId, OptOutResolution.Revoked));
        Assert.Equal(OptOutResolveOutcome.Resolved, resolvida.Outcome);
        var linhas = await ConsentimentosAsync(a.Tenant.TenantId.Value);
        Assert.Equal(2, linhas.Count);
        Assert.All(linhas, l => Assert.Equal(((short)2, (short)2), (l.Situacao, l.Origem))); // Revoked, AgentRecorded

        // Uma só vez: a mesma decisão é idempotente; a oposta é conflito; nada novo.
        Assert.Equal(OptOutResolveOutcome.AlreadyResolved, (await a.C.ComoAsync(supervisor, sp => sp.GetRequiredService<IConsentCommands>().ResolveOptOutReviewAsync(entrada.SessionId, sair.MessageId, OptOutResolution.Revoked))).Outcome);
        Assert.Equal(OptOutResolveOutcome.Conflict, (await a.C.ComoAsync(supervisor, sp => sp.GetRequiredService<IConsentCommands>().ResolveOptOutReviewAsync(entrada.SessionId, sair.MessageId, OptOutResolution.Kept))).Outcome);
        Assert.Equal(2, (await ConsentimentosAsync(a.Tenant.TenantId.Value)).Count);
        Assert.Equal(new[] { "consent.possible-optout", "consent.record", "consent.record", "consent.possible-optout-resolve" }, await OperacoesAuditadasAsync(a.C));
    }

    [Fact]
    public async Task Ocorrencia_de_um_tenant_nao_e_visivel_nem_resolvivel_por_supervisor_de_outro_tenant()
    {
        var a = await CenaAsync(banco, "e25-tenanta");
        var entrada = await PrepararAsync(a, Fone, Saida.IA);
        var sair = await a.C.EntrarAsync(Fone, "SAIR");
        var outro = await CenaAsync(banco, "e25-tenantb");
        var supervisorDoOutro = Supervisor(outro);

        // A mesma sessão e mensagem, no contexto de OUTRO tenant: não existem.
        var lista = await outro.C.ComoAsync(supervisorDoOutro, sp => sp.GetRequiredService<IConsentQueries>().ListOptOutReviewsAsync(entrada.SessionId));
        Assert.Null(lista);
        var tentativa = await outro.C.ComoAsync(supervisorDoOutro, sp => sp.GetRequiredService<IConsentCommands>().ResolveOptOutReviewAsync(entrada.SessionId, sair.MessageId, OptOutResolution.Revoked));
        Assert.Equal(OptOutResolveOutcome.NotFound, tentativa.Outcome);
        Assert.Null((await OcorrenciasAsync(a.Tenant.TenantId.Value)).Single().Decisao);
        Assert.Empty(await ConsentimentosAsync(a.Tenant.TenantId.Value));
    }

    [Fact]
    public async Task A_sonda_de_status_conta_as_pendencias_a_idade_da_mais_antiga_e_avisa_acima_da_meta_sem_expirar_nada()
    {
        var a = await CenaAsync(banco, "e25-sonda");
        await PrepararAsync(a, Fone, Saida.IA);
        await a.C.EntrarAsync(Fone, "SAIR");
        await a.C.EntrarAsync(Fone, "pare de me ligar");

        // A sonda é interna do módulo: o teste a instancia com a fábrica de escopos do ambiente (o que o comando status faz pelo registro AddConversationsHealth).
        var fabrica = await a.C.ComoAsync(a.C.ContextoDeSistema, sp => Task.FromResult(sp.GetRequiredService<IServiceScopeFactory>()));
        var sonda = (IHealthProbe)Activator.CreateInstance(typeof(ConversationsDbContext).Assembly.GetType("IaBimeta.Conversations.Infrastructure.ConversationsHealthProbe")!, fabrica)!;
        Assert.Contains(new ServiceCollection().AddConversationsHealth(), d => d.ServiceType == typeof(IHealthProbe)); // e o registro público existe
        var limites = new HealthThresholds { OptOutReviewTarget = TimeSpan.FromHours(24) };
        var tenant = a.Tenant.TenantId.Value;
        await using var conexaoDaIdade = new NpgsqlConnection(TestDatabase.ConnectionString);
        await conexaoDaIdade.OpenAsync();
        await using var consultaDaIdade = new NpgsqlCommand("select min(opened_at) from conversations.optout_reviews where tenant_id = @t", conexaoDaIdade);
        consultaDaIdade.Parameters.AddWithValue("t", tenant);
        var maisAntiga = new DateTimeOffset(DateTime.SpecifyKind((DateTime)(await consultaDaIdade.ExecuteScalarAsync())!, DateTimeKind.Utc)); // a idade é relativa à abertura (o relógio do ambiente de teste não é o da máquina)

        // Dentro da meta: informa a contagem e a idade, sem aviso.
        var dentro = (await sonda.CheckAsync(new HealthContext(limites, maisAntiga.AddHours(1), tenant))).Single();
        Assert.Equal((HealthSeverity.Ok, tenant), (dentro.Severity, dentro.TenantId!.Value));
        Assert.Contains("2 possível(is) saída(s)", dentro.Message);
        Assert.Contains("há 60 min", dentro.Message);

        // Acima de um dia: aviso, com a ação. Nada é resolvido nem expirado pela sonda.
        var acima = (await sonda.CheckAsync(new HealthContext(limites, maisAntiga.AddHours(30), tenant))).Single();
        Assert.Equal(HealthSeverity.Warning, acima.Severity);
        Assert.Contains("acima da meta", acima.Message);
        Assert.Contains("ConsentManage", acima.Action);
        Assert.All(await OcorrenciasAsync(tenant), o => Assert.Null(o.Decisao));
        Assert.Empty(await ConsentimentosAsync(tenant));

        // Resolvida a pendência, ela deixa de contar; sem pendências, a sonda diz que está tudo em dia.
        var supervisor = Supervisor(a);
        foreach (var o in await OcorrenciasAsync(tenant))
        {
            await a.C.ComoAsync(supervisor, sp => sp.GetRequiredService<IConsentCommands>().ResolveOptOutReviewAsync(o.Sessao, o.Mensagem, OptOutResolution.Kept));
        }

        var emDia = (await sonda.CheckAsync(new HealthContext(limites, maisAntiga.AddHours(30), tenant))).Single();
        Assert.Equal(HealthSeverity.Ok, emDia.Severity);
        Assert.Contains("nenhuma possível saída", emDia.Message);
        Assert.Throws<InvalidOperationException>(() => new HealthThresholds { OptOutReviewTarget = TimeSpan.Zero }.Validate());
    }
}
