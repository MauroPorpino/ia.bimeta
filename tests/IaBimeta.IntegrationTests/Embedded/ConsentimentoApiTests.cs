using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using IaBimeta.Api.Embedded;
using IaBimeta.Conversations.Application;
using IaBimeta.Conversations.Infrastructure;
using IaBimeta.Identity.Domain;
using IaBimeta.IntegrationTests.Api;
using IaBimeta.IntegrationTests.ContactCenter;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.Messaging;
using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IaBimeta.IntegrationTests.Embedded;

/// <summary>
/// E2.4 (ADR 0041, seções 4 e 5): o consentimento de contato pelo servidor do sistema externo (HMAC, <c>PUT /embed/v1/customers/{ref}/consents</c>) e pelo atendente com <c>ConsentManage</c>
/// (<c>/contact-center/items/{id}/consents</c>), e a resolução de <c>PossibleOptOut</c> (<c>/contact-center/items/{id}/optout-reviews</c>). API real e PostgreSQL real; a auditoria vai ao outbox na transação do efeito e
/// é entregue pelo handler do módulo (o que o relay do Worker faz).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ConsentimentoApiTests(PostgresFixture banco)
{
    private const string TelA = "5511999990101"; // contato da organização A (item A)
    private const string TelB = "5511999990202"; // contato SÓ da organização B (item B)
    private const string RefOpaca = "BR.opaco-9f3k2"; // referência que não é telefone (contato opaco)

    private sealed record Cena(
        CenarioDeConversas C, ApiServer Api, string TokenDoAdmin, string TokenDoGestor, string TokenDoAtendente, ConexaoDto Conexao, ErpAssinante Erp, Guid OrgA, Guid OrgB,
        Guid SessaoA, Guid ItemA, Guid SessaoB, Guid ItemB, Guid SessaoOpaca, Guid ItemOpaco, TrocaDto TrocaAtendente, TrocaDto TrocaGestor, Guid GestorId)
    {
        public Guid Tenant => C.Tenant.TenantId.Value;
    }

    private async Task<Cena> MontarAsync(string prefixo, ApiServer? api = null, string telA = TelA, string telB = TelB)
    {
        var c = await CenarioDeConversas.CriarAsync(banco, prefixo);
        var orgA = c.OrganizacaoId;
        var orgB = (await banco.CriarOrganizacaoAsync(c.Tenant, $"Organização B {prefixo}")).Id;
        var admin = await banco.CriarAdministradorDoTenantAsync(c.Tenant);
        api ??= await ApiServer.StartAsync("Production", EmbeddedTestSupport.Configuracao());
        var tokenAdmin = await api.LoginAsync(admin);
        var conexao = await api.CriarAsync<ConexaoDto>(tokenAdmin, "/embedded/connections", new
        {
            organizationId = orgA, name = "ERP", mode = "FullAttendant", provisioning = "JitAttendant", allowedOrigins = new[] { EmbeddedTestSupport.Origem }, allowStartConversation = false,
        });
        var chave = await api.CriarAsync<ChaveCriadaDto>(tokenAdmin, $"/embedded/connections/{conexao.Id}/keys", new { });
        var erp = new ErpAssinante(api, chave.KeyId, chave.Secret);

        var filaA = (await c.CriarFilaAsync("Fila A", organizacao: orgA)).Id;
        var filaB = (await c.CriarFilaAsync("Fila B", organizacao: orgB)).Id;
        var unidadeB = await c.CriarUnidadeAsync(orgB, "Matriz B", matriz: true);
        var canalB = await c.CriarCanalAsync(new[] { unidadeB }, unidadeB, organizacao: orgB);
        var sessaoA = (await c.EntrarAsync(telA, texto: "Pergunta A")).SessionId;
        var sessaoOpaca = (await c.EntrarAsync(RefOpaca, texto: "Contato opaco")).SessionId;
        var sessaoB = (await c.EntrarAsync(telB, texto: "Pergunta B", organizacao: orgB, unidade: unidadeB, canal: canalB)).SessionId;
        var itemA = (await c.NaFilaAsync(sessaoA, filaA)).Id;
        var itemOpaco = (await c.NaFilaAsync(sessaoOpaca, filaA)).Id;
        var itemB = (await c.NaFilaAsync(sessaoB, filaB)).Id;

        // O gestor da organização A (ConsentManage) e o atendente comum da fila A (sem ConsentManage).
        var gestor = await banco.CriarUsuarioAsync(c.Tenant, orgA, Role.Manager);
        var atendente = await banco.CriarUsuarioAsync(c.Tenant, orgA, Role.Attendant);
        await c.AdicionarAtendenteAsync(filaA, atendente.Id);

        // As duas sessões embutidas: o atendente federado (JIT) e o gestor mapeado como identidade da conexão (sessão embutida de supervisor).
        var verifier = EmbeddedTestSupport.NovoVerifier();
        var ticket = await erp.EmitirAsync("u-consent", EmbeddedTestSupport.Challenge(verifier), cliente: "CLI-100");
        var trocaAtendente = await EmbeddedTestSupport.TrocarAsync(api, ticket.Ticket, verifier);
        await c.AdicionarAtendenteAsync(filaA, trocaAtendente.User.Id);
        Assert.Equal(HttpStatusCode.OK, await api.StatusAsync(tokenAdmin, HttpMethod.Put, $"/embedded/connections/{conexao.Id}/identities", new { externalUserId = "gestor-consent", userId = gestor.Id }));
        var vg = EmbeddedTestSupport.NovoVerifier();
        var trocaGestor = await EmbeddedTestSupport.TrocarAsync(api, (await erp.EmitirAsync("gestor-consent", EmbeddedTestSupport.Challenge(vg), cliente: "CLI-100")).Ticket, vg);

        return new Cena(c, api, tokenAdmin, await api.LoginAsync(gestor), await api.LoginAsync(atendente), conexao, erp, orgA, orgB, sessaoA, itemA, sessaoB, itemB, sessaoOpaca, itemOpaco, trocaAtendente, trocaGestor, gestor.Id);
    }

    // ------------------------------------------------------------------ acesso direto ao banco (leitura, semeadura da ocorrência e a falha injetada)

    private sealed record Linha(long Seq, Guid Id, Guid Org, string Fone, short Finalidade, short Situacao, short Origem, string Evidencia, DateTimeOffset Coleta, DateTimeOffset Registro, short AtorTipo, Guid? AtorId, string? Cliente, Guid? Sessao, Guid? Mensagem);

    private static async Task<List<Linha>> LinhasAsync(Guid tenant)
    {
        await using var conexao = new NpgsqlConnection(TestDatabase.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = new NpgsqlCommand(
            "select seq, id, organization_id, phone, purpose, status, origin, evidence, collected_at, recorded_at, actor_kind, actor_id, customer_ref, session_id, source_message_id from conversations.contact_consents where tenant_id = @t order by seq", conexao);
        comando.Parameters.AddWithValue("t", tenant);
        var linhas = new List<Linha>();
        await using var leitor = await comando.ExecuteReaderAsync();
        while (await leitor.ReadAsync())
        {
            linhas.Add(new Linha(
                leitor.GetInt64(0), leitor.GetGuid(1), leitor.GetGuid(2), leitor.GetString(3), leitor.GetInt16(4), leitor.GetInt16(5), leitor.GetInt16(6), leitor.GetString(7),
                leitor.GetFieldValue<DateTimeOffset>(8), leitor.GetFieldValue<DateTimeOffset>(9), leitor.GetInt16(10), leitor.IsDBNull(11) ? null : leitor.GetGuid(11),
                leitor.IsDBNull(12) ? null : leitor.GetString(12), leitor.IsDBNull(13) ? null : leitor.GetGuid(13), leitor.IsDBNull(14) ? null : leitor.GetGuid(14)));
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

    private static async Task ExecutarAsync(string sql)
    {
        await using var conexao = new NpgsqlConnection(TestDatabase.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = new NpgsqlCommand(sql, conexao);
        await comando.ExecuteNonQueryAsync();
    }

    private static Task<long> AuditoriaNoOutboxAsync(Guid tenant) =>
        EscalarAsync("select count(*) from conversations.outbox_message where tenant_id = @t and event_type = 'conversations.audit-requested'", ("t", tenant));

    private static async Task<List<(string Operacao, string Recurso, Guid? Ator, string Origem)>> TrilhaAsync(Guid tenant)
    {
        await using var conexao = new NpgsqlConnection(TestDatabase.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = new NpgsqlCommand(
            "select operation, coalesce(resource_id, ''), actor_user_id, actor_origin from audit.audit_events where tenant_id = @t and operation like 'consent.%' order by occurred_at, id", conexao);
        comando.Parameters.AddWithValue("t", tenant);
        var linhas = new List<(string, string, Guid?, string)>();
        await using var leitor = await comando.ExecuteReaderAsync();
        while (await leitor.ReadAsync())
        {
            linhas.Add((leitor.GetString(0), leitor.GetString(1), leitor.IsDBNull(2) ? null : leitor.GetGuid(2), leitor.GetString(3)));
        }

        return linhas;
    }

    /// <summary>Semeia a ocorrência de possível saída (o que a ingestão da E2.5 fará): o teste da E2.4 prova só a revisão e a resolução.</summary>
    private static async Task<Guid> SemearOcorrenciaAsync(Guid tenant, Guid organizacao, Guid sessao, Guid? mensagem = null, short motivo = 2, string? palavra = "SAIR")
    {
        var id = Guid.CreateVersion7();
        var msg = mensagem ?? Guid.NewGuid();
        await using var conexao = new NpgsqlConnection(TestDatabase.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = new NpgsqlCommand(
            "insert into conversations.optout_reviews (id, tenant_id, organization_id, session_id, message_id, reason, keyword, opened_at) values (@id, @t, @o, @s, @m, @r, @k, now() - interval '1 hour')", conexao);
        comando.Parameters.AddWithValue("id", id);
        comando.Parameters.AddWithValue("t", tenant);
        comando.Parameters.AddWithValue("o", organizacao);
        comando.Parameters.AddWithValue("s", sessao);
        comando.Parameters.AddWithValue("m", msg);
        comando.Parameters.AddWithValue("r", motivo);
        comando.Parameters.AddWithValue("k", (object?)palavra ?? DBNull.Value);
        await comando.ExecuteNonQueryAsync();
        return msg;
    }

    /// <summary>Faz o que o relay do Worker faz com o handler do módulo: entrega os pedidos de auditoria pendentes à trilha e marca os entregues como processados.</summary>
    private static async Task<int> EntregarAuditoriaAsync(ApiServer api, Guid tenant, bool reentrega = false)
    {
        await using var escopo = api.Services.CreateAsyncScope();
        escopo.ServiceProvider.GetRequiredService<RequestContextHolder>().Set(new RequestContext(new TenantId(tenant), "messaging-relay"));
        var db = escopo.ServiceProvider.GetRequiredService<ConversationsDbContext>();
        var handler = escopo.ServiceProvider.GetServices<IEventHandler>().Single(h => h.EventType == ConversationAuditEvents.AuditRequested);
        var estado = reentrega ? OutboxState.Processed : OutboxState.Pending;
        var linhas = await db.Set<OutboxMessage>().Where(o => o.TenantId == tenant && o.EventType == ConversationAuditEvents.AuditRequested && o.State == estado).OrderBy(o => o.IngestSeq).ToListAsync();
        var entregues = 0;
        foreach (var linha in linhas)
        {
            var payload = MessagingJson.Deserialize(linha.Payload, handler.PayloadType);
            var envelope = new EventEnvelope(linha.Id, linha.EventType, linha.EventVersion, new TenantId(tenant), linha.CorrelationId, linha.CausationId, linha.OrderingKey, linha.OccurredAt, payload);
            if ((await handler.HandleAsync(envelope, default)).Outcome == HandlerOutcome.Success)
            {
                linha.State = OutboxState.Processed;
                entregues++;
            }
        }

        await db.SaveChangesAsync();
        return entregues;
    }

    // ------------------------------------------------------------------ chamadas

    private static string Iso(DateTimeOffset instante) => instante.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private static readonly DateTimeOffset Base = DateTimeOffset.UtcNow.AddDays(-2);

    private static Dictionary<string, object?> Corpo(string? fone, string? finalidade = "Utility", string? situacao = "Granted", string? evidencia = "Aceite no cadastro do cliente (formulário 123)", DateTimeOffset? coleta = null) =>
        new() { ["phone"] = fone, ["purpose"] = finalidade, ["status"] = situacao, ["evidence"] = evidencia, ["collectedAt"] = Iso(coleta ?? Base) };

    private static Task<HttpResponseMessage> RegistrarAsync(ErpAssinante erp, object corpo, string cliente = "CLI-100") =>
        erp.EnviarAsync($"/embed/v1/customers/{cliente}/consents", corpo, metodo: HttpMethod.Put);

    private static async Task<(HttpStatusCode Status, string Corpo)> LerAsync(HttpResponseMessage resposta)
    {
        using (resposta)
        {
            return (resposta.StatusCode, await resposta.Content.ReadAsStringAsync());
        }
    }

    private static async Task<(bool Gravado, string Situacao, string Finalidade)> OkAsync(HttpResponseMessage resposta)
    {
        using (resposta)
        {
            Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
            var json = await resposta.Content.ReadFromJsonAsync<JsonElement>();
            return (json.GetProperty("recorded").GetBoolean(), json.GetProperty("status").GetString()!, json.GetProperty("purpose").GetString()!);
        }
    }

    private static object PedidoDoAtendente(string finalidade = "Utility", string situacao = "Granted", string? evidencia = "Atendente confirmou por telefone", DateTimeOffset? coleta = null) =>
        new { purpose = finalidade, status = situacao, evidence = evidencia, collectedAt = Iso(coleta ?? Base) };

    private static async Task<JsonElement> LerJsonAsync(HttpResponseMessage resposta)
    {
        using (resposta)
        {
            Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
            return await resposta.Content.ReadFromJsonAsync<JsonElement>();
        }
    }

    // ------------------------------------------------------------------ servidor do sistema externo (HMAC)

    [Fact]
    public async Task Integracao_registra_na_organizacao_da_conexao_com_origem_autor_evidencia_e_data_e_audita_na_transacao_sem_dado_pessoal()
    {
        var s = await MontarAsync("e24-erp");
        await using var servidor = s.Api;
        var coleta = Base.AddMinutes(7);

        Assert.Equal((true, "Granted", "Utility"), await OkAsync(await RegistrarAsync(s.Erp, Corpo("+55 (11) 99999-0101", coleta: coleta))));

        var linha = Assert.Single(await LinhasAsync(s.Tenant));
        Assert.Equal((s.OrgA, "+5511999990101", (short)1, (short)1, (short)1), (linha.Org, linha.Fone, linha.Finalidade, linha.Situacao, linha.Origem)); // IntegrationAsserted, Utility, Granted
        Assert.Equal(((short)1, (Guid?)s.Conexao.Id, "CLI-100"), (linha.AtorTipo, linha.AtorId, linha.Cliente)); // quem registrou foi a conexão
        Assert.Equal("Aceite no cadastro do cliente (formulário 123)", linha.Evidencia);
        Assert.Equal(coleta.UtcTicks / 10, linha.Coleta.UtcTicks / 10);
        Assert.True(linha.Registro >= linha.Coleta);
        Assert.Null(linha.Sessao);
        Assert.Null(linha.Mensagem);

        // A auditoria já está NO OUTBOX (mesma transação) e ainda não na trilha; o handler a entrega depois.
        Assert.Equal(1, await AuditoriaNoOutboxAsync(s.Tenant));
        Assert.Empty(await TrilhaAsync(s.Tenant));
        Assert.Equal(1, await EntregarAuditoriaAsync(s.Api, s.Tenant));
        Assert.Equal(0, await EntregarAuditoriaAsync(s.Api, s.Tenant)); // nada pendente

        var trilha = Assert.Single(await TrilhaAsync(s.Tenant));
        Assert.Equal("consent.record", trilha.Operacao);
        Assert.Null(trilha.Ator);
        Assert.Equal("integration", trilha.Origem);
        Assert.Equal($"{linha.Id}:{s.OrgA:N}:UGI", trilha.Recurso); // finalidade, situação e origem em uma letra (o recurso da trilha tem até 100 caracteres)
        Assert.True(trilha.Recurso.Length <= 100);
        foreach (var segredo in new[] { "999990101", "CLI-100", "formulário", "Aceite" })
        {
            Assert.DoesNotContain(segredo, trilha.Recurso); // nem telefone, nem cliente, nem evidência
        }
    }

    [Fact]
    public async Task Repetir_o_mesmo_registro_e_idempotente_sem_linha_nova_e_sem_auditoria_nova_e_a_reentrega_da_auditoria_nao_duplica()
    {
        var s = await MontarAsync("e24-idem");
        await using var servidor = s.Api;

        Assert.Equal((true, "Granted", "Utility"), await OkAsync(await RegistrarAsync(s.Erp, Corpo("+" + TelA))));
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal((false, "Granted", "Utility"), await OkAsync(await RegistrarAsync(s.Erp, Corpo("+" + TelA))));
        }

        Assert.Single(await LinhasAsync(s.Tenant));
        Assert.Equal(1, await AuditoriaNoOutboxAsync(s.Tenant));

        // Retentativa do relay: o mesmo evento entregue de novo não duplica a trilha (a chave de idempotência é o evento).
        Assert.Equal(1, await EntregarAuditoriaAsync(s.Api, s.Tenant));
        Assert.Equal(1, await EntregarAuditoriaAsync(s.Api, s.Tenant, reentrega: true));
        Assert.Single(await TrilhaAsync(s.Tenant));
    }

    [Fact]
    public async Task Estado_efetivo_e_a_ultima_linha_por_ordem_de_gravacao_revogacao_e_novo_consentimento_e_registro_defasado_nunca_reativa()
    {
        var s = await MontarAsync("e24-estado");
        await using var servidor = s.Api;
        var t1 = Base;
        var t2 = Base.AddHours(1);
        var t3 = Base.AddHours(2);

        Assert.Equal((true, "Granted", "Utility"), await OkAsync(await RegistrarAsync(s.Erp, Corpo("+" + TelA, coleta: t1))));
        Assert.Equal((true, "Revoked", "Utility"), await OkAsync(await RegistrarAsync(s.Erp, Corpo("+" + TelA, situacao: "Revoked", coleta: t2))));

        // Uma repetição ATRASADA do consentimento (coleta anterior à revogação) e uma com a MESMA coleta da revogação não reativam: 409 e nada é gravado.
        foreach (var coletaDefasada in new[] { t1, t2 })
        {
            var (status, corpo) = await LerAsync(await RegistrarAsync(s.Erp, Corpo("+" + TelA, coleta: coletaDefasada)));
            Assert.Equal(HttpStatusCode.Conflict, status);
            Assert.Contains("consent-superseded", corpo);
        }

        Assert.Equal(2, (await LinhasAsync(s.Tenant)).Count);

        // Um novo consentimento coletado DEPOIS da revogação é aceito (e reativa).
        Assert.Equal((true, "Granted", "Utility"), await OkAsync(await RegistrarAsync(s.Erp, Corpo("+" + TelA, coleta: t3))));
        // A revogação nunca é recusada por data: mesmo com coleta antiga, o sentido seguro prevalece.
        Assert.Equal((true, "Revoked", "Utility"), await OkAsync(await RegistrarAsync(s.Erp, Corpo("+" + TelA, situacao: "Revoked", coleta: t1.AddMinutes(1)))));

        var linhas = await LinhasAsync(s.Tenant);
        Assert.Equal(new short[] { 1, 2, 1, 2 }, linhas.Select(l => l.Situacao).ToArray());
        Assert.Equal(linhas.Select(l => l.Seq).OrderBy(x => x), linhas.Select(l => l.Seq)); // a ordem de gravação é a ordem do seq

        // O estado efetivo pelo atendente é a ÚLTIMA linha por finalidade; a outra finalidade nunca foi tratada: sem linha (ausência não é consentimento).
        var estado = await LerJsonAsync(await s.Api.EnviarAsync(s.TokenDoGestor, HttpMethod.Get, $"/contact-center/items/{s.ItemA}/consents"));
        var efetivo = Assert.Single(estado.GetProperty("effective").EnumerateArray());
        Assert.Equal(("Utility", "Revoked", "IntegrationAsserted"), (efetivo.GetProperty("purpose").GetString(), efetivo.GetProperty("status").GetString(), efetivo.GetProperty("origin").GetString()));
        Assert.Equal(4, estado.GetProperty("history").GetArrayLength());
        Assert.Equal("Revoked", estado.GetProperty("history")[0].GetProperty("status").GetString()); // da mais nova para a mais antiga
    }

    [Fact]
    public async Task Finalidades_sao_independentes_e_o_consentimento_existe_para_quem_nunca_escreveu_sem_vinculo_nem_contato()
    {
        var s = await MontarAsync("e24-fin");
        await using var servidor = s.Api;

        Assert.Equal((true, "Granted", "Marketing"), await OkAsync(await RegistrarAsync(s.Erp, Corpo("+" + TelA, finalidade: "marketing", coleta: Base))));
        Assert.Equal((true, "Revoked", "Utility"), await OkAsync(await RegistrarAsync(s.Erp, Corpo("+" + TelA, situacao: "revoked", coleta: Base))));
        // Um telefone sem nenhum contato (nunca escreveu): o consentimento é gravado do mesmo jeito (o início ativo precisa dele antes do contato).
        Assert.Equal((true, "Granted", "Utility"), await OkAsync(await RegistrarAsync(s.Erp, Corpo("+5511988887777", coleta: Base))));

        var linhas = await LinhasAsync(s.Tenant);
        Assert.Equal(3, linhas.Count);
        Assert.All(linhas, l => Assert.Equal(s.OrgA, l.Org));

        var estado = await LerJsonAsync(await s.Api.EnviarAsync(s.TokenDoGestor, HttpMethod.Get, $"/contact-center/items/{s.ItemA}/consents"));
        var efetivas = estado.GetProperty("effective").EnumerateArray().ToDictionary(e => e.GetProperty("purpose").GetString()!, e => e.GetProperty("status").GetString());
        Assert.Equal(new Dictionary<string, string?> { ["Utility"] = "Revoked", ["Marketing"] = "Granted" }, efetivas); // independentes
    }

    [Fact]
    public async Task Vinculo_nao_e_consentimento_e_ausencia_de_consentimento_nao_e_autorizacao()
    {
        var s = await MontarAsync("e24-vinc");
        await using var servidor = s.Api;

        // Vincular o cliente ao contato (E2.2) não cria nenhuma linha de consentimento.
        using var vinculo = await s.Erp.EnviarAsync("/embed/v1/customer-links", new { customerRef = "CLI-100", phone = "+" + TelA }, metodo: HttpMethod.Put);
        Assert.Equal(HttpStatusCode.OK, vinculo.StatusCode);
        Assert.Empty(await LinhasAsync(s.Tenant));

        var estado = await LerJsonAsync(await s.Api.EnviarAsync(s.TokenDoGestor, HttpMethod.Get, $"/contact-center/items/{s.ItemA}/consents"));
        Assert.Equal(0, estado.GetProperty("effective").GetArrayLength()); // nenhuma finalidade "consentida" por inferência
        Assert.Equal(0, estado.GetProperty("history").GetArrayLength());
    }

    [Fact]
    public async Task Campos_obrigatorios_e_invalidos_sao_400_com_o_codigo_do_campo_e_nada_e_gravado_nem_auditado()
    {
        var s = await MontarAsync("e24-campos");
        await using var servidor = s.Api;

        var casos = new List<(object Corpo, string Codigo, string? Cliente)>
        {
            (Corpo("+" + TelA, evidencia: null), "evidence-required", null),
            (Corpo("+" + TelA, evidencia: ""), "evidence-required", null),
            (Corpo("+" + TelA, evidencia: "   "), "evidence-required", null),
            (Corpo("+" + TelA, evidencia: new string('e', 2001)), "evidence-too-long", null),
            (Corpo("+" + TelA, finalidade: null), "invalid-purpose", null),
            (Corpo("+" + TelA, finalidade: "Other"), "invalid-purpose", null),
            (Corpo("+" + TelA, finalidade: "1"), "invalid-purpose", null), // número não vale: só o nome
            (Corpo("+" + TelA, situacao: null), "invalid-status", null),
            (Corpo("+" + TelA, situacao: "Maybe"), "invalid-status", null),
            (Corpo("11999990101"), "invalid-phone", null), // sem código internacional
            (Corpo(null), "invalid-phone", null),
            (Corpo("+55abc"), "invalid-phone", null),
            (Corpo("+" + TelA, coleta: DateTimeOffset.UtcNow.AddHours(1)), "collected-at-invalid", null), // no futuro
            (Corpo("+" + TelA), "invalid-customer-ref", "%20%20"), // cliente em branco no caminho
            (Corpo("+" + TelA), "invalid-customer-ref", new string('c', 129)),
        };
        foreach (var (corpo, codigo, cliente) in casos)
        {
            var (status, texto) = await LerAsync(await RegistrarAsync(s.Erp, corpo, cliente ?? "CLI-100"));
            Assert.True(status == HttpStatusCode.BadRequest, $"{codigo}: esperado 400, veio {(int)status} {texto}");
            Assert.Contains(codigo, texto);
        }

        // A data da coleta ausente, sem fuso ou ilegível também é recusada (nunca interpretada por suposição).
        foreach (var coleta in new object?[] { null, "2026-11-01T10:00:00", "ontem", "01/11/2026", 12345 })
        {
            var corpo = Corpo("+" + TelA);
            corpo["collectedAt"] = coleta;
            var (status, texto) = await LerAsync(await RegistrarAsync(s.Erp, corpo));
            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.True(texto.Contains("collected-at-invalid") || texto.Contains("invalid-body"), texto);
        }

        var (statusDoLixo, corpoDoLixo) = await LerAsync(await s.Erp.EnviarAsync("/embed/v1/customers/CLI-100/consents", "não é um objeto", metodo: HttpMethod.Put));
        Assert.Equal(HttpStatusCode.BadRequest, statusDoLixo);
        Assert.Contains("invalid-body", corpoDoLixo);
        Assert.Equal(HttpStatusCode.BadRequest, (await LerAsync(await s.Erp.EnviarAsync("/embed/v1/customers/CLI-100/consents?x=1", Corpo("+" + TelA), metodo: HttpMethod.Put))).Status); // consulta não é assinada

        Assert.Empty(await LinhasAsync(s.Tenant));
        Assert.Equal(0, await AuditoriaNoOutboxAsync(s.Tenant));
    }

    [Fact]
    public async Task Organizacao_e_sempre_a_da_conexao_nunca_do_corpo_e_um_tenant_nunca_enxerga_o_consentimento_de_outro()
    {
        var s = await MontarAsync("e24-org");
        await using var servidor = s.Api;
        var outro = await MontarAsync("e24-org2", api: s.Api, telA: "5511999991111", telB: "5511999993333");

        // O corpo tenta apontar a organização B (e um tenant alheio): é ignorado; a linha vai para a organização da conexão.
        var corpo = Corpo("+" + TelB);
        corpo["organizationId"] = s.OrgB;
        corpo["tenantId"] = outro.Tenant;
        Assert.Equal((true, "Granted", "Utility"), await OkAsync(await RegistrarAsync(s.Erp, corpo)));
        var linha = Assert.Single(await LinhasAsync(s.Tenant));
        Assert.Equal(s.OrgA, linha.Org);
        Assert.Empty(await LinhasAsync(outro.Tenant));

        // O contato da organização B (item B) NÃO vê esse consentimento: a chave inclui a organização.
        var daB = await LerJsonAsync(await s.Api.EnviarAsync(s.TokenDoAdmin, HttpMethod.Get, $"/contact-center/items/{s.ItemB}/consents"));
        Assert.Equal(0, daB.GetProperty("effective").GetArrayLength());

        // Mesmo telefone, OUTRO tenant: linhas separadas, cada tenant só enxerga as suas.
        Assert.Equal((true, "Revoked", "Utility"), await OkAsync(await RegistrarAsync(outro.Erp, Corpo("+" + TelB, situacao: "Revoked"))));
        Assert.Single(await LinhasAsync(s.Tenant));
        Assert.Single(await LinhasAsync(outro.Tenant));
        Assert.Equal((short)1, (await LinhasAsync(s.Tenant))[0].Situacao);
    }

    [Fact]
    public async Task Falha_de_autenticacao_e_chave_revogada_ou_conexao_desativada_sao_o_mesmo_401_sem_efeito()
    {
        var s = await MontarAsync("e24-auth");
        await using var servidor = s.Api;
        var corpo = Corpo("+" + TelA);
        var nonce = "nonce-consent-12345";
        Assert.Equal(HttpStatusCode.OK, (await LerAsync(await s.Erp.EnviarAsync("/embed/v1/customers/CLI-100/consents", corpo, nonce: nonce, metodo: HttpMethod.Put))).Status);

        var tentativas = new List<Func<Task<HttpResponseMessage>>>
        {
            () => s.Erp.EnviarAsync("/embed/v1/customers/CLI-100/consents", corpo, nonce: nonce, metodo: HttpMethod.Put), // replay
            () => s.Erp.EnviarAsync("/embed/v1/customers/CLI-100/consents", corpo, segredoUsado: "ibsk_errado", metodo: HttpMethod.Put),
            () => s.Erp.EnviarAsync("/embed/v1/customers/CLI-100/consents", corpo, keyIdUsado: "ibk_" + Guid.NewGuid().ToString("N"), metodo: HttpMethod.Put),
            () => s.Erp.EnviarAsync("/embed/v1/customers/CLI-100/consents", corpo, instante: DateTimeOffset.UtcNow.AddMinutes(-10), metodo: HttpMethod.Put),
            () => s.Erp.EnviarAsync("/embed/v1/customers/CLI-100/consents", corpo, caminhoAssinado: "/embed/v1/customers/OUTRO/consents", metodo: HttpMethod.Put), // cliente do caminho adulterado
        };
        var corpos = new List<string>();
        foreach (var tentativa in tentativas)
        {
            var (status, texto) = await LerAsync(await tentativa());
            Assert.Equal(HttpStatusCode.Unauthorized, status);
            Assert.Contains("embedded-authentication-failed", texto);
            corpos.Add(Regex.Replace(texto, "\"traceId\":\"[^\"]*\"", string.Empty));
        }

        Assert.Single(corpos.Distinct()); // a mesma recusa, sem revelar qual verificação falhou

        // Sem assinatura e com Bearer de usuário: também 401, nunca efeito (a rota é só do servidor do sistema externo).
        using var semAssinatura = new HttpRequestMessage(HttpMethod.Put, "/embed/v1/customers/CLI-100/consents") { Content = JsonContent.Create(corpo) };
        using var resposta = await s.Api.Client.SendAsync(semAssinatura);
        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await s.Api.StatusAsync(s.TokenDoAdmin, HttpMethod.Put, "/embed/v1/customers/CLI-100/consents", corpo));
        Assert.Single(await LinhasAsync(s.Tenant)); // só o registro legítimo

        // Conexão desativada: perde o acesso (401) e nada é gravado.
        Assert.Equal(HttpStatusCode.OK, await s.Api.StatusAsync(s.TokenDoAdmin, HttpMethod.Post, $"/embedded/connections/{s.Conexao.Id}/disable"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await LerAsync(await RegistrarAsync(s.Erp, Corpo("+" + TelA, situacao: "Revoked", coleta: Base.AddHours(1))))).Status);
        Assert.Single(await LinhasAsync(s.Tenant));
    }

    [Fact]
    public async Task Cliente_com_espaco_e_acento_no_caminho_e_assinado_como_enviado_codificado_e_gravado_decodificado()
    {
        var s = await MontarAsync("e24-enc");
        await using var servidor = s.Api;

        // O sistema externo assina o caminho COMO ENVIA (codificado em porcentagem); a referência gravada é a decodificada.
        Assert.Equal((true, "Granted", "Utility"), await OkAsync(await RegistrarAsync(s.Erp, Corpo("+" + TelA), "JO%C3%83O%207")));
        var linha = Assert.Single(await LinhasAsync(s.Tenant));
        Assert.Equal("JOÃO 7", linha.Cliente);

        // Assinar a forma decodificada NÃO vale (o contrato é o caminho enviado): 401, sem efeito.
        var (status, _) = await LerAsync(await s.Erp.EnviarAsync("/embed/v1/customers/JO%C3%83O%207/consents", Corpo("+" + TelA, situacao: "Revoked", coleta: Base.AddHours(1)), caminhoAssinado: "/embed/v1/customers/JOÃO 7/consents", metodo: HttpMethod.Put));
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Single(await LinhasAsync(s.Tenant));
    }

    // Condição da ratificação C (E2.4): a representação assinada do caminho é inequívoca. Caminho ambíguo é recusado ANTES da autenticação; a caixa da codificação hexadecimal vale como enviada.

    [Theory]
    [InlineData("/embed/v1/customers/CLI-100/consents", false)]
    [InlineData("/embed/v1/customers/JO%C3%83O%207/consents", false)]
    [InlineData("/embed/v1/customers/jo%c3%a3o/consents", false)] // a caixa do hexadecimal não é normalizada: vale a enviada
    [InlineData("/embed/v1/customers/a.b/consents", false)]
    [InlineData("/embed/v1/customers/.../consents", true)] // só pontos
    [InlineData("/embed/v1/customers/./consents", true)]
    [InlineData("/embed/v1/customers/../consents", true)]
    [InlineData("/embed/v1/customers/%2e%2E/consents", true)] // ponto codificado
    [InlineData("/embed/v1/customers/a%2Fb/consents", true)] // barra codificada
    [InlineData("/embed/v1/customers/a%2fb/consents", true)]
    [InlineData("/embed/v1/customers/a%5Cb/consents", true)] // contrabarra codificada
    [InlineData("/embed/v1/customers/a\b/consents", true)]
    [InlineData("/embed/v1/customers/a/b/consents", true)] // dois segmentos
    [InlineData("/embed/v1/customers//consents", true)] // vazio
    [InlineData("/embed/v1/customers/a%00b/consents", true)] // NUL
    [InlineData("/embed/v1/customers/a%0Ab/consents", true)] // quebra de linha
    [InlineData("/embed/v1/customers/a%7Fb/consents", true)] // DEL
    [InlineData("/embed/v1/customers/a%2/consents", true)] // porcentagem truncada
    [InlineData("/embed/v1/customers/a%ZZb/consents", true)] // porcentagem malformada
    [InlineData("/embed/v1/customers/a%/consents", true)]
    [InlineData("/embed/v1/customers/a\u0007b/consents", true)] // controle literal
    [InlineData("/embed/v1/customers/CLI-100/consents/", true)] // barra final
    [InlineData("/embed/v1/customers/CLI-100/consent", true)]
    [InlineData("/embed/v1/customers/consents", true)]
    [InlineData("/EMBED/v1/customers/CLI-100/consents", true)] // a caixa do caminho não é normalizada
    public void A_representacao_assinada_do_caminho_e_inequivoca_e_o_ambiguo_e_recusado(string caminho, bool ambiguo) =>
        Assert.Equal(ambiguo, IaBimeta.Conversations.Presentation.ConversationsIntegrationEndpoints.AmbiguousConsentPath(caminho));

    [Fact]
    public async Task Caminho_ambiguo_e_400_antes_da_autenticacao_sem_efeito_e_a_caixa_do_hexadecimal_e_a_enviada()
    {
        var s = await MontarAsync("e24-amb");
        await using var servidor = s.Api;

        foreach (var cliente in new[] { "a%2Fb", "a%2fb", "a%5Cb", "a%00b", "a%0Ab" })
        {
            var (status, texto) = await LerAsync(await RegistrarAsync(s.Erp, Corpo("+" + TelA), cliente));
            Assert.True(status == HttpStatusCode.BadRequest, $"{cliente}: esperado 400, veio {(int)status} {texto}");
            // O servidor pode recusar o controle codificado antes da aplicação (400 sem corpo); a barra e a contrabarra chegam à regra e trazem o código.
            Assert.True(texto.Contains("invalid-path") || (cliente.Contains("%00") || cliente.Contains("%0A")), texto);
        }

        // Sem assinatura também é 400 (a recusa é anterior à autenticação e não revela nada): nenhuma informação sobre chaves ou clientes.
        using var semAssinatura = new HttpRequestMessage(HttpMethod.Put, "/embed/v1/customers/a%2Fb/consents") { Content = JsonContent.Create(Corpo("+" + TelA)) };
        using var resposta = await s.Api.Client.SendAsync(semAssinatura);
        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.Empty(await LinhasAsync(s.Tenant));
        Assert.Equal(0, await AuditoriaNoOutboxAsync(s.Tenant));

        // A caixa do hexadecimal é CANONICALIZADA para maiúsculas: assinar a forma canônica funciona mesmo enviando minúsculas; assinar as minúsculas não confere (401), seja qual for a normalização do cliente HTTP.
        var (statusMinusculas, _) = await LerAsync(await RegistrarAsync(s.Erp, Corpo("+" + TelA), "jo%c3%a3o"));
        Assert.Equal(HttpStatusCode.Unauthorized, statusMinusculas);
        Assert.Empty(await LinhasAsync(s.Tenant));
        var (statusCanonica, _) = await LerAsync(await s.Erp.EnviarAsync("/embed/v1/customers/jo%c3%a3o/consents", Corpo("+" + TelA), caminhoAssinado: "/embed/v1/customers/jo%C3%A3o/consents", metodo: HttpMethod.Put));
        Assert.Equal(HttpStatusCode.OK, statusCanonica);
        Assert.Equal("joão", Assert.Single(await LinhasAsync(s.Tenant)).Cliente);
    }

    [Theory]
    [InlineData("/embed/v1/customers/CLI-100/consents", "/embed/v1/customers/CLI-100/consents")]
    [InlineData("/embed/v1/customers/jo%c3%a3o/consents", "/embed/v1/customers/jo%C3%A3o/consents")]
    [InlineData("/embed/v1/customers/a%2fb%5c/consents", "/embed/v1/customers/a%2Fb%5C/consents")]
    [InlineData("/embed/v1/customers/100%/consents", "/embed/v1/customers/100%/consents")] // porcentagem solta: nada a canonicalizar (e depois é recusada como ambígua)
    [InlineData("/embed/v1/customers/%zz/consents", "/embed/v1/customers/%zz/consents")]
    [InlineData("/embed/v1/launch-tickets", "/embed/v1/launch-tickets")] // as rotas anteriores não mudam
    public void A_canonicalizacao_so_poe_o_hexadecimal_em_maiusculas_e_nao_toca_em_mais_nada(string enviado, string canonico) =>
        Assert.Equal(canonico, IaBimeta.Conversations.Presentation.ConversationsIntegrationEndpoints.CanonicalizePercentEncoding(enviado));

    // Ressalva da E2.2 para o gate (E2.6): falha de INFRAESTRUTURA do autenticador é 503 (repetir é seguro) e nunca 401; credencial errada continua 401.
    [Fact]
    public async Task Falha_operacional_do_autenticador_e_503_sem_efeito_e_credencial_errada_continua_401()
    {
        var s = await MontarAsync("e26-503");
        await using var servidor = s.Api;
        var nome = "e26_" + Guid.NewGuid().ToString("N")[..8];
        await ExecutarAsync(
            $@"CREATE FUNCTION identity.fn_{nome}() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'falha injetada pelo teste'; END $$;
               CREATE TRIGGER tg_{nome} BEFORE INSERT ON identity.integration_nonces FOR EACH ROW EXECUTE FUNCTION identity.fn_{nome}();");
        try
        {
            var (indisponivel, corpo) = await LerAsync(await RegistrarAsync(s.Erp, Corpo("+" + TelA)));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, indisponivel);
            Assert.Contains("embedded-busy", corpo);
            Assert.DoesNotContain("falha injetada", corpo); // sem detalhe da falha
            // Credencial errada nunca vira 503: o veredito de credencial é 401 mesmo com a infraestrutura degradada.
            Assert.Equal(HttpStatusCode.Unauthorized, (await LerAsync(await s.Erp.EnviarAsync("/embed/v1/customers/CLI-100/consents", Corpo("+" + TelA), segredoUsado: "ibsk_errado", metodo: HttpMethod.Put))).Status);
        }
        finally
        {
            await ExecutarAsync($"DROP TRIGGER IF EXISTS tg_{nome} ON identity.integration_nonces; DROP FUNCTION IF EXISTS identity.fn_{nome}();");
        }

        Assert.Empty(await LinhasAsync(s.Tenant)); // nada gravado: repetir é seguro
        Assert.Equal((true, "Granted", "Utility"), await OkAsync(await RegistrarAsync(s.Erp, Corpo("+" + TelA))));
    }

    // P1 da 1.ª auditoria da E2 (Codex): a sequência exata, pelo sistema externo (HMAC), na mesma organização, telefone e finalidade.
    [Fact]
    public async Task Revoked_12h_Revoked_10h_Granted_11h_termina_revogado_e_a_concessao_e_recusada()
    {
        var s = await MontarAsync("e26-ordem");
        await using var servidor = s.Api;
        var h12 = Base.AddHours(12);
        var h11 = Base.AddHours(11);
        var h10 = Base.AddHours(10);

        Assert.Equal((true, "Revoked", "Utility"), await OkAsync(await RegistrarAsync(s.Erp, Corpo("+" + TelA, situacao: "Revoked", coleta: h12))));
        Assert.Equal((true, "Revoked", "Utility"), await OkAsync(await RegistrarAsync(s.Erp, Corpo("+" + TelA, situacao: "Revoked", coleta: h10)))); // antiga, aceita
        var (status, corpo) = await LerAsync(await RegistrarAsync(s.Erp, Corpo("+" + TelA, coleta: h11)));
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("consent-superseded", corpo);

        var linhas = await LinhasAsync(s.Tenant);
        Assert.Equal(new short[] { 2, 2 }, linhas.Select(l => l.Situacao).ToArray()); // nenhuma concessão gravada
        var estado = await LerJsonAsync(await s.Api.EnviarAsync(s.TokenDoGestor, HttpMethod.Get, $"/contact-center/items/{s.ItemA}/consents"));
        Assert.Equal("Revoked", Assert.Single(estado.GetProperty("effective").EnumerateArray()).GetProperty("status").GetString());

        // Reentrega idêntica de uma revogação é idempotente (a última linha é a de 10h) e uma concessão posterior a 12h é aceita.
        Assert.Equal((false, "Revoked", "Utility"), await OkAsync(await RegistrarAsync(s.Erp, Corpo("+" + TelA, situacao: "Revoked", coleta: h10))));
        Assert.Equal((true, "Granted", "Utility"), await OkAsync(await RegistrarAsync(s.Erp, Corpo("+" + TelA, coleta: Base.AddHours(13)))));
    }

    // ------------------------------------------------------------------ atendente e supervisor

    [Fact]
    public async Task Gestor_registra_para_o_telefone_do_contato_do_item_com_origem_autor_e_sessao_e_consulta_com_evidencia_so_no_efetivo()
    {
        var s = await MontarAsync("e24-ag");
        await using var servidor = s.Api;
        var longa = "Confirmado por telefone em 12/11 com o titular, que pediu o aviso de resultados; " + new string('x', 200);

        // O telefone do corpo é IGNORADO: vale o do contato do item (nunca campo livre).
        var pedido = new Dictionary<string, object?>
        {
            ["purpose"] = "Utility", ["status"] = "Granted", ["evidence"] = longa, ["collectedAt"] = Iso(Base), ["phone"] = "+5511900000000", ["organizationId"] = s.OrgB,
        };
        Assert.Equal((true, "Granted", "Utility"), await OkAsync(await s.Api.EnviarAsync(s.TokenDoGestor, HttpMethod.Post, $"/contact-center/items/{s.ItemA}/consents", pedido)));

        var linha = Assert.Single(await LinhasAsync(s.Tenant));
        Assert.Equal(("+" + TelA, s.OrgA, (short)2, (short)2), (linha.Fone, linha.Org, linha.Origem, linha.AtorTipo)); // AgentRecorded por usuário
        Assert.Equal(s.GestorId, linha.AtorId);
        Assert.Equal(s.SessaoA, linha.Sessao);

        // Repetir é idempotente.
        Assert.Equal((false, "Granted", "Utility"), await OkAsync(await s.Api.EnviarAsync(s.TokenDoGestor, HttpMethod.Post, $"/contact-center/items/{s.ItemA}/consents", pedido)));
        Assert.Single(await LinhasAsync(s.Tenant));

        // A consulta: o estado efetivo traz a evidência completa; o histórico, só o resumo.
        var estado = await LerJsonAsync(await s.Api.EnviarAsync(s.TokenDoGestor, HttpMethod.Get, $"/contact-center/items/{s.ItemA}/consents"));
        Assert.Equal("+" + TelA, estado.GetProperty("phone").GetString());
        Assert.Equal(longa, estado.GetProperty("effective")[0].GetProperty("evidence").GetString());
        var resumo = estado.GetProperty("history")[0].GetProperty("evidence").GetString()!;
        Assert.True(resumo.Length < longa.Length);
        Assert.StartsWith(longa[..80], resumo);

        await EntregarAuditoriaAsync(s.Api, s.Tenant);
        var trilha = Assert.Single(await TrilhaAsync(s.Tenant));
        Assert.Equal(("consent.record", (Guid?)s.GestorId), (trilha.Operacao, trilha.Ator));
        Assert.DoesNotContain("999990101", trilha.Recurso);
        Assert.DoesNotContain("Confirmado", trilha.Recurso);

        // O administrador do tenant (ConsentManage) também registra; a linha é dele.
        Assert.Equal((true, "Revoked", "Utility"), await OkAsync(await s.Api.EnviarAsync(s.TokenDoAdmin, HttpMethod.Post, $"/contact-center/items/{s.ItemA}/consents", PedidoDoAtendente(situacao: "Revoked", coleta: Base.AddHours(1)))));
    }

    [Fact]
    public async Task Sem_ConsentManage_403_atendente_comum_e_toda_sessao_embutida_inclusive_de_gestor_sem_alterar_o_estado()
    {
        var s = await MontarAsync("e24-perm");
        await using var servidor = s.Api;
        var rotaPost = $"/contact-center/items/{s.ItemA}/consents";
        var rotaGet = $"/contact-center/items/{s.ItemA}/consents";
        var rotaResolve = $"/contact-center/items/{s.ItemA}/optout-reviews/{Guid.NewGuid()}";

        // Atendente comum da fila (vê o item, não tem ConsentManage): 403 em tudo.
        Assert.Equal(HttpStatusCode.Forbidden, await s.Api.StatusAsync(s.TokenDoAtendente, HttpMethod.Post, rotaPost, PedidoDoAtendente()));
        Assert.Equal(HttpStatusCode.Forbidden, await s.Api.StatusAsync(s.TokenDoAtendente, HttpMethod.Get, rotaGet));
        Assert.Equal(HttpStatusCode.Forbidden, await s.Api.StatusAsync(s.TokenDoAtendente, HttpMethod.Get, $"/contact-center/items/{s.ItemA}/optout-reviews"));
        Assert.Equal(HttpStatusCode.Forbidden, await s.Api.StatusAsync(s.TokenDoAtendente, HttpMethod.Post, rotaResolve, new { resolution = "Revoked" }));

        // Sessão embutida do atendente federado e a de um GESTOR mapeado: a permissão não faz parte do Embedded e a rota não consta da lista embutida: 403.
        foreach (var token in new[] { s.TrocaAtendente.Token, s.TrocaGestor.Token })
        {
            Assert.Equal(HttpStatusCode.Forbidden, await s.Api.StatusAsync(token, HttpMethod.Post, rotaPost, PedidoDoAtendente()));
            Assert.Equal(HttpStatusCode.Forbidden, await s.Api.StatusAsync(token, HttpMethod.Get, rotaGet));
            Assert.Equal(HttpStatusCode.Forbidden, await s.Api.StatusAsync(token, HttpMethod.Get, $"/contact-center/items/{s.ItemA}/optout-reviews"));
            Assert.Equal(HttpStatusCode.Forbidden, await s.Api.StatusAsync(token, HttpMethod.Post, rotaResolve, new { resolution = "Kept" }));
        }

        // Sem credencial: 401.
        using var anonima = new HttpRequestMessage(HttpMethod.Post, rotaPost) { Content = JsonContent.Create(PedidoDoAtendente()) };
        using var resposta = await s.Api.Client.SendAsync(anonima);
        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);

        Assert.Empty(await LinhasAsync(s.Tenant));
        Assert.Equal(0, await AuditoriaNoOutboxAsync(s.Tenant));
    }

    [Fact]
    public async Task Item_fora_do_escopo_ou_inexistente_e_404_uniforme_e_nao_permite_enumerar_contatos_de_outra_organizacao()
    {
        var s = await MontarAsync("e24-404");
        await using var servidor = s.Api;
        // O gestor da organização A não enxerga o item da organização B: 404, igual ao inexistente.
        var respostas = new List<(HttpStatusCode Status, string Corpo)>
        {
            await LerAsync(await s.Api.EnviarAsync(s.TokenDoGestor, HttpMethod.Post, $"/contact-center/items/{s.ItemB}/consents", PedidoDoAtendente())),
            await LerAsync(await s.Api.EnviarAsync(s.TokenDoGestor, HttpMethod.Post, $"/contact-center/items/{Guid.NewGuid()}/consents", PedidoDoAtendente())),
        };
        Assert.All(respostas, r => Assert.Equal(HttpStatusCode.NotFound, r.Status));
        Assert.Equal(HttpStatusCode.NotFound, await s.Api.StatusAsync(s.TokenDoGestor, HttpMethod.Get, $"/contact-center/items/{s.ItemB}/consents"));
        Assert.Equal(HttpStatusCode.NotFound, await s.Api.StatusAsync(s.TokenDoGestor, HttpMethod.Get, $"/contact-center/items/{Guid.NewGuid()}/consents"));
        Assert.Equal(HttpStatusCode.NotFound, await s.Api.StatusAsync(s.TokenDoGestor, HttpMethod.Get, $"/contact-center/items/{s.ItemB}/optout-reviews"));
        Assert.Equal(HttpStatusCode.NotFound, await s.Api.StatusAsync(s.TokenDoGestor, HttpMethod.Post, $"/contact-center/items/{s.ItemB}/optout-reviews/{Guid.NewGuid()}", new { resolution = "Kept" }));
        Assert.Empty(await LinhasAsync(s.Tenant));
    }

    [Fact]
    public async Task Contato_de_referencia_opaca_e_409_e_limite_de_consulta_invalido_e_400()
    {
        var s = await MontarAsync("e24-opaco");
        await using var servidor = s.Api;

        var (status, corpo) = await LerAsync(await s.Api.EnviarAsync(s.TokenDoGestor, HttpMethod.Post, $"/contact-center/items/{s.ItemOpaco}/consents", PedidoDoAtendente()));
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("consent-phone-unavailable", corpo);
        Assert.DoesNotContain(RefOpaca, corpo); // a referência opaca nunca volta
        var (statusGet, corpoGet) = await LerAsync(await s.Api.EnviarAsync(s.TokenDoGestor, HttpMethod.Get, $"/contact-center/items/{s.ItemOpaco}/consents"));
        Assert.Equal(HttpStatusCode.Conflict, statusGet);
        Assert.Contains("consent-phone-unavailable", corpoGet);

        foreach (var limite in new[] { 0, 101, -1 })
        {
            var (statusLimite, corpoLimite) = await LerAsync(await s.Api.EnviarAsync(s.TokenDoGestor, HttpMethod.Get, $"/contact-center/items/{s.ItemA}/consents?limit={limite}"));
            Assert.Equal(HttpStatusCode.BadRequest, statusLimite);
            Assert.Contains("invalid-limit", corpoLimite);
        }

        Assert.Equal(HttpStatusCode.OK, await s.Api.StatusAsync(s.TokenDoGestor, HttpMethod.Get, $"/contact-center/items/{s.ItemA}/consents?limit=100"));
        Assert.Empty(await LinhasAsync(s.Tenant));
    }

    [Fact]
    public async Task Campos_invalidos_do_atendente_sao_400_com_o_codigo_e_nada_e_gravado()
    {
        var s = await MontarAsync("e24-ag-campos");
        await using var servidor = s.Api;
        var rota = $"/contact-center/items/{s.ItemA}/consents";

        var casos = new (object Corpo, string Codigo)[]
        {
            (PedidoDoAtendente(evidencia: null), "evidence-required"),
            (PedidoDoAtendente(evidencia: " "), "evidence-required"),
            (PedidoDoAtendente(evidencia: new string('e', 2001)), "evidence-too-long"),
            (PedidoDoAtendente(finalidade: "Todas"), "invalid-purpose"),
            (PedidoDoAtendente(situacao: "Talvez"), "invalid-status"),
            (PedidoDoAtendente(coleta: DateTimeOffset.UtcNow.AddHours(2)), "collected-at-invalid"),
            (new { purpose = "Utility", status = "Granted", evidence = "x" }, "collected-at-invalid"), // sem data
        };
        foreach (var (corpo, codigo) in casos)
        {
            var (status, texto) = await LerAsync(await s.Api.EnviarAsync(s.TokenDoGestor, HttpMethod.Post, rota, corpo));
            Assert.True(status == HttpStatusCode.BadRequest, $"{codigo}: esperado 400, veio {(int)status} {texto}");
            Assert.Contains(codigo, texto);
        }

        Assert.Empty(await LinhasAsync(s.Tenant));
        Assert.Equal(0, await AuditoriaNoOutboxAsync(s.Tenant));
    }

    // ------------------------------------------------------------------ concorrência, falha durante a gravação e imutabilidade

    [Fact]
    public async Task Registros_concorrentes_identicos_gravam_uma_linha_e_so_um_recorded_true()
    {
        var s = await MontarAsync("e24-conc1");
        await using var servidor = s.Api;

        var respostas = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(async () => await LerAsync(await RegistrarAsync(s.Erp, Corpo("+" + TelA))))));

        Assert.All(respostas, r => Assert.Equal(HttpStatusCode.OK, r.Status));
        Assert.Equal(1, respostas.Count(r => r.Corpo.Contains("\"recorded\":true")));
        Assert.Single(await LinhasAsync(s.Tenant));
        Assert.Equal(1, await AuditoriaNoOutboxAsync(s.Tenant));
    }

    [Fact]
    public async Task Registros_concorrentes_de_situacoes_diferentes_nunca_falham_e_cada_resposta_corresponde_a_uma_linha_na_ordem_do_seq()
    {
        var s = await MontarAsync("e24-conc2");
        await using var servidor = s.Api;
        await OkAsync(await RegistrarAsync(s.Erp, Corpo("+" + TelA, coleta: Base)));

        // Revogações e novos consentimentos misturados, com coletas diferentes, em paralelo (ERP e atendente juntos).
        var tarefas = Enumerable.Range(1, 12).Select(i => Task.Run(async () =>
        {
            var concedido = i % 2 == 0;
            var coleta = Base.AddMinutes(i);
            return i % 3 == 0
                ? await LerAsync(await s.Api.EnviarAsync(s.TokenDoGestor, HttpMethod.Post, $"/contact-center/items/{s.ItemA}/consents", PedidoDoAtendente(situacao: concedido ? "Granted" : "Revoked", coleta: coleta)))
                : await LerAsync(await RegistrarAsync(s.Erp, Corpo("+" + TelA, situacao: concedido ? "Granted" : "Revoked", coleta: coleta)));
        })).ToArray();
        var respostas = await Task.WhenAll(tarefas);

        Assert.All(respostas, r => Assert.True(r.Status is HttpStatusCode.OK or HttpStatusCode.Conflict, $"{(int)r.Status} {r.Corpo}")); // 200, ou 409 de registro defasado: nunca 5xx
        var gravadas = respostas.Count(r => r.Corpo.Contains("\"recorded\":true"));
        var linhas = await LinhasAsync(s.Tenant);
        Assert.Equal(1 + gravadas, linhas.Count);
        Assert.Equal(linhas.Count, linhas.Select(l => l.Seq).Distinct().Count());

        // Invariante do estado efetivo: nenhum Granted foi gravado com coleta que não seja posterior à revogação vigente NAQUELE momento.
        foreach (var (linha, indice) in linhas.Select((l, i) => (l, i)))
        {
            if (linha.Situacao != 1)
            {
                continue;
            }

            var anteriores = linhas.Take(indice).Where(a => a.Finalidade == linha.Finalidade).ToList();
            var ultima = anteriores.LastOrDefault();
            if (ultima is { Situacao: 2 })
            {
                Assert.True(linha.Coleta > ultima.Coleta, "Um consentimento defasado reativou a revogação.");
            }
        }

        Assert.Equal(linhas.Count, await AuditoriaNoOutboxAsync(s.Tenant)); // uma auditoria por linha gravada (a semente também)
    }

    [Fact]
    public async Task Falha_durante_a_gravacao_desfaz_a_linha_e_a_auditoria_e_a_retentativa_confirma_uma_so_vez()
    {
        var s = await MontarAsync("e24-falha");
        await using var servidor = s.Api;

        // (1) A falha ao gravar a auditoria desfaz também a linha do consentimento.
        var nome = "e24_" + Guid.NewGuid().ToString("N")[..8];
        await ExecutarAsync(
            $@"CREATE FUNCTION conversations.fn_{nome}() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'falha injetada pelo teste'; END $$;
               CREATE TRIGGER tg_{nome} BEFORE INSERT ON conversations.outbox_message FOR EACH ROW WHEN (NEW.tenant_id = '{s.Tenant}'::uuid AND NEW.event_type = 'conversations.audit-requested') EXECUTE FUNCTION conversations.fn_{nome}();");
        try
        {
            var (status, _) = await LerAsync(await RegistrarAsync(s.Erp, Corpo("+" + TelA)));
            Assert.True((int)status >= 500, $"A falha deveria ser 5xx, foi {(int)status}.");
            var (statusAtendente, _) = await LerAsync(await s.Api.EnviarAsync(s.TokenDoGestor, HttpMethod.Post, $"/contact-center/items/{s.ItemA}/consents", PedidoDoAtendente()));
            Assert.True((int)statusAtendente >= 500);
        }
        finally
        {
            await ExecutarAsync($"DROP TRIGGER IF EXISTS tg_{nome} ON conversations.outbox_message; DROP FUNCTION IF EXISTS conversations.fn_{nome}();");
        }

        Assert.Empty(await LinhasAsync(s.Tenant));
        Assert.Equal(0, await AuditoriaNoOutboxAsync(s.Tenant));

        // (2) A falha ao gravar a LINHA não deixa auditoria espúria.
        var nome2 = "e24b_" + Guid.NewGuid().ToString("N")[..8];
        await ExecutarAsync(
            $@"CREATE FUNCTION conversations.fn_{nome2}() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'falha injetada pelo teste'; END $$;
               CREATE TRIGGER tg_{nome2} BEFORE INSERT ON conversations.contact_consents FOR EACH ROW WHEN (NEW.tenant_id = '{s.Tenant}'::uuid) EXECUTE FUNCTION conversations.fn_{nome2}();");
        try
        {
            var (status, _) = await LerAsync(await RegistrarAsync(s.Erp, Corpo("+" + TelA)));
            Assert.True((int)status >= 500);
        }
        finally
        {
            await ExecutarAsync($"DROP TRIGGER IF EXISTS tg_{nome2} ON conversations.contact_consents; DROP FUNCTION IF EXISTS conversations.fn_{nome2}();");
        }

        Assert.Empty(await LinhasAsync(s.Tenant));
        Assert.Equal(0, await AuditoriaNoOutboxAsync(s.Tenant));

        // Sem a falha, a retentativa do MESMO pedido confirma uma única linha e uma única auditoria, e uma nova repetição é no-op.
        Assert.Equal((true, "Granted", "Utility"), await OkAsync(await RegistrarAsync(s.Erp, Corpo("+" + TelA))));
        Assert.Equal((false, "Granted", "Utility"), await OkAsync(await RegistrarAsync(s.Erp, Corpo("+" + TelA))));
        Assert.Single(await LinhasAsync(s.Tenant));
        Assert.Equal(1, await AuditoriaNoOutboxAsync(s.Tenant));
    }

    [Fact]
    public async Task Historico_e_imutavel_o_banco_recusa_update_e_delete_e_o_repositorio_nao_tem_caminho_de_alteracao()
    {
        var s = await MontarAsync("e24-imut");
        await using var servidor = s.Api;
        await OkAsync(await RegistrarAsync(s.Erp, Corpo("+" + TelA)));

        var update = await Assert.ThrowsAsync<PostgresException>(() => ExecutarAsync($"update conversations.contact_consents set status = 2 where tenant_id = '{s.Tenant}'::uuid"));
        var delete = await Assert.ThrowsAsync<PostgresException>(() => ExecutarAsync($"delete from conversations.contact_consents where tenant_id = '{s.Tenant}'::uuid"));
        Assert.Contains("somente de acrescimo", update.MessageText + delete.MessageText, StringComparison.OrdinalIgnoreCase);
        var linha = Assert.Single(await LinhasAsync(s.Tenant));
        Assert.Equal((short)1, linha.Situacao);

        // Nenhuma rota de alteração ou exclusão do consentimento existe: o método não está mapeado.
        var rota = $"/contact-center/items/{s.ItemA}/consents";
        foreach (var metodo in new[] { HttpMethod.Put, HttpMethod.Delete, HttpMethod.Patch })
        {
            Assert.True((int)await s.Api.StatusAsync(s.TokenDoGestor, metodo, rota, PedidoDoAtendente()) is 404 or 405);
        }

        Assert.True((int)(await LerAsync(await s.Erp.EnviarAsync("/embed/v1/customers/CLI-100/consents", Corpo("+" + TelA), metodo: HttpMethod.Delete))).Status is 404 or 405 or 401);
        Assert.Single(await LinhasAsync(s.Tenant));
    }

    // ------------------------------------------------------------------ PossibleOptOut: revisão e resolução

    [Fact]
    public async Task Resolver_como_Revoked_revoga_todas_as_finalidades_do_telefone_audita_e_a_mesma_decisao_repetida_e_idempotente()
    {
        var s = await MontarAsync("e24-rev");
        await using var servidor = s.Api;
        var mensagem = await SemearOcorrenciaAsync(s.Tenant, s.OrgA, s.SessaoA, motivo: 1, palavra: "CANCELAR");
        var rota = $"/contact-center/items/{s.ItemA}/optout-reviews";
        // A finalidade Marketing já estava revogada pelo cliente anteriormente.
        await OkAsync(await RegistrarAsync(s.Erp, Corpo("+" + TelA, finalidade: "Utility", coleta: Base)));
        await OkAsync(await RegistrarAsync(s.Erp, Corpo("+" + TelA, finalidade: "Marketing", situacao: "Revoked", coleta: Base)));

        var pendentes = await LerJsonAsync(await s.Api.EnviarAsync(s.TokenDoGestor, HttpMethod.Get, rota));
        var pendente = Assert.Single(pendentes.EnumerateArray());
        Assert.Equal((mensagem, "FlowFreeText", "CANCELAR"), (pendente.GetProperty("messageId").GetGuid(), pendente.GetProperty("reason").GetString(), pendente.GetProperty("keyword").GetString()));
        Assert.Equal(JsonValueKind.Null, pendente.GetProperty("resolution").ValueKind);

        var resolvida = await LerJsonAsync(await s.Api.EnviarAsync(s.TokenDoGestor, HttpMethod.Post, $"{rota}/{mensagem}", new { resolution = "Revoked" }));
        Assert.True(resolvida.GetProperty("resolved").GetBoolean());
        Assert.Equal("Revoked", resolvida.GetProperty("resolution").GetString());

        // Só a finalidade que ainda não estava revogada ganhou linha nova (Utility); Marketing já estava: nada a acrescentar.
        var linhas = await LinhasAsync(s.Tenant);
        Assert.Equal(3, linhas.Count);
        var nova = linhas[^1];
        Assert.Equal(((short)1, (short)2, (short)2, (short)2), (nova.Finalidade, nova.Situacao, nova.Origem, nova.AtorTipo)); // Utility, Revoked, AgentRecorded, usuário
        Assert.Equal((s.GestorId, s.SessaoA, mensagem, s.OrgA), (nova.AtorId, nova.Sessao, nova.Mensagem, nova.Org));
        Assert.Contains("optout-review", nova.Evidencia);
        Assert.Contains("CANCELAR", nova.Evidencia);

        var depois = await LerJsonAsync(await s.Api.EnviarAsync(s.TokenDoGestor, HttpMethod.Get, rota));
        var decidida = Assert.Single(depois.EnumerateArray());
        Assert.Equal(("Revoked", s.GestorId), (decidida.GetProperty("resolution").GetString(), decidida.GetProperty("resolvedBy").GetGuid()));
        Assert.NotEqual(JsonValueKind.Null, decidida.GetProperty("resolvedAt").ValueKind);

        // A mesma decisão repetida é idempotente: nada novo, e a auditoria não cresce.
        var antes = (await LinhasAsync(s.Tenant)).Count;
        var auditoriaAntes = await AuditoriaNoOutboxAsync(s.Tenant);
        var repetida = await LerJsonAsync(await s.Api.EnviarAsync(s.TokenDoGestor, HttpMethod.Post, $"{rota}/{mensagem}", new { resolution = "Revoked" }));
        Assert.False(repetida.GetProperty("resolved").GetBoolean());
        Assert.Equal(antes, (await LinhasAsync(s.Tenant)).Count);
        Assert.Equal(auditoriaAntes, await AuditoriaNoOutboxAsync(s.Tenant));

        // Auditoria exata: o registro da revogação (Utility) e a resolução; sem telefone, sem texto, sem referência do cliente.
        await EntregarAuditoriaAsync(s.Api, s.Tenant);
        var trilha = (await TrilhaAsync(s.Tenant)).Where(t => t.Operacao == "consent.possible-optout-resolve").ToList();
        var resolucao = Assert.Single(trilha);
        Assert.Equal(s.GestorId, resolucao.Ator);
        Assert.EndsWith(":Revoked", resolucao.Recurso);
        Assert.DoesNotContain("999990101", string.Join('|', (await TrilhaAsync(s.Tenant)).Select(t => t.Recurso)));
        Assert.Equal(1 + 2 + 1, (await TrilhaAsync(s.Tenant)).Count); // 2 registros do ERP + 1 revogação da resolução + 1 resolução
    }

    [Fact]
    public async Task Resolver_como_Kept_so_registra_a_decisao_sem_alterar_o_consentimento_e_nao_admite_a_decisao_oposta_depois()
    {
        var s = await MontarAsync("e24-kept");
        await using var servidor = s.Api;
        var mensagem = await SemearOcorrenciaAsync(s.Tenant, s.OrgA, s.SessaoA, motivo: 3, palavra: null);
        var rota = $"/contact-center/items/{s.ItemA}/optout-reviews/{mensagem}";
        await OkAsync(await RegistrarAsync(s.Erp, Corpo("+" + TelA, coleta: Base)));

        var resolvida = await LerJsonAsync(await s.Api.EnviarAsync(s.TokenDoAdmin, HttpMethod.Post, rota, new { resolution = "kept" }));
        Assert.True(resolvida.GetProperty("resolved").GetBoolean());
        Assert.Single(await LinhasAsync(s.Tenant)); // o consentimento não mudou

        // Outra decisão para a MESMA ocorrência: 409; nada muda.
        var (status, corpo) = await LerAsync(await s.Api.EnviarAsync(s.TokenDoGestor, HttpMethod.Post, rota, new { resolution = "Revoked" }));
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("optout-review-already-resolved", corpo);
        Assert.Single(await LinhasAsync(s.Tenant));

        // O banco é a última barreira: uma ocorrência resolvida não muda mais e nenhuma é excluída.
        await Assert.ThrowsAsync<PostgresException>(() => ExecutarAsync($"update conversations.optout_reviews set resolution = 1, resolved_by = '{Guid.NewGuid()}', resolved_at = now() where tenant_id = '{s.Tenant}'::uuid"));
        await Assert.ThrowsAsync<PostgresException>(() => ExecutarAsync($"delete from conversations.optout_reviews where tenant_id = '{s.Tenant}'::uuid"));
    }

    [Fact]
    public async Task Resolucoes_concorrentes_da_mesma_ocorrencia_aplicam_uma_so_decisao_sem_efeitos_conflitantes()
    {
        var s = await MontarAsync("e24-conc3");
        await using var servidor = s.Api;
        var mensagem = await SemearOcorrenciaAsync(s.Tenant, s.OrgA, s.SessaoA);
        var rota = $"/contact-center/items/{s.ItemA}/optout-reviews/{mensagem}";

        var respostas = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
            await LerAsync(await s.Api.EnviarAsync(i % 2 == 0 ? s.TokenDoGestor : s.TokenDoAdmin, HttpMethod.Post, rota, new { resolution = i % 2 == 0 ? "Revoked" : "Kept" })))));

        Assert.All(respostas, r => Assert.True(r.Status is HttpStatusCode.OK or HttpStatusCode.Conflict, $"{(int)r.Status} {r.Corpo}"));
        var vencedoras = respostas.Where(r => r.Corpo.Contains("\"resolved\":true")).ToList();
        Assert.Single(vencedoras);
        var decisao = JsonDocument.Parse(vencedoras[0].Corpo).RootElement.GetProperty("resolution").GetString();

        // O estado final bate com a única vencedora: Revoked tem as duas finalidades revogadas; Kept não tem linha nenhuma.
        var linhas = await LinhasAsync(s.Tenant);
        Assert.Equal(decisao == "Revoked" ? 2 : 0, linhas.Count);
        Assert.All(linhas, l => Assert.Equal((short)2, l.Situacao));
        // Quem decidiu o oposto recebeu 409; quem repetiu a mesma, 200 sem efeito.
        Assert.All(respostas.Where(r => r.Status == HttpStatusCode.Conflict), r => Assert.Contains("optout-review-already-resolved", r.Corpo));
        Assert.Equal(1 + linhas.Count, await AuditoriaNoOutboxAsync(s.Tenant)); // uma resolução + um registro por linha de revogação
    }

    [Fact]
    public async Task Resolucao_recusa_decisao_invalida_ocorrencia_inexistente_ou_de_outra_conversa_e_telefone_opaco_sem_alterar_nada()
    {
        var s = await MontarAsync("e24-res-neg");
        await using var servidor = s.Api;
        var daA = await SemearOcorrenciaAsync(s.Tenant, s.OrgA, s.SessaoA);
        var daB = await SemearOcorrenciaAsync(s.Tenant, s.OrgB, s.SessaoB);
        var opaca = await SemearOcorrenciaAsync(s.Tenant, s.OrgA, s.SessaoOpaca);

        // Decisão inválida: 400 com o código.
        foreach (var decisao in new object?[] { null, "", "Maybe", 1 })
        {
            var (status, corpo) = await LerAsync(await s.Api.EnviarAsync(s.TokenDoGestor, HttpMethod.Post, $"/contact-center/items/{s.ItemA}/optout-reviews/{daA}", new { resolution = decisao }));
            Assert.True(status == HttpStatusCode.BadRequest, $"{decisao}: {(int)status} {corpo}");
        }

        // Ocorrência inexistente, de OUTRA conversa (item) e da organização B (item que o gestor nem enxerga): sempre 404.
        Assert.Equal(HttpStatusCode.NotFound, await s.Api.StatusAsync(s.TokenDoGestor, HttpMethod.Post, $"/contact-center/items/{s.ItemA}/optout-reviews/{Guid.NewGuid()}", new { resolution = "Kept" }));
        Assert.Equal(HttpStatusCode.NotFound, await s.Api.StatusAsync(s.TokenDoGestor, HttpMethod.Post, $"/contact-center/items/{s.ItemA}/optout-reviews/{daB}", new { resolution = "Kept" }));
        Assert.Equal(HttpStatusCode.NotFound, await s.Api.StatusAsync(s.TokenDoGestor, HttpMethod.Post, $"/contact-center/items/{s.ItemB}/optout-reviews/{daB}", new { resolution = "Kept" }));

        // Contato opaco: Revoked precisa do telefone (409, a ocorrência segue pendente); Kept não precisa.
        var (statusOpaco, corpoOpaco) = await LerAsync(await s.Api.EnviarAsync(s.TokenDoGestor, HttpMethod.Post, $"/contact-center/items/{s.ItemOpaco}/optout-reviews/{opaca}", new { resolution = "Revoked" }));
        Assert.Equal(HttpStatusCode.Conflict, statusOpaco);
        Assert.Contains("consent-phone-unavailable", corpoOpaco);
        var lista = await LerJsonAsync(await s.Api.EnviarAsync(s.TokenDoGestor, HttpMethod.Get, $"/contact-center/items/{s.ItemOpaco}/optout-reviews"));
        Assert.Equal(JsonValueKind.Null, lista[0].GetProperty("resolution").ValueKind);
        Assert.Equal(HttpStatusCode.OK, await s.Api.StatusAsync(s.TokenDoGestor, HttpMethod.Post, $"/contact-center/items/{s.ItemOpaco}/optout-reviews/{opaca}", new { resolution = "Kept" }));

        Assert.Empty(await LinhasAsync(s.Tenant));
        Assert.Equal(1, await AuditoriaNoOutboxAsync(s.Tenant)); // só a resolução Kept do contato opaco
    }

    [Fact]
    public async Task Falha_ao_resolver_desfaz_a_decisao_as_linhas_e_a_auditoria_e_a_ocorrencia_continua_pendente()
    {
        var s = await MontarAsync("e24-res-falha");
        await using var servidor = s.Api;
        var mensagem = await SemearOcorrenciaAsync(s.Tenant, s.OrgA, s.SessaoA);
        var rota = $"/contact-center/items/{s.ItemA}/optout-reviews/{mensagem}";

        var nome = "e24r_" + Guid.NewGuid().ToString("N")[..8];
        await ExecutarAsync(
            $@"CREATE FUNCTION conversations.fn_{nome}() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'falha injetada pelo teste'; END $$;
               CREATE TRIGGER tg_{nome} BEFORE INSERT ON conversations.outbox_message FOR EACH ROW WHEN (NEW.tenant_id = '{s.Tenant}'::uuid AND NEW.event_type = 'conversations.audit-requested') EXECUTE FUNCTION conversations.fn_{nome}();");
        try
        {
            var (status, _) = await LerAsync(await s.Api.EnviarAsync(s.TokenDoGestor, HttpMethod.Post, rota, new { resolution = "Revoked" }));
            Assert.True((int)status >= 500, $"A falha deveria ser 5xx, foi {(int)status}.");
        }
        finally
        {
            await ExecutarAsync($"DROP TRIGGER IF EXISTS tg_{nome} ON conversations.outbox_message; DROP FUNCTION IF EXISTS conversations.fn_{nome}();");
        }

        Assert.Empty(await LinhasAsync(s.Tenant));
        Assert.Equal(0, await AuditoriaNoOutboxAsync(s.Tenant));
        Assert.Equal(0, await EscalarAsync("select count(*) from conversations.optout_reviews where tenant_id = @t and resolution is not null", ("t", s.Tenant))); // continua pendente

        // A retentativa conclui, uma vez.
        Assert.True((await LerJsonAsync(await s.Api.EnviarAsync(s.TokenDoGestor, HttpMethod.Post, rota, new { resolution = "Revoked" }))).GetProperty("resolved").GetBoolean());
        Assert.Equal(2, (await LinhasAsync(s.Tenant)).Count);
    }
}
