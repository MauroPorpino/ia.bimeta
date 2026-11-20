using System.Net;
using System.Text.Json;
using IaBimeta.ContactCenter.Application;
using IaBimeta.Identity.Application;
using IaBimeta.Identity.Domain;
using IaBimeta.IntegrationTests.Api;
using IaBimeta.IntegrationTests.ContactCenter;
using IaBimeta.IntegrationTests.Conversations;
using IaBimeta.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Embedded;

/// <summary>
/// E1-R: a organização da conexão Embedded é o limite DURO de todo o Contact Center (filas, itens, mensagens, atribuição, transferência e recibos), mesmo que o usuário federado participe de filas de outra organização do MESMO
/// tenant (equipe compartilhada é decisão de quem gere a fila e vale para o painel do próprio ia.bimeta, não para a sessão embutida). E o modo <c>CustomerOnly</c> não alcança a fila geral nem atendimento algum até a autorização por
/// cliente (E2). API real e PostgreSQL real; duas organizações no mesmo tenant.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class EmbeddedOrganizacaoApiTests(PostgresFixture banco)
{
    private sealed record Cena(
        CenarioDeConversas C, ApiServer Api, string TokenDoAdmin, ConexaoDto Conexao, ErpAssinante Erp, Guid OrgA, Guid OrgB, Guid FilaA, Guid FilaB, Guid ItemA, Guid ItemB, TrocaDto Troca);

    private async Task<Cena> MontarAsync(string prefixo, string modo = "FullAttendant", bool membroDaFilaB = true)
    {
        var c = await CenarioDeConversas.CriarAsync(banco, prefixo);
        var orgA = c.OrganizacaoId;
        var orgB = (await banco.CriarOrganizacaoAsync(c.Tenant, $"Organização B {prefixo}")).Id;
        var admin = await banco.CriarAdministradorDoTenantAsync(c.Tenant);
        var api = await ApiServer.StartAsync("Production", EmbeddedTestSupport.Configuracao());
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
        var sessaoA = (await c.EntrarAsync("5511999990101", texto: "Pergunta da organização A")).SessionId;
        var sessaoB = (await c.EntrarAsync("5511999990202", texto: "Pergunta CONFIDENCIAL da organização B", organizacao: orgB, unidade: unidadeB, canal: canalB)).SessionId;
        var itemA = (await c.NaFilaAsync(sessaoA, filaA)).Id;
        var itemB = (await c.NaFilaAsync(sessaoB, filaB)).Id;

        var verifier = EmbeddedTestSupport.NovoVerifier();
        var ticket = await erp.EmitirAsync("u-organizacao", EmbeddedTestSupport.Challenge(verifier));
        var troca = await EmbeddedTestSupport.TrocarAsync(api, ticket.Ticket, verifier);
        await c.AdicionarAtendenteAsync(filaA, troca.User.Id);
        if (membroDaFilaB)
        {
            await c.AdicionarAtendenteAsync(filaB, troca.User.Id); // equipe compartilhada: participa de uma fila de OUTRA organização do mesmo tenant
        }

        return new Cena(c, api, token, conexao, erp, orgA, orgB, filaA, filaB, itemA, itemB, troca);
    }

    private static async Task<List<Guid>> IdsAsync(ApiServer api, string token, string caminho) =>
        (await api.LerAsync<JsonElement>(token, caminho)).EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).ToList();

    [Fact]
    public async Task Sessao_embutida_so_ve_filas_e_itens_da_organizacao_da_conexao_mesmo_participando_de_fila_de_outra()
    {
        var s = await MontarAsync("emb-org-lista");
        await using var servidor = s.Api;

        // Controle: o próprio painel do ia.bimeta (sessão interativa do administrador do tenant) enxerga as duas organizações.
        Assert.Equivalent(new[] { s.ItemA, s.ItemB }, await IdsAsync(s.Api, s.TokenDoAdmin, "/contact-center/items"));

        // Antes: a participação na fila B abria a fila e o item B (a organização da conexão só limitava as CONCESSÕES, não a participação).
        Assert.Equal(new[] { s.ItemA }, await IdsAsync(s.Api, s.Troca.Token, "/contact-center/items"));
        Assert.Equal(new[] { s.FilaA }, await IdsAsync(s.Api, s.Troca.Token, "/contact-center/me/queues"));
        Assert.Equal(new[] { s.ItemA }, await IdsAsync(s.Api, s.Troca.Token, $"/contact-center/items?queueId={s.FilaA}"));
        Assert.Empty(await IdsAsync(s.Api, s.Troca.Token, $"/contact-center/items?queueId={s.FilaB}"));
    }

    [Fact]
    public async Task Item_de_outra_organizacao_e_inexistente_em_toda_operacao_da_sessao_embutida()
    {
        var s = await MontarAsync("emb-org-itens");
        await using var servidor = s.Api;
        var token = s.Troca.Token;
        var corpo = new { requestId = Guid.NewGuid() };

        // Leituras do item B e da conversa dele.
        foreach (var caminho in new[] { "", "/history", "/conversation", "/messages", "/reply-options" })
        {
            Assert.True(
                await s.Api.StatusAsync(token, HttpMethod.Get, $"/contact-center/items/{s.ItemB}{caminho}") is HttpStatusCode.NotFound,
                $"GET item B{caminho} deveria ser 404 para a sessão embutida.");
        }

        // Operações sobre o item B, inclusive as de atribuição, transferência, resposta e encerramento (a sessão é MEMBRO da fila B).
        var operacoes = new (string Caminho, object Corpo)[]
        {
            ("claim", new { requestId = Guid.NewGuid() }),
            ("release", new { requestId = Guid.NewGuid() }),
            ("transfer", new { queueId = s.FilaA, requestId = Guid.NewGuid() }),
            ("return-to-ai", new { requestId = Guid.NewGuid() }),
            ("reply", new { requestId = Guid.NewGuid(), text = "intrusão" }),
            ("close", new { requestId = Guid.NewGuid() }),
        };
        foreach (var (caminho, c) in operacoes)
        {
            using var resposta = await s.Api.EnviarAsync(token, HttpMethod.Post, $"/contact-center/items/{s.ItemB}/{caminho}", c);
            Assert.True(resposta.StatusCode is HttpStatusCode.NotFound, $"POST item B/{caminho} deveria ser 404, foi {(int)resposta.StatusCode}.");
        }

        // A fila B inteira: assumir o próximo item dela.
        using var proximo = await s.Api.EnviarAsync(token, HttpMethod.Post, $"/contact-center/queues/{s.FilaB}/claim-next", corpo);
        Assert.Equal(HttpStatusCode.NotFound, proximo.StatusCode);

        // Nada mudou no item B.
        var item = await s.C.ItemAsync(s.ItemB);
        Assert.Equal(("Waiting", null), (item!.Status.ToString(), item.AssignedUserId?.ToString()));

        // Controle: a organização da conexão continua operando normalmente.
        using var assumiu = await s.Api.EnviarAsync(token, HttpMethod.Post, $"/contact-center/items/{s.ItemA}/claim", new { requestId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.OK, assumiu.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await s.Api.StatusAsync(token, HttpMethod.Get, $"/contact-center/items/{s.ItemA}/conversation"));
    }

    [Fact]
    public async Task Transferir_item_da_organizacao_da_conexao_para_fila_de_outra_organizacao_e_recusado()
    {
        var s = await MontarAsync("emb-org-transfere");
        await using var servidor = s.Api;
        Assert.Equal(HttpStatusCode.OK, await s.Api.StatusAsync(s.Troca.Token, HttpMethod.Post, $"/contact-center/items/{s.ItemA}/claim", new { requestId = Guid.NewGuid() }));

        using var resposta = await s.Api.EnviarAsync(s.Troca.Token, HttpMethod.Post, $"/contact-center/items/{s.ItemA}/transfer", new { queueId = s.FilaB, requestId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
        Assert.Equal(s.FilaA, (await s.C.ItemAsync(s.ItemA))!.QueueId);
    }

    [Fact]
    public async Task Operacao_por_recibo_de_item_de_outra_organizacao_nao_e_reaproveitada_pela_sessao_embutida()
    {
        var s = await MontarAsync("emb-org-recibo");
        await using var servidor = s.Api;
        var pedido = Guid.NewGuid();

        // O MESMO usuário assume o item B por um caminho legítimo (contexto sem limite embutido, como o painel interativo dele faria) e fica com o recibo do pedido.
        var contextoInterativo = PostgresFixture.ContextoDeUsuario(s.C.Tenant, s.Troca.User.Id);
        var assumido = await s.C.AssumirAsync(s.ItemB, contextoInterativo, pedido);
        Assert.Equal(OperationOutcome.Completed, assumido.Outcome);

        // Antes: a repetição do MESMO pedido pela sessão embutida devolvia o resultado gravado (item B incluído), sem nenhuma checagem de organização.
        using var repetida = await s.Api.EnviarAsync(s.Troca.Token, HttpMethod.Post, $"/contact-center/items/{s.ItemB}/claim", new { requestId = pedido });
        Assert.NotEqual(HttpStatusCode.OK, repetida.StatusCode);
        var texto = await repetida.Content.ReadAsStringAsync();
        Assert.DoesNotContain(s.ItemB.ToString(), texto, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(s.FilaB.ToString(), texto, StringComparison.OrdinalIgnoreCase);

        // O mesmo requestId aplicado a OUTRA operação também não vaza nada.
        using var outraOperacao = await s.Api.EnviarAsync(s.Troca.Token, HttpMethod.Post, $"/contact-center/items/{s.ItemB}/release", new { requestId = pedido });
        Assert.NotEqual(HttpStatusCode.OK, outraOperacao.StatusCode);
    }

    [Fact]
    public async Task No_servico_a_organizacao_da_conexao_limita_participacao_responsavel_e_listas_para_a_sessao_embutida()
    {
        var s = await MontarAsync("emb-org-servico");
        await using var servidor = s.Api;
        var usuario = s.Troca.User.Id;
        var grants = EmbeddedSessionPolicy.Filter(RolePermissions.Expand(Role.Attendant, s.OrgA), s.OrgA);
        var embutido = new RequestContext(s.C.Tenant.TenantId, "embedded-session", new ActorPrincipal(usuario, Guid.NewGuid(), grants, new EmbeddedScope(s.OrgA, CustomerOnly: false)));

        var filas = await s.C.ComoAsync(embutido, sp => sp.GetRequiredService<ServiceQueueService>().ListMineAsync());
        var itens = await s.C.ItensAsync(embutido);

        Assert.Equal(new[] { s.FilaA }, filas.Select(f => f.Id).ToArray());
        Assert.Equal(new[] { s.ItemA }, itens.Select(i => i.Id).ToArray());
        Assert.Null(await s.C.ItemAsync(s.ItemB, embutido));
        await Assert.ThrowsAsync<ContactCenterResourceNotFoundException>(() => s.C.AssumirAsync(s.ItemB, embutido));

        // O mesmo usuário, SEM o limite embutido (o painel interativo), enxerga as duas filas: o limite é da sessão embutida, não do usuário.
        var interativo = PostgresFixture.ContextoDeUsuario(s.C.Tenant, usuario, grants.ToArray());
        Assert.Equivalent(new[] { s.FilaA, s.FilaB }, (await s.C.ComoAsync(interativo, sp => sp.GetRequiredService<ServiceQueueService>().ListMineAsync())).Select(f => f.Id).ToArray());
    }

    // Item 2: CustomerOnly bloqueado por padrão

    [Fact]
    public async Task Modo_CustomerOnly_nao_alcanca_fila_geral_nem_atendimento_pela_API_mas_alcanca_a_propria_sessao()
    {
        var s = await MontarAsync("emb-customer-only", modo: "CustomerOnly");
        await using var servidor = s.Api;
        var token = s.Troca.Token;
        Assert.Equal("CustomerOnly", s.Troca.Mode);
        var id = s.ItemA;

        var proibidas = new (HttpMethod Metodo, string Caminho)[]
        {
            (HttpMethod.Get, "/contact-center/me/queues"),
            (HttpMethod.Get, "/contact-center/me/presence"),
            (HttpMethod.Put, "/contact-center/me/presence"),
            (HttpMethod.Post, $"/contact-center/items/{id}/claim"),
            (HttpMethod.Post, $"/contact-center/items/{id}/reply"),
            (HttpMethod.Post, $"/contact-center/queues/{s.FilaA}/claim-next"),
        };
        foreach (var (metodo, caminho) in proibidas)
        {
            using var resposta = await s.Api.EnviarAsync(token, metodo, caminho, metodo == HttpMethod.Get ? null : new { requestId = Guid.NewGuid(), text = "x", status = "Available" });
            Assert.Equal(HttpStatusCode.Forbidden, resposta.StatusCode);
            Assert.Equal("embedded-route-forbidden", await EmbeddedTestSupport.CodigoDoProblemaAsync(resposta));
        }

        // E2.3: a LEITURA é roteável, mas sem vínculo ativo para um cliente autorizado nada aparece (lista vazia; item, conversa e mensagens inexistentes).
        Assert.Empty(await IdsAsync(s.Api, token, "/contact-center/items"));
        foreach (var caminho in new[] { $"/contact-center/items/{id}", $"/contact-center/items/{id}/conversation", $"/contact-center/items/{id}/messages" })
        {
            Assert.Equal(HttpStatusCode.NotFound, await s.Api.StatusAsync(token, HttpMethod.Get, caminho));
        }

        // A própria sessão e o resgate do contexto seguem disponíveis (o painel precisa deles para mostrar o cliente em foco).
        Assert.Equal(HttpStatusCode.OK, await s.Api.StatusAsync(token, HttpMethod.Get, "/embed/v1/session"));
        Assert.Equal(HttpStatusCode.OK, await s.Api.StatusAsync(token, HttpMethod.Get, "/auth/me"));
        var contexto = await s.Erp.EmitirContextoAsync("u-organizacao", "CLI-1", s.Troca.SessionId);
        Assert.Equal(HttpStatusCode.OK, await s.Api.StatusAsync(token, HttpMethod.Post, "/embed/v1/context", new { ticket = contexto.Ticket }));
    }

    [Fact]
    public async Task No_servico_o_modo_CustomerOnly_nega_filas_itens_e_operacoes_mesmo_sendo_membro_de_fila_da_propria_organizacao()
    {
        var s = await MontarAsync("emb-customer-servico", modo: "CustomerOnly");
        await using var servidor = s.Api;
        var usuario = s.Troca.User.Id;
        var soCliente = new RequestContext(s.C.Tenant.TenantId, "embedded-session", new ActorPrincipal(usuario, Guid.NewGuid(), Array.Empty<PermissionGrant>(), new EmbeddedScope(s.OrgA, CustomerOnly: true)));

        // Defesa em profundidade: mesmo que uma rota fosse aberta por engano, o serviço nega (a participação na fila da PRÓPRIA organização não vale para CustomerOnly).
        Assert.Empty(await s.C.ComoAsync(soCliente, sp => sp.GetRequiredService<ServiceQueueService>().ListMineAsync()));
        Assert.Empty(await s.C.ItensAsync(soCliente)); // E2.3: a lista do CustomerOnly é só a dos clientes autorizados (aqui, nenhum), nunca a das filas
        Assert.Null(await s.C.ItemAsync(s.ItemA, soCliente));
        await Assert.ThrowsAsync<ContactCenterResourceNotFoundException>(() => s.C.AssumirAsync(s.ItemA, soCliente));
        await Assert.ThrowsAsync<ContactCenterResourceNotFoundException>(() => s.C.AssumirProximaAsync(s.FilaA, soCliente));

        // O item A segue intacto e disponível para quem pode.
        Assert.Equal("Waiting", (await s.C.ItemAsync(s.ItemA))!.Status.ToString());
    }

    [Fact]
    public async Task Alterar_o_modo_da_conexao_de_FullAttendant_para_CustomerOnly_derruba_o_acesso_da_sessao_ja_aberta()
    {
        var s = await MontarAsync("emb-modo", membroDaFilaB: false);
        await using var servidor = s.Api;
        Assert.Equal(HttpStatusCode.OK, await s.Api.StatusAsync(s.Troca.Token, HttpMethod.Get, "/contact-center/items"));

        Assert.Equal(HttpStatusCode.OK, await s.Api.StatusAsync(s.TokenDoAdmin, HttpMethod.Put, $"/embedded/connections/{s.Conexao.Id}", new
        {
            name = "ERP", mode = "CustomerOnly", provisioning = "JitAttendant", allowedOrigins = new[] { EmbeddedTestSupport.Origem }, allowStartConversation = false,
        }));

        // O modo é lido a cada requisição: a sessão aberta perde a fila geral e o atendimento na hora (a leitura passa a ser só dos clientes autorizados, aqui nenhum vinculado).
        Assert.Empty(await IdsAsync(s.Api, s.Troca.Token, "/contact-center/items"));
        Assert.Equal(HttpStatusCode.Forbidden, await s.Api.StatusAsync(s.Troca.Token, HttpMethod.Get, "/contact-center/me/queues"));
        Assert.Equal(HttpStatusCode.Forbidden, await s.Api.StatusAsync(s.Troca.Token, HttpMethod.Post, $"/contact-center/items/{s.ItemA}/claim", new { requestId = Guid.NewGuid() }));
        Assert.Equal(HttpStatusCode.OK, await s.Api.StatusAsync(s.Troca.Token, HttpMethod.Get, "/embed/v1/session"));
    }
}
