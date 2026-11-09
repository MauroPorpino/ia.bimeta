using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using IaBimeta.Api.Operations;
using IaBimeta.Api.Provisioning;
using IaBimeta.IntegrationTests.Agents;
using Xunit.Abstractions;

namespace IaBimeta.IntegrationTests.Api;

/// <summary>
/// A revisão das lacunas do FAQ (ADR 0030) pela API, com Api e Worker reais, PostgreSQL real e a Meta e a OpenAI simuladas por HTTP: o fluxo COMPLETO (pergunta sem FAQ, lacuna, resposta humana, revisão, entrada no
/// rascunho, publicação autorizada e a próxima pergunta respondida com ZERO requisições à OpenAI), o isolamento entre empresas, as permissões (401, 403, 404), as regras (validação, conflito, nada publicado por revisar)
/// e a concorrência pelo HTTP.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ConhecimentoDoFaqNaOperacaoTests(PostgresFixture banco, ITestOutputHelper saida)
{
    private const string Pergunta = "Preciso levar documento para a coleta?";
    private const string RespostaDoAtendente = "Sim, leve um documento oficial com foto.";
    private const string RespostaRevisada = "Sim. Leve um documento oficial com foto (RG ou CNH).";
    private const string SenhaDoAdministrador = "Senha-Do-Admin-2026!";
    private const string SenhaDaAtendente = "Senha-Da-Atendente-2026!";
    private const string RespostaDaIA = "Resposta gerada pela IA simulada.";

    private sealed class Segredos : IProvisioningSecrets
    {
        public string? Get(string name) => name switch
        {
            "ADM_SENHA" => SenhaDoAdministrador,
            "ATD_SENHA" => SenhaDaAtendente,
            "IA_CHAVE" => EnsaioDeIa.ChaveCanario,
            _ => null,
        };
    }

    private sealed record Empresa(string Slug, ProvisioningReport Relatorio, string Waba, string Numero, Guid Webhook, string Admin, string Atendente);

    private static string NovoSlug() => $"con-{Guid.NewGuid().ToString("N")[..10]}";

    private static ProvisioningSpec Especificacao(string slug) => ProvisioningSpec.Parse($$"""
        {
          "tenant": { "slug": "{{slug}}", "name": "Laboratório {{slug}}" },
          "administrator": { "email": "admin@{{slug}}.local", "displayName": "Administradora", "passwordEnv": "ADM_SENHA" },
          "organization": { "name": "Laboratório {{slug}}", "unitName": "Matriz" },
          "queue": { "name": "Atendimento humano" },
          "ai": {
            "providerId": "openai",
            "modelId": "{{EnsaioDeIa.ModeloDoEnsaio}}",
            "instructions": "Você é a assistente do laboratório de teste.",
            "handoffNotice": "Vou chamar uma pessoa da nossa equipe. Um instante!",
            "faq": [ { "question": "Qual o horário de atendimento?", "answer": "De segunda a sexta, das 7h às 17h.", "autoReply": true } ],
            "apiKeyEnv": "IA_CHAVE"
          },
          "whatsApp": { "mode": "simulated" },
          "attendants": [ { "email": "ana@{{slug}}.local", "displayName": "Ana Atendente", "passwordEnv": "ATD_SENHA" } ]
        }
        """);

    private async Task<EnsaioDeWhatsApp> IniciarAsync(string prefixo)
    {
        // O Worker precisa da chave mestra do hash das lacunas (sem ela o registro fica desligado).
        var e = await EnsaioDeWhatsApp.IniciarAsync(banco, prefixo, new Dictionary<string, string?> { ["Agents:Options:KnowledgeGapHashKey"] = PostgresFixture.ChaveDeLacunasDeTeste });
        await e.Ia.CadastrarChaveAsync();
        return e;
    }

    private static async Task<string> LoginAsync(EnsaioDeWhatsApp e, string tenant, string email, string senha)
    {
        using var resposta = await e.Ia.Api.Client.PostAsJsonAsync("/auth/login", new { tenant, email, password = senha });
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        return (await resposta.Content.ReadFromJsonAsync<LoginDto>())!.Token;
    }

    private static async Task<Empresa> ProvisionarAsync(EnsaioDeWhatsApp e)
    {
        var slug = NovoSlug();
        var relatorio = await new TenantProvisioner(e.Ia.Api.Services, new Segredos()).ProvisionAsync(Especificacao(slug));
        Assert.True(relatorio.Succeeded, string.Join(" | ", relatorio.Steps.Select(s => $"{s.Name}: {s.Outcome} {s.Detail}")));
        var (waba, numero) = TenantProvisioner.SimulatedIdentifiers(slug);
        var webhook = Guid.Parse(relatorio.WebhookPath!["/webhooks/whatsapp/".Length..]);
        return new Empresa(
            slug, relatorio, waba, numero, webhook,
            await LoginAsync(e, slug, $"admin@{slug}.local", SenhaDoAdministrador),
            await LoginAsync(e, slug, $"ana@{slug}.local", SenhaDaAtendente));
    }

    private async Task PerguntarAsync(EnsaioDeWhatsApp e, Empresa empresa, string cliente, string texto)
    {
        saida.WriteLine($"[cliente {cliente[^4..]}] {texto}");
        var corpo = EnsaioDeWhatsApp.MontarPayloadDeTexto(empresa.Waba, empresa.Numero, $"wamid.{Guid.NewGuid():N}", cliente, "Cliente", texto);
        Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(corpo, EnsaioDeWhatsApp.Assinar(corpo, TenantProvisioner.SimulatedAppSecret), empresa.Webhook));
    }


    private static Task<JsonNode> LerAsync(EnsaioDeWhatsApp e, string token, string caminho) => e.Ia.Api.LerAsync<JsonNode>(token, caminho);

    private static async Task<(HttpStatusCode Status, JsonNode? Corpo)> ChamarAsync(EnsaioDeWhatsApp e, string? token, HttpMethod metodo, string caminho, object? corpo = null)
    {
        using var resposta = await e.Ia.Api.EnviarAsync(token, metodo, caminho, corpo);
        var texto = await resposta.Content.ReadAsStringAsync();
        return (resposta.StatusCode, string.IsNullOrWhiteSpace(texto) ? null : JsonNode.Parse(texto));
    }

    private static async Task<JsonNode> EsperarLacunasAsync(EnsaioDeWhatsApp e, Empresa empresa, int total, string query = "status=Open")
    {
        JsonNode? ultima = null;
        await e.EsperarAsync(
            async () =>
            {
                ultima = await LerAsync(e, empresa.Admin, $"/agents/knowledge-gaps?{query}");
                return ultima["total"]!.GetValue<int>() >= total;
            },
            $"deveria haver {total} lacuna(s) do FAQ");
        return ultima!;
    }

    private static string IdDaLacuna(JsonNode lista, int indice = 0) => lista["items"]![indice]!["id"]!.GetValue<string>();

    private static async Task AtendenteAssumeEResponderAsync(EnsaioDeWhatsApp e, Empresa empresa, string texto)
    {
        string? itemId = null;
        await e.EsperarAsync(
            async () =>
            {
                itemId = (await LerAsync(e, empresa.Atendente, "/contact-center/items?status=Waiting&take=20")).AsArray().FirstOrDefault()?["id"]?.GetValue<string>();
                return itemId is not null;
            },
            "a conversa deveria esperar o atendente");
        // Assumir uma conversa no instante em que ela ainda está entrando na fila é recusado com conflito (OperationInProgress): a interface espera, e o teste também.
        await e.EsperarAsync(
            async () =>
            {
                var status = await e.Ia.Api.StatusAsync(empresa.Atendente, HttpMethod.Post, $"/contact-center/items/{itemId}/claim", new { requestId = Guid.NewGuid() });
                Assert.True(status is HttpStatusCode.OK or HttpStatusCode.Conflict, $"assumir respondeu {(int)status}");
                return status == HttpStatusCode.OK;
            },
            "o atendente deveria conseguir assumir a conversa");
        Assert.Equal(HttpStatusCode.OK, await e.Ia.Api.StatusAsync(empresa.Atendente, HttpMethod.Post, $"/contact-center/items/{itemId}/reply", new { requestId = Guid.NewGuid(), text = texto }));
    }

    private static void RoteiroDaIa(EnsaioDeWhatsApp e, bool primeiraTransfere)
    {
        var chamadas = 0;
        e.Ia.OpenAi.Roteiros.Definir(
            EnsaioDeIa.ChaveCanario,
            (_, _) =>
            {
                var n = Interlocked.Increment(ref chamadas);
                return Task.FromResult(ServidorOpenAiSimulado.Sucesso(
                    primeiraTransfere && n == 1 ? CenarioDeAgentes.Transferir("insufficient_information") : CenarioDeAgentes.Responder(RespostaDaIA), 1200, 0, 100, $"req_con_{n}"));
            });
    }

    private int RequisicoesOpenAi(EnsaioDeWhatsApp e) => e.Ia.OpenAi.Roteiros.DaCredencial(EnsaioDeIa.ChaveCanario).Count;

    // O fluxo completo

    [Fact]
    public async Task Fluxo_completo_pergunta_sem_FAQ_lacuna_resposta_humana_revisao_rascunho_publicacao_e_a_proxima_pergunta_sem_chamar_a_IA()
    {

        await using var e = await IniciarAsync("confluxo");
        RoteiroDaIa(e, primeiraTransfere: true);
        var empresa = await ProvisionarAsync(e);

        // 1. O cliente pergunta algo que o FAQ não tem: a IA não sabe e transfere (uma requisição à OpenAI).
        await PerguntarAsync(e, empresa, "5511999990901", Pergunta);
        await e.EsperarAsync(async () => e.Graph.Capturas.Count == 1, "o aviso de transferência deveria sair pelo WhatsApp");
        Assert.Equal(1, RequisicoesOpenAi(e));

        // 2. O atendente assume e responde ao cliente.
        await AtendenteAssumeEResponderAsync(e, empresa, RespostaDoAtendente);
        await e.EsperarAsync(async () => e.Graph.Capturas.Count == 2, "a resposta do atendente deveria sair pelo WhatsApp");
        saida.WriteLine($"[atendente] {RespostaDoAtendente}");

        // 3. A lacuna foi registrada (sem o texto do cliente): o administrador a vê na lista, e só ele.
        var lista = await EsperarLacunasAsync(e, empresa, 1);
        var item = lista["items"]![0]!;
        Assert.Equal(("NoMatch", "Open", 1), (item["kind"]!.GetValue<string>(), item["status"]!.GetValue<string>(), item["occurrences"]!.GetValue<int>()));
        Assert.DoesNotContain("documento", item.ToJsonString()); // a lista nunca traz texto de cliente
        var id = IdDaLacuna(lista);
        Assert.Equal(HttpStatusCode.Forbidden, (await ChamarAsync(e, empresa.Atendente, HttpMethod.Get, "/agents/knowledge-gaps")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ChamarAsync(e, null, HttpMethod.Get, "/agents/knowledge-gaps")).Status);

        // O diagnóstico operacional mostra que há o que revisar (só a contagem, nada do cliente).
        var statusAntes = new StringWriter();
        await StatusCommand.ExecuteAsync(e.Ia.Api.Services, new StatusOptions(empresa.Slug, null, false, false), statusAntes);
        Assert.Contains("1 lacuna(s) do FAQ aberta(s) esperando revisão do administrador", statusAntes.ToString());

        // 4. A revisão lê a pergunta original e a resposta do atendente como SUGESTÃO (com a autorização do administrador).
        var detalhe = await LerAsync(e, empresa.Admin, $"/agents/knowledge-gaps/{id}");
        Assert.Equal("Available", detalhe["source"]!["state"]!.GetValue<string>());
        Assert.Equal(Pergunta, detalhe["source"]!["question"]!.GetValue<string>());
        Assert.Equal(RespostaDoAtendente, detalhe["source"]!["humanReply"]!["text"]!.GetValue<string>());
        Assert.Equal(0, detalhe["source"]!["humanReply"]!["customerMessagesBetween"]!.GetValue<int>());
        saida.WriteLine($"[revisão] pergunta lida: \"{Pergunta}\" | sugestão do atendente: \"{RespostaDoAtendente}\"");

        // 5. O administrador EDITA a resposta, acrescenta um alias, liga a resposta direta DE PROPÓSITO e envia ao rascunho.
        var promocao = await ChamarAsync(e, empresa.Admin, HttpMethod.Post, $"/agents/knowledge-gaps/{id}/promote",
            new { question = Pergunta, aliases = new[] { "preciso de documento para coletar" }, answer = RespostaRevisada, autoReply = true });
        Assert.Equal(HttpStatusCode.OK, promocao.Status);
        Assert.Equal((2, 2, "Promoted"), (promocao.Corpo!["draftVersion"]!.GetValue<int>(), promocao.Corpo["faqEntries"]!.GetValue<int>(), promocao.Corpo["gap"]!["status"]!.GetValue<string>()));
        saida.WriteLine($"[revisão] enviada ao rascunho v2 com autoReply=true e resposta editada: \"{RespostaRevisada}\"");

        // Nada foi publicado: a versão publicada continua a 1, só com a entrada do horário.
        var publicada = await LerAsync(e, empresa.Admin, "/agents/definition/published");
        Assert.Equal((1, 1), (publicada["number"]!.GetValue<int>(), publicada["content"]!["faq"]!.AsArray().Count));
        var rascunho = await LerAsync(e, empresa.Admin, "/agents/definition/draft");
        Assert.Equal((2, "Draft", 2), (rascunho["number"]!.GetValue<int>(), rascunho["status"]!.GetValue<string>(), rascunho["content"]!["faq"]!.AsArray().Count));

        // 6. Antes de publicar, a mesma pergunta de outro cliente continua indo à IA (o rascunho nunca é usado).
        await PerguntarAsync(e, empresa, "5511999990902", Pergunta);
        await e.EsperarAsync(async () => e.Graph.Capturas.Count == 3, "a IA deveria responder enquanto o rascunho não está publicado");
        Assert.Equal(RespostaDaIA, e.Graph.Capturas[2].Texto);
        Assert.Equal(2, RequisicoesOpenAi(e));
        saida.WriteLine($"[resposta ao cliente] {e.Graph.Capturas[2].Texto}  (OpenAI: {RequisicoesOpenAi(e)} requisições, o rascunho não vale)");

        // 7. A publicação é uma ação explícita e auditada do administrador.
        var publicacao = await ChamarAsync(e, empresa.Admin, HttpMethod.Post, "/agents/definition/publish");
        Assert.Equal(HttpStatusCode.OK, publicacao.Status);
        Assert.Equal((2, "Published"), (publicacao.Corpo!["number"]!.GetValue<int>(), publicacao.Corpo["status"]!.GetValue<string>()));
        saida.WriteLine("[administrador] rascunho publicado: versão 2");

        // 8. A próxima pergunta (e o alias) é respondida com o texto REVISADO e ZERO novas requisições à OpenAI.
        var antes = RequisicoesOpenAi(e);
        await PerguntarAsync(e, empresa, "5511999990903", "preciso levar documento para a coleta");
        await e.EsperarAsync(async () => e.Graph.Capturas.Count == 4, "a resposta direta deveria sair pelo WhatsApp");
        await PerguntarAsync(e, empresa, "5511999990904", "Preciso de documento para coletar???");
        await e.EsperarAsync(async () => e.Graph.Capturas.Count == 5, "a resposta direta pelo alias deveria sair pelo WhatsApp");
        Assert.Equal(RespostaRevisada, e.Graph.Capturas[3].Texto);
        Assert.Equal(RespostaRevisada, e.Graph.Capturas[4].Texto);
        Assert.Equal(antes, RequisicoesOpenAi(e)); // nenhuma requisição nova
        saida.WriteLine($"[resposta ao cliente] {e.Graph.Capturas[3].Texto}  (OpenAI: {RequisicoesOpenAi(e)} requisições no total, nenhuma nova)");

        // 9. O diagnóstico operacional conta as chamadas evitadas.
        var relatorio = new StringWriter();
        await StatusCommand.ExecuteAsync(e.Ia.Api.Services, new StatusOptions(empresa.Slug, null, false, false), relatorio);
        Assert.Contains("2 turno(s) respondido(s) direto pelo FAQ, SEM chamar o modelo", relatorio.ToString());
    }

    // Isolamento e permissões pela API

    [Fact]
    public async Task Cada_empresa_so_ve_as_proprias_lacunas_o_atendente_nao_tem_acesso_e_sem_token_e_401_em_toda_rota()
    {
        await using var e = await IniciarAsync("conperm");
        RoteiroDaIa(e, primeiraTransfere: false);
        var a = await ProvisionarAsync(e);
        var b = await ProvisionarAsync(e);

        await PerguntarAsync(e, a, "5511999990911", Pergunta);
        var listaA = await EsperarLacunasAsync(e, a, 1);
        var idDeA = IdDaLacuna(listaA);

        // A outra empresa não lista, não lê e não decide a lacuna de A: é inexistente (404), e nada muda.
        Assert.Equal(0, (await LerAsync(e, b.Admin, "/agents/knowledge-gaps"))["total"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.NotFound, (await ChamarAsync(e, b.Admin, HttpMethod.Get, $"/agents/knowledge-gaps/{idDeA}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await ChamarAsync(e, b.Admin, HttpMethod.Post, $"/agents/knowledge-gaps/{idDeA}/dismiss")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await ChamarAsync(e, b.Admin, HttpMethod.Post, $"/agents/knowledge-gaps/{idDeA}/promote", new { question = "P?", answer = "R" })).Status);
        Assert.Equal("Open", (await LerAsync(e, a.Admin, $"/agents/knowledge-gaps/{idDeA}"))["gap"]!["status"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, (await ChamarAsync(e, a.Admin, HttpMethod.Get, "/agents/definition/draft")).Status); // nada vazou para o rascunho de A

        // O atendente de A (sem a permissão de configurar a empresa) recebe 403 em tudo, inclusive no rascunho e na publicação.
        foreach (var (metodo, caminho) in new[]
        {
            (HttpMethod.Get, "/agents/knowledge-gaps"),
            (HttpMethod.Get, $"/agents/knowledge-gaps/{idDeA}"),
            (HttpMethod.Post, $"/agents/knowledge-gaps/{idDeA}/dismiss"),
            (HttpMethod.Post, $"/agents/knowledge-gaps/{idDeA}/promote"),
            (HttpMethod.Get, "/agents/definition/draft"),
            (HttpMethod.Get, "/agents/definition/published"),
            (HttpMethod.Post, "/agents/definition/publish"),
        })
        {
            object? corpo = metodo == HttpMethod.Post && caminho.EndsWith("/promote", StringComparison.Ordinal) ? new { question = "P?", answer = "R" } : null;
            Assert.Equal(HttpStatusCode.Forbidden, (await ChamarAsync(e, a.Atendente, metodo, caminho, corpo)).Status);
            Assert.Equal(HttpStatusCode.Unauthorized, (await ChamarAsync(e, null, metodo, caminho, corpo)).Status);
        }

        Assert.Equal("Open", (await LerAsync(e, a.Admin, $"/agents/knowledge-gaps/{idDeA}"))["gap"]!["status"]!.GetValue<string>());
    }

    // As regras pela API

    [Fact]
    public async Task Regras_da_API_validacao_conflito_descarte_e_nada_e_publicado_por_revisar()
    {
        await using var e = await IniciarAsync("conregras");
        RoteiroDaIa(e, primeiraTransfere: false);
        var empresa = await ProvisionarAsync(e);
        await PerguntarAsync(e, empresa, "5511999990921", Pergunta);
        await PerguntarAsync(e, empresa, "5511999990922", "Vocês aceitam cartão de crédito?");
        var lista = await EsperarLacunasAsync(e, empresa, 2);
        var a = IdDaLacuna(lista, 0);
        var b = IdDaLacuna(lista, 1);

        // Validação: pergunta ou resposta em branco, alias demais, tamanho, paginação inválida.
        Assert.Equal(HttpStatusCode.BadRequest, (await ChamarAsync(e, empresa.Admin, HttpMethod.Post, $"/agents/knowledge-gaps/{a}/promote", new { question = "   ", answer = "R" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await ChamarAsync(e, empresa.Admin, HttpMethod.Post, $"/agents/knowledge-gaps/{a}/promote", new { question = "P?", answer = new string('r', 1501) })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await ChamarAsync(e, empresa.Admin, HttpMethod.Post, $"/agents/knowledge-gaps/{a}/promote", new { question = "P?", answer = "R", aliases = Enumerable.Range(0, 11).Select(i => $"alias {i}").ToArray() })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await ChamarAsync(e, empresa.Admin, HttpMethod.Get, "/agents/knowledge-gaps?take=0")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await ChamarAsync(e, empresa.Admin, HttpMethod.Get, $"/agents/knowledge-gaps/{Guid.NewGuid()}")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await ChamarAsync(e, empresa.Admin, HttpMethod.Post, "/agents/definition/publish")).Status); // não há rascunho para publicar

        // Conflito: a pergunta já existe no FAQ.
        var conflito = await ChamarAsync(e, empresa.Admin, HttpMethod.Post, $"/agents/knowledge-gaps/{a}/promote", new { question = "QUAL O HORARIO DE ATENDIMENTO", answer = "R" });
        Assert.Equal(HttpStatusCode.Conflict, conflito.Status);
        Assert.Equal("faq-conflict", conflito.Corpo!["code"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, (await ChamarAsync(e, empresa.Admin, HttpMethod.Get, "/agents/definition/draft")).Status); // nada foi gravado

        // Descartar é idempotente; depois de descartada, não vai ao rascunho; depois de enviada, não se descarta.
        Assert.Equal(HttpStatusCode.OK, (await ChamarAsync(e, empresa.Admin, HttpMethod.Post, $"/agents/knowledge-gaps/{b}/dismiss")).Status);
        var descartada = await ChamarAsync(e, empresa.Admin, HttpMethod.Post, $"/agents/knowledge-gaps/{b}/dismiss");
        Assert.Equal((HttpStatusCode.OK, "Dismissed"), (descartada.Status, descartada.Corpo!["status"]!.GetValue<string>()));
        var naoAberta = await ChamarAsync(e, empresa.Admin, HttpMethod.Post, $"/agents/knowledge-gaps/{b}/promote", new { question = "Aceitam cartão?", answer = "Aceitamos." });
        Assert.Equal((HttpStatusCode.Conflict, "gap-not-open"), (naoAberta.Status, naoAberta.Corpo!["code"]!.GetValue<string>()));

        // Enviar ao rascunho: autoReply desligado por padrão (o corpo nem diz), e a versão publicada não muda.
        var enviada = await ChamarAsync(e, empresa.Admin, HttpMethod.Post, $"/agents/knowledge-gaps/{a}/promote", new { question = Pergunta, answer = RespostaRevisada });
        Assert.Equal(HttpStatusCode.OK, enviada.Status);
        var rascunho = await LerAsync(e, empresa.Admin, "/agents/definition/draft");
        Assert.False(rascunho["content"]!["faq"]![1]!["autoReply"]!.GetValue<bool>());
        Assert.Equal(1, (await LerAsync(e, empresa.Admin, "/agents/definition/published"))["number"]!.GetValue<int>());
        var jaEnviada = await ChamarAsync(e, empresa.Admin, HttpMethod.Post, $"/agents/knowledge-gaps/{a}/dismiss");
        Assert.Equal((HttpStatusCode.Conflict, "gap-not-open"), (jaEnviada.Status, jaEnviada.Corpo!["code"]!.GetValue<string>()));

        // Mesmo publicado, sem autoReply a pergunta ainda vai à IA (opt-in por entrada), e vira lacuna do tipo "resposta direta desligada".
        Assert.Equal(HttpStatusCode.OK, (await ChamarAsync(e, empresa.Admin, HttpMethod.Post, "/agents/definition/publish")).Status);
        var antes = RequisicoesOpenAi(e);
        await PerguntarAsync(e, empresa, "5511999990923", Pergunta);
        await e.EsperarAsync(async () => RequisicoesOpenAi(e) == antes + 1, "sem autoReply a pergunta deveria ir à IA");
        Assert.Equal(antes + 1, RequisicoesOpenAi(e));
    }

    [Fact]
    public async Task Tres_administradores_enviando_a_mesma_lacuna_pela_API_ao_mesmo_tempo_so_um_recebe_200_e_o_rascunho_ganha_uma_entrada()
    {
        await using var e = await IniciarAsync("concorr");
        RoteiroDaIa(e, primeiraTransfere: false);
        var empresa = await ProvisionarAsync(e);
        await PerguntarAsync(e, empresa, "5511999990931", Pergunta);
        var id = IdDaLacuna(await EsperarLacunasAsync(e, empresa, 1));

        var respostas = await Task.WhenAll(Enumerable.Range(0, 3).Select(i =>
            ChamarAsync(e, empresa.Admin, HttpMethod.Post, $"/agents/knowledge-gaps/{id}/promote", new { question = $"Pergunta do administrador {i}?", answer = "Resposta." })));

        Assert.Equal(1, respostas.Count(r => r.Status == HttpStatusCode.OK));
        Assert.All(respostas.Where(r => r.Status != HttpStatusCode.OK), r =>
        {
            Assert.Equal(HttpStatusCode.Conflict, r.Status);
            Assert.Contains(r.Corpo!["code"]!.GetValue<string>(), new[] { "gap-changed", "gap-not-open", "definition-changed" });
        });
        Assert.Equal(2, (await LerAsync(e, empresa.Admin, "/agents/definition/draft"))["content"]!["faq"]!.AsArray().Count);
    }

    [Fact]
    public async Task A_mesma_pergunta_de_varios_clientes_vira_uma_lacuna_com_a_contagem_exata_e_a_pergunta_respondida_direto_nunca_vira_lacuna()
    {
        await using var e = await IniciarAsync("concont");
        RoteiroDaIa(e, primeiraTransfere: false);
        var empresa = await ProvisionarAsync(e);

        for (var i = 0; i < 3; i++)
        {
            await PerguntarAsync(e, empresa, $"559999999094{i}", i == 1 ? "PRECISO LEVAR DOCUMENTO PARA A COLETA!!!" : Pergunta);
        }

        await PerguntarAsync(e, empresa, "5599999990949", "Qual o horário de atendimento?"); // do FAQ, com autoReply: resposta direta, nunca lacuna
        await e.EsperarAsync(async () => e.Graph.Capturas.Count == 4, "as quatro respostas deveriam sair");

        var lista = await EsperarLacunasAsync(e, empresa, 1);
        Assert.Equal(1, lista["total"]!.GetValue<int>()); // a pergunta do FAQ não abriu lacuna
        await e.EsperarAsync(async () => (await LerAsync(e, empresa.Admin, $"/agents/knowledge-gaps/{IdDaLacuna(lista)}"))["gap"]!["occurrences"]!.GetValue<int>() == 3, "as três ocorrências deveriam contar");
    }
}
