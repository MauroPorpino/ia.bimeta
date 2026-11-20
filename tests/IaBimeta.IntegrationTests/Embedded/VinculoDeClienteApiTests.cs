using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using IaBimeta.Api.Embedded;
using IaBimeta.Conversations.Application;
using IaBimeta.Conversations.Infrastructure;
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
/// E2.2 (ADR 0041, seções 1, 2, 5 e 8): o vínculo entre o cliente do sistema externo e o contato, pelo servidor do sistema externo (HMAC, <c>/embed/v1/customer-links</c>) e pelo atendente em sessão embutida
/// (<c>/contact-center/items/{id}/customer-link</c>). API real e PostgreSQL real; a auditoria do Conversations vai ao outbox na transação do efeito e é entregue pelo handler do módulo (o que o relay do Worker faz).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class VinculoDeClienteApiTests(PostgresFixture banco)
{
    private const string Rota = "/embed/v1/customer-links";
    private const string TelA1 = "5511999990101"; // contato da organização A (item A)
    private const string TelA2 = "5511999990303"; // outro contato da organização A
    private const string TelB = "5511999990202"; // contato SÓ da organização B

    private sealed record Cena(CenarioDeConversas C, ApiServer Api, string TokenDoAdmin, ConexaoDto Conexao, ErpAssinante Erp, Guid OrgA, Guid OrgB, Guid FilaA, Guid ItemA, Guid ItemB, TrocaDto Troca)
    {
        public Guid Tenant => C.Tenant.TenantId.Value;
    }

    private async Task<Cena> MontarAsync(string prefixo, string modo = "FullAttendant", ApiServer? api = null, string telA1 = TelA1, string telA2 = TelA2, string telB = TelB)
    {
        var c = await CenarioDeConversas.CriarAsync(banco, prefixo);
        var orgA = c.OrganizacaoId;
        var orgB = (await banco.CriarOrganizacaoAsync(c.Tenant, $"Organização B {prefixo}")).Id;
        var admin = await banco.CriarAdministradorDoTenantAsync(c.Tenant);
        api ??= await ApiServer.StartAsync("Production", EmbeddedTestSupport.Configuracao());
        var token = await api.LoginAsync(admin);
        var conexao = await api.CriarAsync<ConexaoDto>(token, "/embedded/connections", new
        {
            organizationId = orgA, name = "ERP", mode = modo, provisioning = "JitAttendant", allowedOrigins = new[] { EmbeddedTestSupport.Origem }, allowStartConversation = false,
        });
        var chave = await api.CriarAsync<ChaveCriadaDto>(token, $"/embedded/connections/{conexao.Id}/keys", new { });
        var erp = new ErpAssinante(api, chave.KeyId, chave.Secret);

        var filaA = (await c.CriarFilaAsync("Fila A", organizacao: orgA)).Id;
        var filaB = (await c.CriarFilaAsync("Fila B", organizacao: orgB)).Id;
        var unidadeB = await c.CriarUnidadeAsync(orgB, "Matriz B", matriz: true);
        var canalB = await c.CriarCanalAsync(new[] { unidadeB }, unidadeB, organizacao: orgB);
        var sessaoA = (await c.EntrarAsync(telA1, texto: "Pergunta A")).SessionId;
        await c.EntrarAsync(telA2, texto: "Outra pergunta A");
        var sessaoB = (await c.EntrarAsync(telB, texto: "Pergunta B", organizacao: orgB, unidade: unidadeB, canal: canalB)).SessionId;
        var itemA = (await c.NaFilaAsync(sessaoA, filaA)).Id;
        var itemB = (await c.NaFilaAsync(sessaoB, filaB)).Id;

        var verifier = EmbeddedTestSupport.NovoVerifier();
        var ticket = await erp.EmitirAsync("u-vinculo", EmbeddedTestSupport.Challenge(verifier), cliente: "CLI-100");
        var troca = await EmbeddedTestSupport.TrocarAsync(api, ticket.Ticket, verifier);
        await c.AdicionarAtendenteAsync(filaA, troca.User.Id);
        return new Cena(c, api, token, conexao, erp, orgA, orgB, filaA, itemA, itemB, troca);
    }

    // Acesso direto ao banco (só leitura e a falha injetada)

    private sealed record Linha(Guid Id, Guid Conexao, string Cliente, Guid Contato, short Origem, short PorTipo, Guid PorId, DateTimeOffset? DesvinculadoEm, short? DesvinculadoPorTipo, Guid? DesvinculadoPorId);

    private static async Task<List<Linha>> LinhasAsync(Guid tenant)
    {
        await using var conexao = new NpgsqlConnection(TestDatabase.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = new NpgsqlCommand(
            "select id, connection_id, customer_ref, contact_id, origin, linked_by_kind, linked_by_id, unlinked_at, unlinked_by_kind, unlinked_by_id from conversations.contact_external_links where tenant_id = @t order by linked_at, id", conexao);
        comando.Parameters.AddWithValue("t", tenant);
        var linhas = new List<Linha>();
        await using var leitor = await comando.ExecuteReaderAsync();
        while (await leitor.ReadAsync())
        {
            linhas.Add(new Linha(
                leitor.GetGuid(0), leitor.GetGuid(1), leitor.GetString(2), leitor.GetGuid(3), leitor.GetInt16(4), leitor.GetInt16(5), leitor.GetGuid(6),
                leitor.IsDBNull(7) ? null : leitor.GetFieldValue<DateTimeOffset>(7), leitor.IsDBNull(8) ? null : leitor.GetInt16(8), leitor.IsDBNull(9) ? null : leitor.GetGuid(9)));
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

    private static Task ExecutarAsync(string sql) => EscalarAsyncSemResultado(sql);

    private static async Task EscalarAsyncSemResultado(string sql)
    {
        await using var conexao = new NpgsqlConnection(TestDatabase.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = new NpgsqlCommand(sql, conexao);
        await comando.ExecuteNonQueryAsync();
    }

    private static Task<long> AuditoriaNoOutboxAsync(Guid tenant) =>
        EscalarAsync("select count(*) from conversations.outbox_message where tenant_id = @t and event_type = 'conversations.audit-requested'", ("t", tenant));

    private static Task<long> ContatosAsync(Guid tenant) => EscalarAsync("select count(*) from conversations.contacts where tenant_id = @t", ("t", tenant));

    private static async Task<List<(string Operacao, string Recurso, Guid? Ator, string Origem)>> TrilhaAsync(Guid tenant)
    {
        await using var conexao = new NpgsqlConnection(TestDatabase.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = new NpgsqlCommand(
            "select operation, coalesce(resource_id, ''), actor_user_id, actor_origin from audit.audit_events where tenant_id = @t and operation like 'embed.customer.%' order by occurred_at, id", conexao);
        comando.Parameters.AddWithValue("t", tenant);
        var linhas = new List<(string, string, Guid?, string)>();
        await using var leitor = await comando.ExecuteReaderAsync();
        while (await leitor.ReadAsync())
        {
            linhas.Add((leitor.GetString(0), leitor.GetString(1), leitor.IsDBNull(2) ? null : leitor.GetGuid(2), leitor.GetString(3)));
        }

        return linhas;
    }

    /// <summary>Faz o que o relay do Worker faz com o handler do módulo: abre o contexto do tenant do evento, entrega os pedidos de auditoria pendentes à trilha e marca os entregues como processados.</summary>
    private static async Task<int> EntregarAuditoriaAsync(ApiServer api, Guid tenant)
    {
        await using var escopo = api.Services.CreateAsyncScope();
        escopo.ServiceProvider.GetRequiredService<RequestContextHolder>().Set(new RequestContext(new TenantId(tenant), "messaging-relay"));
        var db = escopo.ServiceProvider.GetRequiredService<ConversationsDbContext>();
        var handler = escopo.ServiceProvider.GetServices<IEventHandler>().Single(h => h.EventType == ConversationAuditEvents.AuditRequested);
        var pendentes = await db.Set<OutboxMessage>().Where(o => o.TenantId == tenant && o.EventType == ConversationAuditEvents.AuditRequested && o.State == OutboxState.Pending).OrderBy(o => o.IngestSeq).ToListAsync();
        var entregues = 0;
        foreach (var linha in pendentes)
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

    // Chamadas do "servidor do sistema externo"

    private static Task<HttpResponseMessage> VincularAsync(ErpAssinante erp, string cliente, string? telefone, string? caminho = null) =>
        erp.EnviarAsync(caminho ?? Rota, new { customerRef = cliente, phone = telefone }, metodo: HttpMethod.Put);

    private static Task<HttpResponseMessage> DesvincularAsync(ErpAssinante erp, string cliente, string? telefone = null) =>
        erp.EnviarAsync(Rota, telefone is null ? new { customerRef = cliente } : (object)new { customerRef = cliente, phone = telefone }, metodo: HttpMethod.Delete);

    private static async Task<(HttpStatusCode Status, string Corpo)> LerAsync(HttpResponseMessage resposta)
    {
        using (resposta)
        {
            return (resposta.StatusCode, await resposta.Content.ReadAsStringAsync());
        }
    }

    private static async Task<(bool Resolvido, bool Compartilhado)> OkAsync(HttpResponseMessage resposta)
    {
        using (resposta)
        {
            Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
            var json = await resposta.Content.ReadFromJsonAsync<JsonElement>();
            return (json.GetProperty("resolved").GetBoolean(), json.GetProperty("shared").GetBoolean());
        }
    }

    private async Task<Guid> ContatoAsync(Guid tenant, string referencia) =>
        Guid.Parse((await EscalarTextoAsync("select id::text from conversations.contacts where tenant_id = @t and external_ref = @r", ("t", tenant), ("r", referencia)))!);

    private static async Task<string?> EscalarTextoAsync(string sql, params (string Nome, object Valor)[] parametros)
    {
        await using var conexao = new NpgsqlConnection(TestDatabase.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = new NpgsqlCommand(sql, conexao);
        foreach (var (nome, valor) in parametros)
        {
            comando.Parameters.AddWithValue(nome, valor);
        }

        return (await comando.ExecuteScalarAsync()) as string;
    }

    // ------------------------------------------------------------------ vínculo pelo servidor do sistema externo

    [Fact]
    public async Task Vinculo_valido_resolve_o_contato_da_organizacao_grava_uma_linha_ativa_e_audita_na_transacao_sem_dado_pessoal()
    {
        var s = await MontarAsync("e22-valido");
        await using var servidor = s.Api;
        var contato = await ContatoAsync(s.Tenant, TelA1);

        // O telefone com separadores e o prefixo internacional é normalizado para a chave do contato.
        Assert.Equal((true, false), await OkAsync(await VincularAsync(s.Erp, "CLI-100", "+55 (11) 99999-0101")));

        var linha = Assert.Single(await LinhasAsync(s.Tenant));
        Assert.Equal((s.Conexao.Id, "CLI-100", contato, (short)1, (short)1, s.Conexao.Id, (DateTimeOffset?)null), (linha.Conexao, linha.Cliente, linha.Contato, linha.Origem, linha.PorTipo, linha.PorId, linha.DesvinculadoEm));

        // A auditoria já está NO OUTBOX (mesma transação do efeito) e ainda não na trilha; o handler a entrega depois.
        Assert.Equal(1, await AuditoriaNoOutboxAsync(s.Tenant));
        Assert.Empty(await TrilhaAsync(s.Tenant));
        Assert.Equal(1, await EntregarAuditoriaAsync(s.Api, s.Tenant));
        Assert.Equal(0, await EntregarAuditoriaAsync(s.Api, s.Tenant)); // nada pendente: sem duplicata

        var trilha = Assert.Single(await TrilhaAsync(s.Tenant));
        Assert.Equal("embed.customer.link", trilha.Operacao);
        Assert.Null(trilha.Ator); // quem agiu é a conexão, não um usuário
        Assert.Equal("integration", trilha.Origem);
        Assert.Matches(new Regex($"^{linha.Id}:{s.Conexao.Id:N}:[0-9a-f]{{16}}$"), trilha.Recurso);
        Assert.DoesNotContain("CLI-100", trilha.Recurso); // nem o cliente nem o telefone em claro
        Assert.DoesNotContain("999990101", trilha.Recurso);
    }

    [Fact]
    public async Task Repetir_o_vinculo_e_idempotente_sem_linha_nova_e_sem_auditoria_nova()
    {
        var s = await MontarAsync("e22-idem");
        await using var servidor = s.Api;

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal((true, false), await OkAsync(await VincularAsync(s.Erp, "CLI-100", "+" + TelA1)));
        }

        Assert.Single(await LinhasAsync(s.Tenant));
        Assert.Equal(1, await AuditoriaNoOutboxAsync(s.Tenant));
        await EntregarAuditoriaAsync(s.Api, s.Tenant);
        Assert.Single(await TrilhaAsync(s.Tenant));
    }

    [Fact]
    public async Task Contato_inexistente_de_outra_organizacao_ou_de_outro_tenant_tem_a_mesma_resposta_e_nada_e_criado()
    {
        var s = await MontarAsync("e22-uniforme");
        await using var servidor = s.Api;
        var outro = await MontarAsync("e22-uniforme2", api: s.Api, telA1: "5511999991111", telA2: "5511999992222", telB: "5511999993333");
        var contatosAntes = (await ContatosAsync(s.Tenant), await ContatosAsync(outro.Tenant));

        var respostas = new[]
        {
            await LerAsync(await VincularAsync(s.Erp, "CLI-100", "+5511999990999")), // não existe
            await LerAsync(await VincularAsync(s.Erp, "CLI-100", "+" + TelB)), // existe, mas só na organização B do MESMO tenant
            await LerAsync(await VincularAsync(s.Erp, "CLI-100", "+5511999991111")), // existe, mas é de OUTRO tenant
        };

        Assert.All(respostas, r => Assert.Equal(HttpStatusCode.OK, r.Status));
        Assert.Single(respostas.Select(r => r.Corpo).Distinct()); // o mesmo corpo, byte a byte
        Assert.Equal("""{"resolved":false,"shared":false}""", respostas[0].Corpo);

        Assert.Empty(await LinhasAsync(s.Tenant));
        Assert.Empty(await LinhasAsync(outro.Tenant));
        Assert.Equal(contatosAntes, (await ContatosAsync(s.Tenant), await ContatosAsync(outro.Tenant))); // nenhum contato criado
        Assert.Equal(0, await AuditoriaNoOutboxAsync(s.Tenant));
    }

    [Theory]
    [InlineData("11999990101")] // só dígitos: pode ser nacional, sem código do país
    [InlineData("5511999990101")] // idem, mesmo parecendo internacional
    [InlineData("+55abc")]
    [InlineData("+0511999990101")] // código do país com zero
    [InlineData("+1234567")] // curto demais
    [InlineData("+1234567890123456")] // longo demais
    [InlineData("")]
    [InlineData(null)]
    public async Task Telefone_invalido_ou_sem_codigo_internacional_e_400_sem_efeito(string? telefone)
    {
        var s = await MontarAsync("e22-fone");
        await using var servidor = s.Api;

        var (status, corpo) = await LerAsync(await VincularAsync(s.Erp, "CLI-100", telefone));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("invalid-phone", corpo);
        Assert.Empty(await LinhasAsync(s.Tenant));
        Assert.Equal(0, await AuditoriaNoOutboxAsync(s.Tenant));
    }

    [Fact]
    public async Task Referencia_do_cliente_invalida_e_corpo_invalido_sao_400_com_o_codigo_do_campo()
    {
        var s = await MontarAsync("e22-ref");
        await using var servidor = s.Api;

        foreach (var cliente in new[] { "", "   ", new string('x', 129), "a\u0007b" })
        {
            var (status, corpo) = await LerAsync(await VincularAsync(s.Erp, cliente, "+" + TelA1));
            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Contains("invalid-customer-ref", corpo);
        }

        var (statusDoLixo, corpoDoLixo) = await LerAsync(await s.Erp.EnviarAsync(Rota, "não é um objeto", metodo: HttpMethod.Put));
        Assert.Equal(HttpStatusCode.BadRequest, statusDoLixo);
        Assert.Contains("invalid-body", corpoDoLixo);

        Assert.Equal(HttpStatusCode.BadRequest, (await LerAsync(await VincularAsync(s.Erp, "CLI-100", "+" + TelA1, Rota + "?x=1"))).Status); // consulta não é assinada
        var grande = new { customerRef = "CLI-100", phone = "+" + TelA1, extra = new string('z', 9000) };
        Assert.Equal(HttpStatusCode.BadRequest, (await LerAsync(await s.Erp.EnviarAsync(Rota, grande, metodo: HttpMethod.Put))).Status);

        Assert.Empty(await LinhasAsync(s.Tenant));
        Assert.Equal(0, await AuditoriaNoOutboxAsync(s.Tenant));
    }

    [Fact]
    public async Task Falha_de_autenticacao_e_sempre_o_mesmo_401_sem_efeito_em_vincular_e_desvincular()
    {
        var s = await MontarAsync("e22-auth");
        await using var servidor = s.Api;
        await VincularAsync(s.Erp, "CLI-100", "+" + TelA1);
        var corpo = new { customerRef = "CLI-100", phone = "+" + TelA1 };
        var nonceRepetido = "nonce-repetido-12345";
        Assert.Equal(HttpStatusCode.OK, (await LerAsync(await s.Erp.EnviarAsync(Rota, corpo, nonce: nonceRepetido, metodo: HttpMethod.Put))).Status);

        var tentativas = new List<Func<Task<HttpResponseMessage>>>
        {
            () => s.Erp.EnviarAsync(Rota, corpo, nonce: nonceRepetido, metodo: HttpMethod.Put), // replay
            () => s.Erp.EnviarAsync(Rota, corpo, segredoUsado: "ibsk_errado", metodo: HttpMethod.Put),
            () => s.Erp.EnviarAsync(Rota, corpo, keyIdUsado: "ibk_" + Guid.NewGuid().ToString("N"), metodo: HttpMethod.Put),
            () => s.Erp.EnviarAsync(Rota, corpo, instante: DateTimeOffset.UtcNow.AddMinutes(-10), metodo: HttpMethod.Put),
            () => s.Erp.EnviarAsync(Rota, corpo, corpoEnviado: "{\"customerRef\":\"OUTRO\",\"phone\":\"+5511999990303\"}"u8.ToArray(), metodo: HttpMethod.Put), // corpo adulterado
            () => s.Erp.EnviarAsync(Rota, new { customerRef = "CLI-100" }, segredoUsado: "ibsk_errado", metodo: HttpMethod.Delete),
            () => s.Erp.EnviarAsync(Rota, corpo, metodo: HttpMethod.Post), // outro método assinado: a rota não existe para ele
        };

        var corpos = new List<string>();
        foreach (var tentativa in tentativas.Take(6))
        {
            var (status, texto) = await LerAsync(await tentativa());
            Assert.Equal(HttpStatusCode.Unauthorized, status);
            Assert.Contains("embedded-authentication-failed", texto);
            corpos.Add(texto);
        }

        Assert.Single(corpos.Select(c => Regex.Replace(c, "\"traceId\":\"[^\"]*\"", string.Empty)).Distinct()); // a mesma recusa, sem revelar qual verificação falhou
        Assert.NotEqual(HttpStatusCode.OK, (await LerAsync(await tentativas[6]())).Status);

        // Sem cabeçalhos de assinatura e com Bearer de usuário: também 401/403, nunca efeito.
        using var semAssinatura = new HttpRequestMessage(HttpMethod.Put, Rota) { Content = JsonContent.Create(corpo) };
        using var resposta = await s.Api.Client.SendAsync(semAssinatura);
        Assert.Equal(HttpStatusCode.Unauthorized, resposta.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await s.Api.StatusAsync(s.TokenDoAdmin, HttpMethod.Put, Rota, corpo));

        Assert.Single(await LinhasAsync(s.Tenant)); // só o vínculo legítimo
    }

    [Fact]
    public async Task Chave_revogada_e_conexao_desativada_perdem_o_acesso_e_nada_e_gravado()
    {
        var s = await MontarAsync("e22-chave");
        await using var servidor = s.Api;
        Assert.Equal((true, false), await OkAsync(await VincularAsync(s.Erp, "CLI-100", "+" + TelA1)));

        Assert.Equal(HttpStatusCode.OK, await s.Api.StatusAsync(s.TokenDoAdmin, HttpMethod.Post, $"/embedded/connections/{s.Conexao.Id}/disable"));

        Assert.Equal(HttpStatusCode.Unauthorized, (await LerAsync(await VincularAsync(s.Erp, "CLI-200", "+" + TelA2))).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LerAsync(await DesvincularAsync(s.Erp, "CLI-100"))).Status);
        var linha = Assert.Single(await LinhasAsync(s.Tenant));
        Assert.Null(linha.DesvinculadoEm); // a desativação da conexão não desvincula por si
    }

    [Fact]
    public async Task Telefone_compartilhado_e_cliente_com_dois_contatos_sao_sinalizados_sem_escolher_um_por_suposicao()
    {
        var s = await MontarAsync("e22-compart");
        await using var servidor = s.Api;

        Assert.Equal((true, false), await OkAsync(await VincularAsync(s.Erp, "CLI-100", "+" + TelA1)));
        // Outro cliente no MESMO telefone: compartilhado (e os dois vínculos existem).
        Assert.Equal((true, true), await OkAsync(await VincularAsync(s.Erp, "CLI-200", "+" + TelA1)));
        // E o primeiro, ao repetir, passa a ver o compartilhamento.
        Assert.Equal((true, true), await OkAsync(await VincularAsync(s.Erp, "CLI-100", "+" + TelA1)));
        Assert.Equal(2, (await LinhasAsync(s.Tenant)).Count);

        // O mesmo cliente em dois telefones (dois contatos): compartilhado também.
        Assert.Equal((true, true), await OkAsync(await VincularAsync(s.Erp, "CLI-100", "+" + TelA2)));
        Assert.Equal(3, (await LinhasAsync(s.Tenant)).Count(l => l.DesvinculadoEm is null));
    }

    // ------------------------------------------------------------------ desvinculação

    [Fact]
    public async Task Desvincular_e_logico_idempotente_auditado_uma_vez_e_preserva_conversas_e_consentimentos()
    {
        var s = await MontarAsync("e22-desv");
        await using var servidor = s.Api;
        await VincularAsync(s.Erp, "CLI-100", "+" + TelA1);
        var consentimento = $"insert into conversations.contact_consents (id, tenant_id, organization_id, phone, purpose, status, origin, evidence, collected_at, recorded_at, actor_kind, actor_id) values (gen_random_uuid(), '{s.Tenant}', '{s.OrgA}', '+{TelA1}', 1, 1, 1, 'Formulário', now(), now(), 1, '{s.Conexao.Id}')";
        await ExecutarAsync(consentimento);
        var sessoesAntes = await EscalarAsync("select count(*) from conversations.service_sessions where tenant_id = @t", ("t", s.Tenant));
        var mensagensAntes = await EscalarAsync("select count(*) from conversations.messages where tenant_id = @t", ("t", s.Tenant));

        var primeira = await LerAsync(await DesvincularAsync(s.Erp, "CLI-100"));
        Assert.Equal(HttpStatusCode.NoContent, primeira.Status);
        var depoisDaPrimeira = Assert.Single(await LinhasAsync(s.Tenant));
        Assert.NotNull(depoisDaPrimeira.DesvinculadoEm);
        Assert.Equal(((short?)1, (Guid?)s.Conexao.Id), (depoisDaPrimeira.DesvinculadoPorTipo, depoisDaPrimeira.DesvinculadoPorId));

        // Repetir, e desvincular quem nunca existiu: o mesmo 204, nada muda (a primeira desvinculação é a que vale) e nenhuma auditoria nova.
        Assert.Equal(HttpStatusCode.NoContent, (await LerAsync(await DesvincularAsync(s.Erp, "CLI-100"))).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await LerAsync(await DesvincularAsync(s.Erp, "CLI-QUE-NAO-EXISTE"))).Status);
        Assert.Equal(depoisDaPrimeira, Assert.Single(await LinhasAsync(s.Tenant)));

        await EntregarAuditoriaAsync(s.Api, s.Tenant);
        Assert.Equal(new[] { "embed.customer.link", "embed.customer.unlink" }, (await TrilhaAsync(s.Tenant)).Select(t => t.Operacao).Order().ToArray());

        // Histórico e consentimento intactos.
        Assert.Equal(sessoesAntes, await EscalarAsync("select count(*) from conversations.service_sessions where tenant_id = @t", ("t", s.Tenant)));
        Assert.Equal(mensagensAntes, await EscalarAsync("select count(*) from conversations.messages where tenant_id = @t", ("t", s.Tenant)));
        Assert.Equal(1, await EscalarAsync("select count(*) from conversations.contact_consents where tenant_id = @t", ("t", s.Tenant)));
    }

    [Fact]
    public async Task Desvincular_com_telefone_so_desfaz_o_vinculo_daquele_contato_e_revincular_cria_linha_nova()
    {
        var s = await MontarAsync("e22-revinc");
        await using var servidor = s.Api;
        await VincularAsync(s.Erp, "CLI-100", "+" + TelA1);
        await VincularAsync(s.Erp, "CLI-100", "+" + TelA2);

        Assert.Equal(HttpStatusCode.NoContent, (await LerAsync(await DesvincularAsync(s.Erp, "CLI-100", "+" + TelA1))).Status);
        var contatoA1 = await ContatoAsync(s.Tenant, TelA1);
        var linhas = await LinhasAsync(s.Tenant);
        Assert.NotNull(linhas.Single(l => l.Contato == contatoA1).DesvinculadoEm);
        Assert.Null(linhas.Single(l => l.Contato != contatoA1).DesvinculadoEm);

        // Revincular: linha NOVA (a desvinculada permanece como histórico, inalterada).
        var antiga = linhas.Single(l => l.Contato == contatoA1);
        Assert.Equal((true, true), await OkAsync(await VincularAsync(s.Erp, "CLI-100", "+" + TelA1)));
        var depois = await LinhasAsync(s.Tenant);
        Assert.Equal(3, depois.Count);
        Assert.Contains(antiga, depois);
        var nova = Assert.Single(depois, l => l.Contato == contatoA1 && l.DesvinculadoEm is null);
        Assert.NotEqual(antiga.Id, nova.Id);

        Assert.Equal(HttpStatusCode.BadRequest, (await LerAsync(await DesvincularAsync(s.Erp, "CLI-100", "5511999990101"))).Status); // com telefone, o formato estrito vale também
    }

    [Fact]
    public async Task A_conexao_so_desfaz_os_proprios_vinculos_e_um_tenant_nunca_desfaz_os_de_outro()
    {
        var s = await MontarAsync("e22-escopo");
        await using var servidor = s.Api;
        var outro = await MontarAsync("e22-escopo2", api: s.Api, telA1: TelA1, telA2: "5511999994444", telB: "5511999995555"); // MESMO telefone e MESMO cliente em outro tenant
        var segundaChave = await s.Api.CriarAsync<ConexaoDto>(s.TokenDoAdmin, "/embedded/connections", new
        {
            organizationId = s.OrgA, name = "Outro ERP", mode = "FullAttendant", provisioning = "JitAttendant", allowedOrigins = new[] { EmbeddedTestSupport.Origem }, allowStartConversation = false,
        });
        var chave2 = await s.Api.CriarAsync<ChaveCriadaDto>(s.TokenDoAdmin, $"/embedded/connections/{segundaChave.Id}/keys", new { });
        var erp2 = new ErpAssinante(s.Api, chave2.KeyId, chave2.Secret);

        await VincularAsync(s.Erp, "CLI-100", "+" + TelA1);
        await VincularAsync(outro.Erp, "CLI-100", "+" + TelA1);

        // A segunda conexão do MESMO tenant e organização não desfaz o vínculo da primeira; o outro tenant também não.
        Assert.Equal(HttpStatusCode.NoContent, (await LerAsync(await DesvincularAsync(erp2, "CLI-100"))).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await LerAsync(await DesvincularAsync(outro.Erp, "CLI-100"))).Status);
        var linhaDoPrimeiro = Assert.Single(await LinhasAsync(s.Tenant));
        Assert.Null(linhaDoPrimeiro.DesvinculadoEm);
        Assert.NotNull(Assert.Single(await LinhasAsync(outro.Tenant)).DesvinculadoEm); // o outro tenant só desfez o PRÓPRIO

        // A segunda conexão vincula o mesmo cliente: é vínculo próprio (a chave é por conexão).
        Assert.Equal((true, false), await OkAsync(await VincularAsync(erp2, "CLI-100", "+" + TelA1)));
        Assert.Equal(2, (await LinhasAsync(s.Tenant)).Count(l => l.DesvinculadoEm is null));
    }

    // ------------------------------------------------------------------ concorrência e atomicidade

    [Fact]
    public async Task Vinculos_simultaneos_do_mesmo_cliente_geram_uma_linha_ativa_e_uma_auditoria_sem_erro()
    {
        var s = await MontarAsync("e22-conc");
        await using var servidor = s.Api;

        var respostas = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => LerAsync(VincularAsync(s.Erp, "CLI-100", "+" + TelA1).GetAwaiter().GetResult())));

        Assert.All(respostas, r => Assert.Equal(HttpStatusCode.OK, r.Status));
        Assert.Single(await LinhasAsync(s.Tenant));
        Assert.Equal(1, await AuditoriaNoOutboxAsync(s.Tenant));
    }

    [Fact]
    public async Task Vincular_e_desvincular_ao_mesmo_tempo_terminam_consistentes_com_uma_auditoria_por_efeito()
    {
        var s = await MontarAsync("e22-conc2");
        await using var servidor = s.Api;
        await VincularAsync(s.Erp, "CLI-100", "+" + TelA1);

        var tarefas = Enumerable.Range(0, 6).SelectMany(i => new[]
        {
            Task.Run(async () => (await LerAsync(await VincularAsync(s.Erp, "CLI-100", "+" + TelA1))).Status),
            Task.Run(async () => (await LerAsync(await DesvincularAsync(s.Erp, "CLI-100"))).Status),
        });
        var status = await Task.WhenAll(tarefas);

        Assert.All(status, c => Assert.True(c is HttpStatusCode.OK or HttpStatusCode.NoContent, $"Status inesperado: {c}"));
        var linhas = await LinhasAsync(s.Tenant);
        Assert.True(linhas.Count(l => l.DesvinculadoEm is null) <= 1, "No máximo um vínculo ativo.");
        // Uma auditoria por efeito: cada linha criada tem um evento de vínculo e cada desvinculada, um de desvinculação.
        Assert.Equal(linhas.Count + linhas.Count(l => l.DesvinculadoEm is not null), await AuditoriaNoOutboxAsync(s.Tenant));
    }

    [Fact]
    public async Task Clientes_diferentes_no_mesmo_contato_ao_mesmo_tempo_criam_os_dois_vinculos()
    {
        var s = await MontarAsync("e22-conc3");
        await using var servidor = s.Api;

        var status = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Task.Run(async () => (await LerAsync(await VincularAsync(s.Erp, $"CLI-{i}", "+" + TelA1))).Status)));

        Assert.All(status, c => Assert.Equal(HttpStatusCode.OK, c));
        Assert.Equal(6, (await LinhasAsync(s.Tenant)).Count(l => l.DesvinculadoEm is null));
        Assert.Equal(6, await AuditoriaNoOutboxAsync(s.Tenant));
    }

    [Fact]
    public async Task Falha_no_meio_desfaz_o_vinculo_e_nao_deixa_auditoria_espuria_e_sem_a_falha_confirma_os_dois()
    {
        var s = await MontarAsync("e22-falha");
        await using var servidor = s.Api;
        var nome = "e22_" + Guid.NewGuid().ToString("N")[..8];
        await ExecutarAsync(
            $@"CREATE FUNCTION conversations.fn_{nome}() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'falha injetada pelo teste'; END $$;
               CREATE TRIGGER tg_{nome} BEFORE INSERT ON conversations.outbox_message FOR EACH ROW WHEN (NEW.tenant_id = '{s.Tenant}'::uuid AND NEW.event_type = 'conversations.audit-requested') EXECUTE FUNCTION conversations.fn_{nome}();");
        try
        {
            var (status, _) = await LerAsync(await VincularAsync(s.Erp, "CLI-100", "+" + TelA1));
            Assert.True((int)status >= 500, $"A falha deveria ser 5xx, foi {(int)status}.");
        }
        finally
        {
            await ExecutarAsync($"DROP TRIGGER IF EXISTS tg_{nome} ON conversations.outbox_message; DROP FUNCTION IF EXISTS conversations.fn_{nome}();");
        }

        Assert.Empty(await LinhasAsync(s.Tenant)); // o vínculo foi desfeito junto com a auditoria
        Assert.Equal(0, await AuditoriaNoOutboxAsync(s.Tenant));

        Assert.Equal((true, false), await OkAsync(await VincularAsync(s.Erp, "CLI-100", "+" + TelA1)));
        Assert.Single(await LinhasAsync(s.Tenant));
        Assert.Equal(1, await AuditoriaNoOutboxAsync(s.Tenant));
    }

    // ------------------------------------------------------------------ atendente em sessão embutida

    [Fact]
    public async Task Atendente_vincula_o_cliente_em_foco_ao_contato_do_item_com_origem_e_autor_corretos_e_audita()
    {
        var s = await MontarAsync("e22-ag");
        await using var servidor = s.Api;
        var contato = await ContatoAsync(s.Tenant, TelA1);

        using var resposta = await s.Api.EnviarAsync(s.Troca.Token, HttpMethod.Post, $"/contact-center/items/{s.ItemA}/customer-link", new { customerRef = "CLI-100" });
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.False((await resposta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("shared").GetBoolean());

        var linha = Assert.Single(await LinhasAsync(s.Tenant));
        Assert.Equal((s.Conexao.Id, "CLI-100", contato, (short)2, (short)2, s.Troca.User.Id), (linha.Conexao, linha.Cliente, linha.Contato, linha.Origem, linha.PorTipo, linha.PorId));

        await EntregarAuditoriaAsync(s.Api, s.Tenant);
        var trilha = Assert.Single(await TrilhaAsync(s.Tenant));
        Assert.Equal(("embed.customer.link", (Guid?)s.Troca.User.Id, "embedded-session"), (trilha.Operacao, trilha.Ator, trilha.Origem));

        // Repetir é idempotente; desvincular também, e a desvinculação é do usuário.
        using var repetida = await s.Api.EnviarAsync(s.Troca.Token, HttpMethod.Post, $"/contact-center/items/{s.ItemA}/customer-link", new { customerRef = "CLI-100" });
        Assert.Equal(HttpStatusCode.OK, repetida.StatusCode);
        Assert.Single(await LinhasAsync(s.Tenant));
        Assert.Equal(HttpStatusCode.NoContent, await s.Api.StatusAsync(s.Troca.Token, HttpMethod.Delete, $"/contact-center/items/{s.ItemA}/customer-link/CLI-100"));
        Assert.Equal(HttpStatusCode.NoContent, await s.Api.StatusAsync(s.Troca.Token, HttpMethod.Delete, $"/contact-center/items/{s.ItemA}/customer-link/CLI-100"));
        var desfeita = Assert.Single(await LinhasAsync(s.Tenant));
        Assert.Equal(((short?)2, (Guid?)s.Troca.User.Id), (desfeita.DesvinculadoPorTipo, desfeita.DesvinculadoPorId));
        Assert.Equal(2, await AuditoriaNoOutboxAsync(s.Tenant));
    }

    [Fact]
    public async Task Atendente_so_vincula_cliente_autorizado_na_sessao_em_item_do_escopo_e_em_sessao_embutida()
    {
        var s = await MontarAsync("e22-ag-neg");
        await using var servidor = s.Api;

        // O cliente vem do ticket assinado, nunca do corpo: outro identificador é 404, como se não existisse.
        Assert.Equal(HttpStatusCode.NotFound, await s.Api.StatusAsync(s.Troca.Token, HttpMethod.Post, $"/contact-center/items/{s.ItemA}/customer-link", new { customerRef = "CLI-999" }));
        Assert.Equal(HttpStatusCode.NotFound, await s.Api.StatusAsync(s.Troca.Token, HttpMethod.Delete, $"/contact-center/items/{s.ItemA}/customer-link/CLI-999"));
        // Item de outra organização: 404 (fora do escopo), mesmo com cliente autorizado.
        Assert.Equal(HttpStatusCode.NotFound, await s.Api.StatusAsync(s.Troca.Token, HttpMethod.Post, $"/contact-center/items/{s.ItemB}/customer-link", new { customerRef = "CLI-100" }));
        Assert.Equal(HttpStatusCode.NotFound, await s.Api.StatusAsync(s.Troca.Token, HttpMethod.Post, $"/contact-center/items/{Guid.NewGuid()}/customer-link", new { customerRef = "CLI-100" }));
        // Sessão interativa (o administrador enxerga e pode operar, mas não há cliente em foco): 404.
        Assert.Equal(HttpStatusCode.NotFound, await s.Api.StatusAsync(s.TokenDoAdmin, HttpMethod.Post, $"/contact-center/items/{s.ItemA}/customer-link", new { customerRef = "CLI-100" }));
        // Identificador inválido: 400.
        Assert.Equal(HttpStatusCode.BadRequest, await s.Api.StatusAsync(s.Troca.Token, HttpMethod.Post, $"/contact-center/items/{s.ItemA}/customer-link", new { customerRef = new string('x', 129) }));
        Assert.Equal(HttpStatusCode.Unauthorized, await s.Api.StatusAsync(null, HttpMethod.Post, $"/contact-center/items/{s.ItemA}/customer-link", new { customerRef = "CLI-100" }));

        Assert.Empty(await LinhasAsync(s.Tenant));
        Assert.Equal(0, await AuditoriaNoOutboxAsync(s.Tenant));
    }

    [Fact]
    public async Task Sessao_CustomerOnly_nao_alcanca_as_rotas_de_vinculo_do_atendente()
    {
        var s = await MontarAsync("e22-ag-co", modo: "CustomerOnly");
        await using var servidor = s.Api;

        Assert.Equal(HttpStatusCode.Forbidden, await s.Api.StatusAsync(s.Troca.Token, HttpMethod.Post, $"/contact-center/items/{s.ItemA}/customer-link", new { customerRef = "CLI-100" }));
        Assert.Equal(HttpStatusCode.Forbidden, await s.Api.StatusAsync(s.Troca.Token, HttpMethod.Delete, $"/contact-center/items/{s.ItemA}/customer-link/CLI-100"));
        Assert.Empty(await LinhasAsync(s.Tenant));
    }

    [Fact]
    public async Task Vinculo_feito_pelo_sistema_externo_nao_concede_acesso_a_ninguem()
    {
        var s = await MontarAsync("e22-sem-acesso");
        await using var servidor = s.Api;
        // Um segundo usuário embutido, SEM cliente em foco: o que ele enxerga vem das concessões dele, nunca de vínculo.
        var verifier = EmbeddedTestSupport.NovoVerifier();
        var ticket = await s.Erp.EmitirAsync("u-sem-cliente", EmbeddedTestSupport.Challenge(verifier));
        var outro = await EmbeddedTestSupport.TrocarAsync(s.Api, ticket.Ticket, verifier);
        var caminhos = new[] { $"/contact-center/items/{s.ItemA}", $"/contact-center/items/{s.ItemA}/conversation", $"/contact-center/items/{s.ItemB}", $"/contact-center/items/{s.ItemB}/messages" };
        var antes = new List<HttpStatusCode>();
        foreach (var caminho in caminhos)
        {
            antes.Add(await s.Api.StatusAsync(outro.Token, HttpMethod.Get, caminho));
        }

        Assert.Equal((true, false), await OkAsync(await VincularAsync(s.Erp, "CLI-100", "+" + TelA1)));
        Assert.Equal((false, false), await OkAsync(await VincularAsync(s.Erp, "CLI-200", "+" + TelB))); // o contato da organização B nem resolve

        // O acesso é exatamente o de antes: o vínculo não abriu nem fechou nada (a organização B segue inexistente para a sessão embutida).
        var depois = new List<HttpStatusCode>();
        foreach (var caminho in caminhos)
        {
            depois.Add(await s.Api.StatusAsync(outro.Token, HttpMethod.Get, caminho));
        }

        Assert.Equal(antes, depois);
        Assert.Equal(HttpStatusCode.NotFound, depois[2]);
        Assert.Equal(HttpStatusCode.NotFound, depois[3]);

        // Sem o cliente em foco na sessão, não há como vincular pelo atendente, mesmo com o vínculo do sistema externo já existente.
        Assert.Equal(HttpStatusCode.NotFound, await s.Api.StatusAsync(outro.Token, HttpMethod.Post, $"/contact-center/items/{s.ItemA}/customer-link", new { customerRef = "CLI-100" }));
    }

    // ------------------------------------------------------------------ lista fechada de rotas

    [Theory]
    [InlineData("POST", "/contact-center/items/3f2504e0-4f89-11d3-9a0c-0305e82c3301/customer-link", true)]
    [InlineData("DELETE", "/contact-center/items/3f2504e0-4f89-11d3-9a0c-0305e82c3301/customer-link/CLI-100", true)]
    [InlineData("DELETE", "/contact-center/items/3f2504e0-4f89-11d3-9a0c-0305e82c3301/customer-link", false)]
    [InlineData("DELETE", "/contact-center/items/3f2504e0-4f89-11d3-9a0c-0305e82c3301/customer-link/a/b", false)]
    [InlineData("PUT", "/contact-center/items/3f2504e0-4f89-11d3-9a0c-0305e82c3301/customer-link", false)]
    [InlineData("GET", "/contact-center/items/3f2504e0-4f89-11d3-9a0c-0305e82c3301/customer-link", false)]
    [InlineData("DELETE", "/contact-center/items/3f2504e0-4f89-11d3-9a0c-0305e82c3301/customer-link/../x", false)]
    [InlineData("PUT", "/embed/v1/customer-links", false)] // a rota HMAC não é da sessão embutida
    [InlineData("DELETE", "/embed/v1/customer-links", false)]
    public void A_lista_fechada_admite_so_as_duas_rotas_do_atendente_e_nenhuma_chega_ao_CustomerOnly(string metodo, string caminho, bool esperado)
    {
        Assert.Equal(esperado, EmbeddedRoutePolicy.IsAllowed(metodo, caminho));
        Assert.False(EmbeddedRoutePolicy.IsAllowed(metodo, caminho, customerOnly: true));
    }
}
