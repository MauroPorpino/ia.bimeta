using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IaBimeta.IntegrationTests.Api;
using IaBimeta.IntegrationTests.ContactCenter;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.Identity.Domain;
using Npgsql;

namespace IaBimeta.IntegrationTests.Embedded;

/// <summary>
/// E2.3 (ADR 0041, seção 3 e D5): o histórico por cliente e a LEITURA do modo <c>CustomerOnly</c>. API real e PostgreSQL real. As cinco condições são simultâneas e relidas a cada requisição: tenant, sessão embutida ativa,
/// cliente autorizado na sessão, vínculo ATIVO e organização da conexão. O vínculo localiza o contato; nunca concede acesso por si.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class HistoricoPorClienteApiTests(PostgresFixture banco)
{
    private const string TelA1 = "5511999990101";
    private const string TelA2 = "5511999990303";
    private const string TelB = "5511999990202";

    private sealed record Cena(
        CenarioDeConversas C, ApiServer Api, string TokenDoAdmin, Guid OrgA, Guid OrgB, Guid FilaA,
        ConexaoDto ConFa, ErpAssinante ErpFa, TrocaDto Fa,
        ConexaoDto ConCo, ErpAssinante ErpCo, TrocaDto Co,
        Guid ItemA1, Guid ItemA2, Guid ItemB, Guid SessaoA1)
    {
        public Guid Tenant => C.Tenant.TenantId.Value;
    }

    private async Task<Cena> MontarAsync(string prefixo, ApiServer? api = null, string telA1 = TelA1, string telA2 = TelA2, string telB = TelB, string clienteDoCo = "CLI-100")
    {
        var c = await CenarioDeConversas.CriarAsync(banco, prefixo);
        var orgA = c.OrganizacaoId;
        var orgB = (await banco.CriarOrganizacaoAsync(c.Tenant, $"Organização B {prefixo}")).Id;
        var admin = await banco.CriarAdministradorDoTenantAsync(c.Tenant);
        api ??= await ApiServer.StartAsync("Production", EmbeddedTestSupport.Configuracao());
        var token = await api.LoginAsync(admin);

        async Task<(ConexaoDto, ErpAssinante)> ConexaoAsync(string nome, string modo)
        {
            var con = await api.CriarAsync<ConexaoDto>(token, "/embedded/connections", new
            {
                organizationId = orgA, name = nome, mode = modo, provisioning = "JitAttendant", allowedOrigins = new[] { EmbeddedTestSupport.Origem }, allowStartConversation = false,
            });
            var chave = await api.CriarAsync<ChaveCriadaDto>(token, $"/embedded/connections/{con.Id}/keys", new { });
            return (con, new ErpAssinante(api, chave.KeyId, chave.Secret));
        }

        var (conFa, erpFa) = await ConexaoAsync("ERP completo", "FullAttendant");
        var (conCo, erpCo) = await ConexaoAsync("ERP só cliente", "CustomerOnly");

        var filaA = (await c.CriarFilaAsync("Fila A", organizacao: orgA)).Id;
        var filaB = (await c.CriarFilaAsync("Fila B", organizacao: orgB)).Id;
        var unidadeB = await c.CriarUnidadeAsync(orgB, "Matriz B", matriz: true);
        var canalB = await c.CriarCanalAsync(new[] { unidadeB }, unidadeB, organizacao: orgB);
        var sessaoA1 = (await c.EntrarAsync(telA1, texto: "Pergunta A1")).SessionId;
        var sessaoA2 = (await c.EntrarAsync(telA2, texto: "Pergunta A2")).SessionId;
        var sessaoB = (await c.EntrarAsync(telB, texto: "Pergunta B", organizacao: orgB, unidade: unidadeB, canal: canalB)).SessionId;
        var itemA1 = (await c.NaFilaAsync(sessaoA1, filaA)).Id;
        var itemA2 = (await c.NaFilaAsync(sessaoA2, filaA)).Id;
        var itemB = (await c.NaFilaAsync(sessaoB, filaB)).Id;

        var vf = EmbeddedTestSupport.NovoVerifier();
        var fa = await EmbeddedTestSupport.TrocarAsync(api, (await erpFa.EmitirAsync("u-fa", EmbeddedTestSupport.Challenge(vf), cliente: "CLI-100")).Ticket, vf);
        var vc = EmbeddedTestSupport.NovoVerifier();
        var co = await EmbeddedTestSupport.TrocarAsync(api, (await erpCo.EmitirAsync("u-co", EmbeddedTestSupport.Challenge(vc), cliente: clienteDoCo)).Ticket, vc);
        await c.AdicionarAtendenteAsync(filaA, fa.User.Id);
        await c.AdicionarAtendenteAsync(filaA, co.User.Id); // participar da fila NÃO abre nada ao CustomerOnly

        return new Cena(c, api, token, orgA, orgB, filaA, conFa, erpFa, fa, conCo, erpCo, co, itemA1, itemA2, itemB, sessaoA1);
    }

    // Apoio

    private static async Task<HttpStatusCode> VincularAsync(ErpAssinante erp, string cliente, string telefone)
    {
        using var r = await erp.EnviarAsync("/embed/v1/customer-links", new { customerRef = cliente, phone = "+" + telefone }, metodo: HttpMethod.Put);
        return r.StatusCode;
    }

    private static async Task<HttpStatusCode> DesvincularAsync(ErpAssinante erp, string cliente)
    {
        using var r = await erp.EnviarAsync("/embed/v1/customer-links", new { customerRef = cliente }, metodo: HttpMethod.Delete);
        return r.StatusCode;
    }

    private static async Task<(HttpStatusCode Status, string Corpo)> GetAsync(ApiServer api, string token, string caminho)
    {
        using var r = await api.EnviarAsync(token, HttpMethod.Get, caminho);
        return (r.StatusCode, await r.Content.ReadAsStringAsync());
    }

    private static async Task<JsonElement> JsonAsync(ApiServer api, string token, string caminho)
    {
        var (status, corpo) = await GetAsync(api, token, caminho);
        Assert.Equal(HttpStatusCode.OK, status);
        return JsonDocument.Parse(corpo).RootElement.Clone();
    }

    private static List<Guid> Itens(JsonElement historico) => historico.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("itemId").GetGuid()).ToList();

    private static async Task<List<Guid>> IdsDaListaAsync(ApiServer api, string token) =>
        (await JsonAsync(api, token, "/contact-center/items")).EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList();

    private static async Task<long> ContarAsync(string sql, Guid tenant)
    {
        await using var conexao = new NpgsqlConnection(TestDatabase.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = new NpgsqlCommand(sql, conexao);
        comando.Parameters.AddWithValue("t", tenant);
        return Convert.ToInt64(await comando.ExecuteScalarAsync());
    }

    private static string Hist(string cliente, string? extra = null) => $"/contact-center/customers/{cliente}/history{extra}";

    // ------------------------------------------------------------------ histórico por cliente

    [Fact]
    public async Task Historico_do_cliente_autorizado_devolve_os_itens_dos_contatos_vinculados_com_o_formato_do_contrato()
    {
        var s = await MontarAsync("e23-hist");
        await using var servidor = s.Api;
        Assert.Equal(HttpStatusCode.OK, await VincularAsync(s.ErpFa, "CLI-100", TelA1));

        var h = await JsonAsync(s.Api, s.Fa.Token, Hist("CLI-100"));

        Assert.Equal("CLI-100", h.GetProperty("customerRef").GetString());
        Assert.True(h.GetProperty("resolved").GetBoolean());
        Assert.False(h.GetProperty("shared").GetBoolean());
        Assert.Equal(new[] { s.ItemA1 }, Itens(h)); // só o contato vinculado: nem o A2 (sem vínculo) nem o B (outra organização)
        var item = h.GetProperty("items")[0];
        Assert.Equal(s.SessaoA1, item.GetProperty("sessionId").GetGuid());
        Assert.Equal(s.FilaA, item.GetProperty("queueId").GetGuid());
        Assert.Equal("Waiting", item.GetProperty("status").GetString());
        Assert.True(item.TryGetProperty("openedAt", out _) && item.TryGetProperty("contactId", out _));
        Assert.Equal(JsonValueKind.Null, h.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task Cliente_nao_autorizado_na_sessao_sessao_interativa_e_sem_vinculo_nao_se_distinguem_de_inexistente()
    {
        var s = await MontarAsync("e23-oraculo");
        await using var servidor = s.Api;
        await VincularAsync(s.ErpFa, "CLI-100", TelA1);
        await VincularAsync(s.ErpFa, "CLI-999", TelA2); // vínculo existe, mas o cliente NÃO está autorizado na sessão do atendente

        var respostas = new[]
        {
            await GetAsync(s.Api, s.Fa.Token, Hist("CLI-999")), // vinculado, não autorizado na sessão
            await GetAsync(s.Api, s.Fa.Token, Hist("CLI-QUE-NAO-EXISTE")), // inexistente
            await GetAsync(s.Api, s.TokenDoAdmin, Hist("CLI-100")), // sessão interativa: não há cliente em foco
            await GetAsync(s.Api, s.Co.Token, Hist("CLI-100")), // autorizado na sessão do CO, mas o vínculo é de OUTRA conexão (o CO nunca vinculou)
        };

        Assert.Equal(HttpStatusCode.NotFound, respostas[0].Status);
        Assert.Equal(HttpStatusCode.NotFound, respostas[1].Status);
        Assert.Equal(HttpStatusCode.NotFound, respostas[2].Status);
        Assert.Equal(respostas[0].Corpo, respostas[1].Corpo); // mesmo corpo
        Assert.Equal(respostas[1].Corpo, respostas[2].Corpo);

        // Autorizado, mas sem vínculo ATIVO nesta conexão: 200 vazio e não resolvido (o cliente é do atendente; o que não existe é o vínculo).
        Assert.Equal(HttpStatusCode.OK, respostas[3].Status);
        var vazio = JsonDocument.Parse(respostas[3].Corpo).RootElement;
        Assert.False(vazio.GetProperty("resolved").GetBoolean());
        Assert.Empty(Itens(vazio));
    }

    [Fact]
    public async Task Referencia_externa_sozinha_nao_concede_acesso_o_cliente_vem_do_ticket_assinado_nunca_da_rota_nem_do_corpo()
    {
        var s = await MontarAsync("e23-ref");
        await using var servidor = s.Api;
        await VincularAsync(s.ErpFa, "CLI-100", TelA1);

        // Um segundo usuário embutido, com OUTRO cliente em foco: conhece a referência CLI-100, mas ela não foi autorizada na sessão dele.
        var v = EmbeddedTestSupport.NovoVerifier();
        var outro = await EmbeddedTestSupport.TrocarAsync(s.Api, (await s.ErpFa.EmitirAsync("u-fa-2", EmbeddedTestSupport.Challenge(v), cliente: "CLI-200")).Ticket, v);

        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(s.Api, outro.Token, Hist("CLI-100"))).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(s.Api, outro.Token, Hist("cli-100"))).Status); // nem por variação de caixa
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(s.Api, outro.Token, Hist("CLI-200"))).Status); // o dele: 200, e sem vínculo
        Assert.False((await JsonAsync(s.Api, outro.Token, Hist("CLI-200"))).GetProperty("resolved").GetBoolean());
    }

    [Fact]
    public async Task Historico_inclui_conversas_anteriores_e_posteriores_ao_vinculo_com_paginacao_estavel_sem_repeticao()
    {
        var s = await MontarAsync("e23-pag");
        await using var servidor = s.Api;
        var esperados = new List<Guid> { s.ItemA1 }; // a conversa de ANTES do vínculo

        // Mais quatro conversas do MESMO contato (a anterior é encerrada): duas antes de vincular e duas depois.
        async Task NovaConversaAsync()
        {
            var atual = await EscalarSessaoAbertaAsync(s.Tenant, TelA1);
            await s.C.EncerrarSessaoAsync(atual);
            var nova = (await s.C.EntrarAsync(TelA1, texto: "Outra conversa")).SessionId;
            esperados.Add((await s.C.NaFilaAsync(nova, s.FilaA)).Id);
            await Task.Delay(15);
        }

        await NovaConversaAsync();
        await NovaConversaAsync();
        Assert.Equal(HttpStatusCode.OK, await VincularAsync(s.ErpFa, "CLI-100", TelA1));
        await NovaConversaAsync();
        await NovaConversaAsync();
        Assert.Equal(5, esperados.Count);

        // Duas por página: três páginas, a mais recente primeiro, sem repetir e sem perder.
        var vistos = new List<Guid>();
        string? cursor = null;
        var paginas = 0;
        do
        {
            var h = await JsonAsync(s.Api, s.Fa.Token, Hist("CLI-100", $"?limit=2{(cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor))}"));
            vistos.AddRange(Itens(h));
            cursor = h.GetProperty("nextCursor").GetString();
            paginas++;

            // Uma conversa NOVA entre as páginas não duplica nem desloca o que falta (a ordem é por chave, e a nova é a mais recente: fica antes do cursor).
            if (paginas == 1)
            {
                await NovaConversaAsync();
            }
        }
        while (cursor is not null && paginas < 10);

        Assert.Equal(3, paginas);
        Assert.Equal(vistos.Count, vistos.Distinct().Count());
        Assert.Equal(esperados.Take(5).OrderBy(i => i), vistos.OrderBy(i => i)); // as 5 conversas do início, todas (a nova, depois da 1.ª página, não entra nem repete)
        Assert.Equal(esperados.Take(5).Reverse(), vistos); // da mais recente para a mais antiga

        // Limites e cursor: erro do pedido, nunca ignorado.
        Assert.Equal(HttpStatusCode.BadRequest, (await GetAsync(s.Api, s.Fa.Token, Hist("CLI-100", "?limit=0"))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await GetAsync(s.Api, s.Fa.Token, Hist("CLI-100", "?limit=101"))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await GetAsync(s.Api, s.Fa.Token, Hist("CLI-100", "?cursor=adulterado"))).Status);
    }

    /// <summary>
    /// Evidência da ressalva da E2.3 (ADR 0041, seção 3): a paginação é por SESSÃO e o item é resolvido DEPOIS. Uma sessão que nunca entrou em fila (respondida só pela IA ou por fluxo) não tem item: a página dela vem
    /// VAZIA e COM cursor, e o item autorizado que vem depois só aparece nas páginas seguintes. O cursor nunca trunca: seguindo-o até o fim, todos os itens autorizados são alcançados, sem repetir.
    /// </summary>
    [Fact]
    public async Task Pagina_pode_vir_vazia_com_cursor_e_o_cursor_alcanca_todos_os_itens_autorizados_sem_truncar()
    {
        var s = await MontarAsync("e23-vazia");
        await using var servidor = s.Api;

        // Duas conversas MAIS RECENTES do mesmo contato que nunca entraram em fila (sem item); a mais antiga (A1) tem item.
        for (var i = 0; i < 2; i++)
        {
            await s.C.EncerrarSessaoAsync(await EscalarSessaoAbertaAsync(s.Tenant, TelA1));
            await s.C.EntrarAsync(TelA1, texto: "Conversa respondida só pela IA");
            await Task.Delay(15);
        }

        Assert.Equal(HttpStatusCode.OK, await VincularAsync(s.ErpFa, "CLI-100", TelA1));

        var paginas = new List<(int Itens, bool TemCursor)>();
        var vistos = new List<Guid>();
        string? cursor = null;
        do
        {
            var h = await JsonAsync(s.Api, s.Fa.Token, Hist("CLI-100", $"?limit=1{(cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor))}"));
            var itens = Itens(h);
            vistos.AddRange(itens);
            cursor = h.GetProperty("nextCursor").GetString();
            paginas.Add((itens.Count, cursor is not null));
        }
        while (cursor is not null && paginas.Count < 10);

        Assert.Equal(new[] { (0, true), (0, true), (1, false) }, paginas); // páginas vazias COM cursor, e o item só na última
        Assert.Equal(new[] { s.ItemA1 }, vistos); // alcançado, sem truncar nem repetir
    }

    private static async Task<Guid> EscalarSessaoAbertaAsync(Guid tenant, string telefone)
    {
        await using var conexao = new NpgsqlConnection(TestDatabase.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = new NpgsqlCommand(
            "select s.id from conversations.service_sessions s join conversations.contacts c on c.tenant_id = s.tenant_id and c.id = s.contact_id where s.tenant_id = @t and c.external_ref = @r and s.closed_at is null", conexao);
        comando.Parameters.AddWithValue("t", tenant);
        comando.Parameters.AddWithValue("r", telefone);
        return (Guid)(await comando.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Desvincular_impede_as_consultas_seguintes_mas_nao_apaga_conversas_mensagens_nem_itens()
    {
        var s = await MontarAsync("e23-desv");
        await using var servidor = s.Api;
        await VincularAsync(s.ErpFa, "CLI-100", TelA1);
        await VincularAsync(s.ErpCo, "CLI-100", TelA1);
        Assert.Equal(new[] { s.ItemA1 }, Itens(await JsonAsync(s.Api, s.Fa.Token, Hist("CLI-100"))));
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(s.Api, s.Co.Token, $"/contact-center/items/{s.ItemA1}")).Status);
        var sessoes = await ContarAsync("select count(*) from conversations.service_sessions where tenant_id = @t", s.Tenant);
        var mensagens = await ContarAsync("select count(*) from conversations.messages where tenant_id = @t", s.Tenant);
        var itens = await ContarAsync("select count(*) from contact_center.queue_items where tenant_id = @t", s.Tenant);

        Assert.Equal(HttpStatusCode.NoContent, await DesvincularAsync(s.ErpFa, "CLI-100"));
        Assert.Equal(HttpStatusCode.NoContent, await DesvincularAsync(s.ErpCo, "CLI-100"));

        // Na requisição SEGUINTE: o histórico some (cliente sem vínculo ativo) e o CustomerOnly perde o item.
        var depois = await JsonAsync(s.Api, s.Fa.Token, Hist("CLI-100"));
        Assert.False(depois.GetProperty("resolved").GetBoolean());
        Assert.Empty(Itens(depois));
        Assert.Empty(await IdsDaListaAsync(s.Api, s.Co.Token));
        foreach (var caminho in new[] { "", "/conversation", "/messages" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(s.Api, s.Co.Token, $"/contact-center/items/{s.ItemA1}{caminho}")).Status);
        }

        // Nada foi apagado, e o atendente que já enxergava a conversa pelas concessões continua lendo.
        Assert.Equal(sessoes, await ContarAsync("select count(*) from conversations.service_sessions where tenant_id = @t", s.Tenant));
        Assert.Equal(mensagens, await ContarAsync("select count(*) from conversations.messages where tenant_id = @t", s.Tenant));
        Assert.Equal(itens, await ContarAsync("select count(*) from contact_center.queue_items where tenant_id = @t", s.Tenant));
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(s.Api, s.Fa.Token, $"/contact-center/items/{s.ItemA1}")).Status);

        // Revincular: volta a valer, com a mesma conversa (o vínculo é do contato, e o histórico é preservado).
        await VincularAsync(s.ErpFa, "CLI-100", TelA1);
        Assert.Equal(new[] { s.ItemA1 }, Itens(await JsonAsync(s.Api, s.Fa.Token, Hist("CLI-100"))));
    }

    // ------------------------------------------------------------------ CustomerOnly

    [Fact]
    public async Task CustomerOnly_le_so_os_itens_do_cliente_autorizado_com_vinculo_ativo_e_nada_mais()
    {
        var s = await MontarAsync("e23-co");
        await using var servidor = s.Api;

        // Sem vínculo: nada (mesmo sendo membro da fila A, e o item A1 existindo).
        Assert.Empty(await IdsDaListaAsync(s.Api, s.Co.Token));
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(s.Api, s.Co.Token, $"/contact-center/items/{s.ItemA1}")).Status);

        await VincularAsync(s.ErpCo, "CLI-100", TelA1);
        await VincularAsync(s.ErpCo, "CLI-200", TelA2); // vínculo de um cliente que NÃO está autorizado na sessão do CO

        Assert.Equal(new[] { s.ItemA1 }, await IdsDaListaAsync(s.Api, s.Co.Token)); // nem A2 (cliente não autorizado), nem B (outra organização)
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(s.Api, s.Co.Token, $"/contact-center/items/{s.ItemA1}")).Status);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(s.Api, s.Co.Token, $"/contact-center/items/{s.ItemA1}/conversation")).Status);
        var mensagens = await JsonAsync(s.Api, s.Co.Token, $"/contact-center/items/{s.ItemA1}/messages");
        Assert.Contains("Pergunta A1", mensagens.GetRawText());
        foreach (var fora in new[] { s.ItemA2, s.ItemB })
        {
            foreach (var caminho in new[] { "", "/conversation", "/messages" })
            {
                Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(s.Api, s.Co.Token, $"/contact-center/items/{fora}{caminho}")).Status);
            }
        }

        // O histórico por cliente também vale no CustomerOnly.
        Assert.Equal(new[] { s.ItemA1 }, Itens(await JsonAsync(s.Api, s.Co.Token, Hist("CLI-100"))));
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(s.Api, s.Co.Token, Hist("CLI-200"))).Status);
    }

    [Fact]
    public async Task CustomerOnly_nao_escreve_nem_opera_nem_altera_vinculo_e_o_servico_tambem_nega()
    {
        var s = await MontarAsync("e23-co-escrita");
        await using var servidor = s.Api;
        await VincularAsync(s.ErpCo, "CLI-100", TelA1);
        var id = s.ItemA1;
        var corpo = new { requestId = Guid.NewGuid(), text = "x", customerRef = "CLI-100", queueId = s.FilaA, templateId = Guid.NewGuid(), reasonCode = "x", userId = s.Co.User.Id };
        var fechadas = new (HttpMethod, string)[]
        {
            (HttpMethod.Post, $"/contact-center/items/{id}/claim"), (HttpMethod.Post, $"/contact-center/items/{id}/release"), (HttpMethod.Post, $"/contact-center/items/{id}/transfer"),
            (HttpMethod.Post, $"/contact-center/items/{id}/return-to-ai"), (HttpMethod.Post, $"/contact-center/items/{id}/reply"), (HttpMethod.Post, $"/contact-center/items/{id}/reply-template"),
            (HttpMethod.Post, $"/contact-center/items/{id}/reply-media"), (HttpMethod.Post, $"/contact-center/items/{id}/close"), (HttpMethod.Post, $"/contact-center/items/{id}/customer-link"),
            (HttpMethod.Delete, $"/contact-center/items/{id}/customer-link/CLI-100"), (HttpMethod.Get, $"/contact-center/items/{id}/reply-options"), (HttpMethod.Get, $"/contact-center/items/{id}/history"),
            (HttpMethod.Get, "/contact-center/me/queues"), (HttpMethod.Put, "/contact-center/me/presence"), (HttpMethod.Post, $"/contact-center/queues/{s.FilaA}/claim-next"),
        };
        foreach (var (metodo, caminho) in fechadas)
        {
            using var r = await s.Api.EnviarAsync(s.Co.Token, metodo, caminho, metodo == HttpMethod.Get ? null : corpo);
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
            Assert.Equal("embedded-route-forbidden", await EmbeddedTestSupport.CodigoDoProblemaAsync(r));
        }

        // Nada mudou: o item continua em espera e sem responsável, e o vínculo segue ativo.
        var item = await s.C.ItemAsync(id);
        Assert.Equal(("Waiting", null), (item!.Status.ToString(), item.AssignedUserId?.ToString()));

        // Defesa em profundidade (mesmo que uma rota fosse aberta por engano): as operações de escrita do serviço negam para o CustomerOnly, mesmo com o item autorizado para leitura.
        var contexto = new IaBimeta.SharedKernel.RequestContext(s.C.Tenant.TenantId, "embedded-session",
            new IaBimeta.SharedKernel.ActorPrincipal(s.Co.User.Id, s.Co.SessionId, Array.Empty<IaBimeta.SharedKernel.PermissionGrant>(), new IaBimeta.SharedKernel.EmbeddedScope(s.OrgA, CustomerOnly: true)));
        await Assert.ThrowsAsync<IaBimeta.ContactCenter.Application.ContactCenterResourceNotFoundException>(() => s.C.AssumirAsync(id, contexto));
    }

    [Fact]
    public async Task CustomerOnly_com_cliente_autorizado_de_outra_conexao_ou_de_outro_tenant_nao_ve_nada()
    {
        var s = await MontarAsync("e23-co-isola");
        await using var servidor = s.Api;
        var outro = await MontarAsync("e23-co-isola2", api: s.Api, telA1: TelA1, telA2: "5511999994444", telB: "5511999995555"); // mesmo telefone e mesmo cliente em OUTRO tenant

        // O vínculo foi feito só pela conexão FullAttendant (e pelo outro tenant): o CustomerOnly desta conexão não herda vínculo de outra conexão.
        await VincularAsync(s.ErpFa, "CLI-100", TelA1);
        await VincularAsync(outro.ErpCo, "CLI-100", TelA1);

        Assert.Empty(await IdsDaListaAsync(s.Api, s.Co.Token));
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(s.Api, s.Co.Token, $"/contact-center/items/{s.ItemA1}")).Status);

        // O CustomerOnly do outro tenant vê o item DELE, e nunca o deste tenant (nem o conhecendo pelo identificador).
        Assert.Equal(new[] { outro.ItemA1 }, await IdsDaListaAsync(outro.Api, outro.Co.Token));
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(outro.Api, outro.Co.Token, $"/contact-center/items/{s.ItemA1}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(outro.Api, outro.Co.Token, $"/contact-center/items/{s.ItemA1}/messages")).Status);
    }

    [Fact]
    public async Task Sessao_revogada_e_conexao_desativada_perdem_a_leitura_na_requisicao_seguinte()
    {
        var s = await MontarAsync("e23-revoga");
        await using var servidor = s.Api;
        await VincularAsync(s.ErpCo, "CLI-100", TelA1);
        Assert.Equal(new[] { s.ItemA1 }, await IdsDaListaAsync(s.Api, s.Co.Token));

        Assert.Equal(HttpStatusCode.OK, await s.Api.StatusAsync(s.TokenDoAdmin, HttpMethod.Post, $"/embedded/connections/{s.ConCo.Id}/disable"));

        foreach (var caminho in new[] { "/contact-center/items", $"/contact-center/items/{s.ItemA1}/messages", Hist("CLI-100") })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(s.Api, s.Co.Token, caminho)).Status);
        }

        // E o usuário FullAttendant, de outra conexão, segue funcionando (sessões independentes).
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(s.Api, s.Fa.Token, "/contact-center/items")).Status);
    }

    [Fact]
    public async Task Consulta_concorrente_com_a_desvinculacao_nunca_falha_e_termina_fechada()
    {
        var s = await MontarAsync("e23-conc");
        await using var servidor = s.Api;
        await VincularAsync(s.ErpCo, "CLI-100", TelA1);

        var leituras = Enumerable.Range(0, 10).Select(_ => Task.Run(async () =>
        {
            var lista = await GetAsync(s.Api, s.Co.Token, "/contact-center/items");
            var historia = await GetAsync(s.Api, s.Co.Token, Hist("CLI-100"));
            var mensagens = await GetAsync(s.Api, s.Co.Token, $"/contact-center/items/{s.ItemA1}/messages");
            return new[] { lista.Status, historia.Status, mensagens.Status };
        })).ToList();
        var desvinculo = Task.Run(async () =>
        {
            await Task.Delay(30);
            return await DesvincularAsync(s.ErpCo, "CLI-100");
        });

        var resultados = await Task.WhenAll(leituras);
        Assert.Equal(HttpStatusCode.NoContent, await desvinculo);

        // Durante a corrida cada resposta é "antes" (200) ou "depois" (200 vazio / 404): nunca 5xx.
        Assert.All(resultados.SelectMany(r => r), c => Assert.True(c is HttpStatusCode.OK or HttpStatusCode.NotFound, $"Status inesperado: {c}"));

        // Depois do commit da desvinculação, tudo fecha.
        Assert.Empty(await IdsDaListaAsync(s.Api, s.Co.Token));
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(s.Api, s.Co.Token, $"/contact-center/items/{s.ItemA1}/messages")).Status);
        Assert.False((await JsonAsync(s.Api, s.Co.Token, Hist("CLI-100"))).GetProperty("resolved").GetBoolean());
    }

    // ------------------------------------------------------------------ Standalone e Embedded

    [Fact]
    public async Task O_mesmo_usuario_interno_ve_os_mesmos_dados_nas_duas_modalidades_e_o_embutido_fica_limitado_ao_cliente_e_a_organizacao()
    {
        var s = await MontarAsync("e23-stand");
        await using var servidor = s.Api;
        await VincularAsync(s.ErpFa, "CLI-100", TelA1);
        await VincularAsync(s.ErpCo, "CLI-100", TelA1);

        // UM usuário interno: gerente da organização A E da organização B (concessões amplas no Standalone), participante da fila A, mapeado como identidade de AMBAS as conexões.
        var interno = await banco.CriarUsuarioAsync(s.C.Tenant, s.OrgA, Role.Manager);
        Assert.Equal(HttpStatusCode.NoContent, await s.Api.StatusAsync(s.TokenDoAdmin, HttpMethod.Put, $"/organizations/{s.OrgB}/users/{interno.Id}/role", new { role = "Manager" }));
        await s.C.AdicionarAtendenteAsync(s.FilaA, interno.Id);
        Assert.Equal(HttpStatusCode.OK, await s.Api.StatusAsync(s.TokenDoAdmin, HttpMethod.Put, $"/embedded/connections/{s.ConFa.Id}/identities", new { externalUserId = "interno-fa", userId = interno.Id }));
        Assert.Equal(HttpStatusCode.OK, await s.Api.StatusAsync(s.TokenDoAdmin, HttpMethod.Put, $"/embedded/connections/{s.ConCo.Id}/identities", new { externalUserId = "interno-co", userId = interno.Id }));

        var standalone = await s.Api.LoginAsync(interno);
        var vfa = EmbeddedTestSupport.NovoVerifier();
        var embutidoFa = await EmbeddedTestSupport.TrocarAsync(s.Api, (await s.ErpFa.EmitirAsync("interno-fa", EmbeddedTestSupport.Challenge(vfa), cliente: "CLI-100")).Ticket, vfa);
        var vco = EmbeddedTestSupport.NovoVerifier();
        var embutidoCo = await EmbeddedTestSupport.TrocarAsync(s.Api, (await s.ErpCo.EmitirAsync("interno-co", EmbeddedTestSupport.Challenge(vco), cliente: "CLI-100")).Ticket, vco);

        // Uma identidade interna única, sessões independentes.
        Assert.Equal(interno.Id, embutidoFa.User.Id);
        Assert.Equal(interno.Id, embutidoCo.User.Id);
        Assert.Equal(3, new[] { embutidoFa.Token, embutidoCo.Token, standalone }.Distinct().Count());

        // Standalone: as duas organizações. FullAttendant embutido: SÓ a organização da conexão (A), mesmo com concessão em B. CustomerOnly: SÓ o cliente autorizado (A1).
        var noStandalone = await IdsDaListaAsync(s.Api, standalone);
        Assert.Contains(s.ItemA1, noStandalone);
        Assert.Contains(s.ItemB, noStandalone);
        var noFa = await IdsDaListaAsync(s.Api, embutidoFa.Token);
        Assert.DoesNotContain(s.ItemB, noFa);
        Assert.Equal(new[] { s.ItemA1, s.ItemA2 }.OrderBy(i => i), noFa.OrderBy(i => i));
        Assert.Equal(new[] { s.ItemA1 }, await IdsDaListaAsync(s.Api, embutidoCo.Token));
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(s.Api, embutidoFa.Token, $"/contact-center/items/{s.ItemB}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(s.Api, embutidoCo.Token, $"/contact-center/items/{s.ItemA2}")).Status);

        // O MESMO dado, nas modalidades em que ele é permitido: o item A1 tem o mesmo conteúdo no Standalone, no FullAttendant e no CustomerOnly (um só registro, sem cópia por modalidade).
        var corpos = new List<string>();
        foreach (var token in new[] { standalone, embutidoFa.Token, embutidoCo.Token })
        {
            corpos.Add((await JsonAsync(s.Api, token, $"/contact-center/items/{s.ItemA1}/messages")).GetProperty("items").GetRawText());
        }

        Assert.Single(corpos.Distinct());
        Assert.Contains("Pergunta A1", corpos[0]);
        Assert.Equal(1, await ContarAsync("select count(*) from conversations.service_sessions s join conversations.contacts c on c.tenant_id = s.tenant_id and c.id = s.contact_id where s.tenant_id = @t and c.external_ref = '" + TelA1 + "'", s.Tenant));

        // O histórico por cliente é o mesmo nas duas modalidades embutidas, e o Standalone não o tem (não há cliente em foco).
        Assert.Equal(Itens(await JsonAsync(s.Api, embutidoFa.Token, Hist("CLI-100"))), Itens(await JsonAsync(s.Api, embutidoCo.Token, Hist("CLI-100"))));
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(s.Api, standalone, Hist("CLI-100"))).Status);

        // Desvincular fecha a leitura do CustomerOnly sem tocar nas outras modalidades do mesmo usuário.
        await DesvincularAsync(s.ErpCo, "CLI-100");
        Assert.Empty(await IdsDaListaAsync(s.Api, embutidoCo.Token));
        Assert.Contains(s.ItemA1, await IdsDaListaAsync(s.Api, standalone));
        Assert.Contains(s.ItemA1, await IdsDaListaAsync(s.Api, embutidoFa.Token));
    }
}
