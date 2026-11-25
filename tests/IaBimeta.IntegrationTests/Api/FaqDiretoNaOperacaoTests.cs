using System.Net;
using IaBimeta.Agents.Domain;
using IaBimeta.Api.Operations;
using IaBimeta.Api.Provisioning;
using IaBimeta.IntegrationTests.Agents;
using Npgsql;
using Xunit.Abstractions;

namespace IaBimeta.IntegrationTests.Api;

/// <summary>
/// A resposta direta do FAQ (ADR 0029) na OPERAÇÃO, com Api e Worker reais, PostgreSQL real e a Meta e a OpenAI simuladas por HTTP: o arquivo da empresa (<c>aliases</c>, <c>autoReply</c>, <c>faqLocale</c>), o
/// reprovisionamento que só republica quando algo mudou (inclusive os campos novos), e a demonstração ponta a ponta: uma pergunta do FAQ respondida pelo WhatsApp com ZERO requisições ao servidor da OpenAI, e outra
/// encaminhada à IA simulada, com o <c>status</c> mostrando o que foi medido e o que foi estimado.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class FaqDiretoNaOperacaoTests(PostgresFixture banco, ITestOutputHelper saida)
{
    private const string Horario = "Qual o horário de atendimento?";
    private const string RespostaDoHorario = "De segunda a sexta, das 7h às 17h.";

    private sealed class Segredos(IReadOnlyDictionary<string, string> valores) : IProvisioningSecrets
    {
        public string? Get(string name) => valores.GetValueOrDefault(name);
    }

    private static readonly IReadOnlyDictionary<string, string> SegredosDoEnsaio = new Dictionary<string, string>
    {
        ["ADM_SENHA"] = "Senha-Do-Admin-2026!",
        ["ATD_SENHA"] = "Senha-Da-Atendente-2026!",
        ["IA_CHAVE"] = EnsaioDeIa.ChaveCanario,
    };

    private static string NovoSlug() => $"faq-{Guid.NewGuid().ToString("N")[..10]}";

    private static string Arquivo(string slug, string faq, string? idioma = null) => $$"""
        {
          "tenant": { "slug": "{{slug}}", "name": "Laboratório {{slug}}", "unlimitedUsers": true },
          "administrator": { "email": "admin@{{slug}}.local", "displayName": "Administradora", "passwordEnv": "ADM_SENHA" },
          "organization": { "name": "Laboratório {{slug}}", "unitName": "Matriz" },
          "queue": { "name": "Atendimento humano" },
          "ai": {
            "providerId": "openai",
            "modelId": "{{EnsaioDeIa.ModeloDoEnsaio}}",
            "instructions": "Você é a assistente do laboratório de teste.",
            "handoffNotice": "Vou chamar uma pessoa da nossa equipe. Um instante!",
            {{(idioma is null ? string.Empty : $"\"faqLocale\": \"{idioma}\",")}}
            "faq": {{faq}},
            "apiKeyEnv": "IA_CHAVE"
          },
          "whatsApp": { "mode": "simulated" },
          "attendants": [ { "email": "ana@{{slug}}.local", "displayName": "Ana Atendente", "passwordEnv": "ATD_SENHA" } ]
        }
        """;

    private const string FaqComRespostaDireta =
        "[ { \"question\": \"Qual o horário de atendimento?\", \"answer\": \"De segunda a sexta, das 7h às 17h.\", \"aliases\": [ \"que horas vocês abrem\" ], \"autoReply\": true }," +
        "  { \"question\": \"Preciso de jejum para o hemograma?\", \"answer\": \"Em geral não; confirme com o atendimento.\" } ]";

    private static ProvisioningSpec Especificacao(string slug, string faq, string? idioma = null) => ProvisioningSpec.Parse(Arquivo(slug, faq, idioma));

    private async Task<EnsaioDeWhatsApp> IniciarAsync(string prefixo)
    {
        var e = await EnsaioDeWhatsApp.IniciarAsync(banco, prefixo);
        await e.Ia.CadastrarChaveAsync();
        return e;
    }

    private static Task<ProvisioningReport> ProvisionarAsync(EnsaioDeWhatsApp e, ProvisioningSpec spec) =>
        new TenantProvisioner(e.Ia.Api.Services, new Segredos(SegredosDoEnsaio)).ProvisionAsync(spec);

    private static async Task<int> ExecutarAsync(string sql, params (string Nome, object? Valor)[] parametros)
    {
        await using var conexao = new NpgsqlConnection(TestDatabase.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = new NpgsqlCommand(sql, conexao);
        foreach (var (nome, valor) in parametros)
        {
            comando.Parameters.AddWithValue(nome, valor ?? DBNull.Value);
        }

        return await comando.ExecuteNonQueryAsync();
    }

    private static string DoPasso(ProvisioningReport r, string nome) => r.Steps.Single(s => s.Name == nome).Outcome.ToString();

    // O arquivo da empresa

    [Fact]
    public void O_arquivo_da_empresa_le_aliases_resposta_direta_e_idioma_e_o_padrao_e_sem_resposta_direta()
    {
        var spec = Especificacao("faq-leitura", FaqComRespostaDireta, "en-US");

        Assert.Equal("en-US", spec.Ai.FaqLocale);
        var primeira = spec.Ai.Faq[0];
        Assert.True(primeira.AutoReply);
        Assert.Equal(new[] { "que horas vocês abrem" }, primeira.Aliases!.ToArray());
        var segunda = spec.Ai.Faq[1];
        Assert.False(segunda.AutoReply); // desligada por padrão
        Assert.Null(segunda.Aliases);
        Assert.Null(Especificacao("faq-padrao", FaqComRespostaDireta).Ai.FaqLocale);
    }

    [Fact]
    public void O_arquivo_da_empresa_recusa_alias_demais_alias_em_branco_idioma_mal_formado_e_campo_desconhecido()
    {
        var onze = string.Join(",", Enumerable.Range(0, 11).Select(i => $"\"alias {i}\""));
        var demais = Assert.Throws<ProvisioningSpecException>(() => Especificacao("faq-a", $"[ {{ \"question\": \"P?\", \"answer\": \"R\", \"aliases\": [ {onze} ], \"autoReply\": true }} ]"));
        Assert.Contains("ai.faq[0].aliases aceita no máximo 10", demais.Message);

        var vazio = Assert.Throws<ProvisioningSpecException>(() => Especificacao("faq-b", "[ { \"question\": \"P?\", \"answer\": \"R\", \"aliases\": [ \"ok\", \"  \" ] } ]"));
        Assert.Contains("ai.faq[0].aliases[1]", vazio.Message);

        var idioma = Assert.Throws<ProvisioningSpecException>(() => Especificacao("faq-c", FaqComRespostaDireta, "portugues"));
        Assert.Contains("ai.faqLocale", idioma.Message);

        // Erro de digitação não vira configuração ignorada em silêncio: ligaria a resposta direta de menos (ou de mais) sem ninguém ver.
        var erroDeDigitacao = Assert.Throws<ProvisioningSpecException>(() => Especificacao("faq-d", "[ { \"question\": \"P?\", \"answer\": \"R\", \"autoReplyy\": true } ]"));
        Assert.Contains("autoReplyy", erroDeDigitacao.Message);
    }

    [Fact]
    public void O_arquivo_de_exemplo_entregue_liga_a_resposta_direta_em_uma_entrada_e_deixa_a_outra_so_para_a_IA()
    {
        var pasta = new DirectoryInfo(AppContext.BaseDirectory);
        while (pasta is not null && !File.Exists(Path.Combine(pasta.FullName, "tools", "ops", "empresa-exemplo.json")))
        {
            pasta = pasta.Parent;
        }

        var spec = ProvisioningSpec.Load(Path.Combine(pasta!.FullName, "tools", "ops", "empresa-exemplo.json"));

        Assert.Equal("pt-BR", spec.Ai.FaqLocale);
        Assert.True(spec.Ai.Faq[0].AutoReply);
        Assert.Equal(2, spec.Ai.Faq[0].Aliases!.Count);
        Assert.False(spec.Ai.Faq[1].AutoReply);
    }

    // Reprovisionar: só republica quando algo mudou, inclusive os campos novos

    [Fact]
    public async Task Reprovisionar_republica_so_quando_muda_alias_resposta_direta_ou_idioma_e_a_definicao_antiga_continua_igual()
    {
        await using var e = await IniciarAsync("faqprov");
        var slug = NovoSlug();
        var semNovosCampos = "[ { \"question\": \"Qual o horário?\", \"answer\": \"Das 7h às 17h.\" } ]";

        var primeira = await ProvisionarAsync(e, Especificacao(slug, semNovosCampos));
        Assert.True(primeira.Succeeded, string.Join(" | ", primeira.Steps.Select(s => $"{s.Name}: {s.Outcome} {s.Detail}")));
        Assert.Equal("Created", DoPasso(primeira, "Agente de IA"));
        Assert.Contains("0 com resposta direta", primeira.Steps.Single(s => s.Name == "Agente de IA").Detail);

        // A definição como o banco a tinha ANTES do ADR 0029 (sem campos novos e sem idioma): é a mesma que um arquivo sem os campos novos descreve.
        var tenant = primeira.TenantId!.Value;
        await ExecutarAsync(
            "UPDATE agents.agent_definition_versions SET faq = @faq::jsonb, faq_locale = NULL WHERE tenant_id = @t AND status = 2",
            ("faq", "[{\"question\":\"Qual o horário?\",\"answer\":\"Das 7h às 17h.\"}]"), ("t", tenant));
        Assert.Equal("Exists", DoPasso(await ProvisionarAsync(e, Especificacao(slug, semNovosCampos)), "Agente de IA"));
        Assert.Equal("Exists", DoPasso(await ProvisionarAsync(e, Especificacao(slug, semNovosCampos, "pt-BR")), "Agente de IA")); // pt-BR explícito é o padrão

        // Ligar a resposta direta (e cadastrar o alias) é uma mudança: publica uma versão nova.
        var comDireta = "[ { \"question\": \"Qual o horário?\", \"answer\": \"Das 7h às 17h.\", \"aliases\": [ \"que horas abrem\" ], \"autoReply\": true } ]";
        var ligada = await ProvisionarAsync(e, Especificacao(slug, comDireta));
        Assert.Equal("Updated", DoPasso(ligada, "Agente de IA"));
        Assert.Contains("1 com resposta direta", ligada.Steps.Single(s => s.Name == "Agente de IA").Detail);
        Assert.Equal("Exists", DoPasso(await ProvisionarAsync(e, Especificacao(slug, comDireta)), "Agente de IA"));

        // Um alias a mais, o alias trocado, a resposta direta desligada e o idioma: cada um é uma mudança.
        var outroAlias = "[ { \"question\": \"Qual o horário?\", \"answer\": \"Das 7h às 17h.\", \"aliases\": [ \"que horas abrem\", \"horário de funcionamento\" ], \"autoReply\": true } ]";
        Assert.Equal("Updated", DoPasso(await ProvisionarAsync(e, Especificacao(slug, outroAlias)), "Agente de IA"));
        var semDireta = "[ { \"question\": \"Qual o horário?\", \"answer\": \"Das 7h às 17h.\", \"aliases\": [ \"que horas abrem\", \"horário de funcionamento\" ] } ]";
        Assert.Equal("Updated", DoPasso(await ProvisionarAsync(e, Especificacao(slug, semDireta)), "Agente de IA"));
        Assert.Equal("Updated", DoPasso(await ProvisionarAsync(e, Especificacao(slug, semDireta, "en-US")), "Agente de IA"));
        Assert.Equal("Exists", DoPasso(await ProvisionarAsync(e, Especificacao(slug, semDireta, "en-US")), "Agente de IA"));

        // O banco guarda o último arquivo.
        await using var conexao = new NpgsqlConnection(TestDatabase.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = new NpgsqlCommand("SELECT faq_locale, faq::text FROM agents.agent_definition_versions WHERE tenant_id = @t AND status = 2", conexao);
        comando.Parameters.AddWithValue("t", tenant);
        await using var leitor = await comando.ExecuteReaderAsync();
        Assert.True(await leitor.ReadAsync());
        Assert.Equal("en-US", leitor.GetString(0));
        Assert.Contains("\"autoReply\": false", leitor.GetString(1));
    }

    // A demonstração ponta a ponta: WhatsApp simulado, Worker real, OpenAI simulada por HTTP

    [Fact]
    public async Task Pelo_WhatsApp_a_pergunta_do_FAQ_e_respondida_com_zero_requisicoes_a_OpenAI_e_a_outra_vai_a_IA_e_o_status_separa_o_medido_do_estimado()
    {
        await using var e = await IniciarAsync("faqwa");
        e.Ia.OpenAi.Roteiros.Definir(
            EnsaioDeIa.ChaveCanario,
            (_, _) => Task.FromResult(ServidorOpenAiSimulado.Sucesso(CenarioDeAgentes.Responder("Resposta gerada pela IA simulada."), 1200, 0, 100, "req_faq")));
        var slug = NovoSlug();
        var relatorio = await ProvisionarAsync(e, Especificacao(slug, FaqComRespostaDireta));
        Assert.True(relatorio.Succeeded);
        var (waba, numero) = TenantProvisioner.SimulatedIdentifiers(slug);
        var webhook = Guid.Parse(relatorio.WebhookPath!["/webhooks/whatsapp/".Length..]);
        const string cliente = "5511999990701";

        int RequisicoesOpenAi() => e.Ia.OpenAi.Roteiros.DaCredencial(EnsaioDeIa.ChaveCanario).Count;

        async Task PerguntarAsync(string id, string texto)
        {
            saida.WriteLine($"[cliente no WhatsApp] {texto}");
            var corpo = EnsaioDeWhatsApp.MontarPayloadDeTexto(waba, numero, id, cliente, "Cliente do FAQ", texto);
            Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(corpo, EnsaioDeWhatsApp.Assinar(corpo, TenantProvisioner.SimulatedAppSecret), webhook));
        }

        // 1. A pergunta do FAQ, com outra caixa e sem acento: respondida direto, sem tocar o servidor da OpenAI.
        await PerguntarAsync("wamid.FAQ0001", "QUAL O HORARIO DE ATENDIMENTO???");
        await e.EsperarAsync(async () => e.Graph.Capturas.Count == 1, "a resposta direta do FAQ deveria sair pelo WhatsApp");
        var direta = Assert.Single(e.Graph.Capturas);
        Assert.Equal((cliente, RespostaDoHorario), (direta.Para, direta.Texto));
        Assert.Equal(TenantProvisioner.SimulatedAccessToken, direta.Credencial);
        Assert.Empty(e.Ia.OpenAi.Roteiros.DaCredencial(EnsaioDeIa.ChaveCanario)); // ZERO requisições ao provedor de IA
        saida.WriteLine($"[resposta ao cliente] {direta.Texto}");
        saida.WriteLine($"[servidor OpenAI simulado] requisicoes recebidas ate aqui: {RequisicoesOpenAi()}");

        // 2. Outra pergunta: vai à IA simulada (uma requisição) e a resposta dela sai pelo WhatsApp.
        await PerguntarAsync("wamid.FAQ0002", "Preciso levar documento para a coleta?");
        await e.EsperarAsync(async () => e.Graph.Capturas.Count == 2, "a resposta da IA simulada deveria sair pelo WhatsApp");
        Assert.Equal((cliente, "Resposta gerada pela IA simulada."), (e.Graph.Capturas[1].Para, e.Graph.Capturas[1].Texto));
        Assert.Single(e.Ia.OpenAi.Roteiros.DaCredencial(EnsaioDeIa.ChaveCanario));
        saida.WriteLine($"[resposta ao cliente] {e.Graph.Capturas[1].Texto}");
        saida.WriteLine($"[servidor OpenAI simulado] requisicoes recebidas ate aqui: {RequisicoesOpenAi()}");

        // 3. Mesma pergunta do FAQ, pelo alias: mais um acerto, e continua UMA requisição à OpenAI no total.
        await PerguntarAsync("wamid.FAQ0003", "que horas vocês abrem");
        await e.EsperarAsync(async () => e.Graph.Capturas.Count == 3, "a resposta direta pelo alias deveria sair pelo WhatsApp");
        Assert.Equal(RespostaDoHorario, e.Graph.Capturas[2].Texto);
        Assert.Single(e.Ia.OpenAi.Roteiros.DaCredencial(EnsaioDeIa.ChaveCanario));
        saida.WriteLine($"[resposta ao cliente] {e.Graph.Capturas[2].Texto}");
        saida.WriteLine($"[servidor OpenAI simulado] requisicoes recebidas no total: {RequisicoesOpenAi()}");

        // O status da empresa: a contagem de chamadas evitadas é exata; os tokens e o custo, uma estimativa rotulada e com a base.
        var relatorioDoStatus = new StringWriter();
        var codigo = await StatusCommand.ExecuteAsync(e.Ia.Api.Services, new StatusOptions(slug, null, false, false), relatorioDoStatus);
        var texto = relatorioDoStatus.ToString();
        saida.WriteLine("[status --tenant " + slug + "]");
        foreach (var linha in texto.Split('\n').SkipWhile(l => !l.StartsWith("FAQ DIRETO", StringComparison.Ordinal)).TakeWhile(l => l.Trim().Length > 0))
        {
            saida.WriteLine(linha.TrimEnd());
        }

        Assert.Equal(0, codigo);
        Assert.Contains("FAQ DIRETO", texto);
        Assert.Contains($"(empresa {slug}) nos últimos 7 d: 2 turno(s) respondido(s) direto pelo FAQ, SEM chamar o modelo (chamadas evitadas; contagem exata); o modelo respondeu 1 turno(s); 66,7% dos turnos respondidos pela IA saíram do FAQ.", texto);
        Assert.Contains("ESTIMATIVA (não medida): ~2.600 tokens", texto); // 2 acertos x (1.200 + 100) tokens da única chamada confirmada do tenant
        Assert.Contains("(1 chamada(s) na janela)", texto);
        Assert.Contains("não é fatura", texto);

        // Outra empresa não vê nada desta.
        var outra = new StringWriter();
        await StatusCommand.ExecuteAsync(e.Ia.Api.Services, new StatusOptions(e.Ia.Slug, null, false, false), outra);
        saida.WriteLine("[status de OUTRA empresa] " + outra.ToString().Split('\n').First(l => l.Contains("turno(s) respondido(s) direto pelo FAQ", StringComparison.Ordinal)).Trim());
        Assert.DoesNotContain(slug, outra.ToString());
        Assert.Contains("nos últimos 7 d: 0 turno(s) respondido(s) direto pelo FAQ", outra.ToString()); // a outra empresa tem zero acertos: os números não se somam
        Assert.DoesNotContain("2 turno(s) respondido(s) direto", outra.ToString());

        // O que o provedor viu: uma única requisição, a da segunda pergunta. A conversa continua inteira: a resposta direta que o cliente recebeu faz parte do contexto dessa chamada (a IA sabe o que já foi dito).
        var requisicao = Assert.Single(e.Ia.OpenAi.Roteiros.DaCredencial(EnsaioDeIa.ChaveCanario));
        Assert.Contains("documento para a coleta", requisicao.Corpo);
        Assert.Contains("segunda a sexta", requisicao.Corpo);
    }

    [Fact]
    public async Task Desligar_a_funcao_pela_configuracao_manda_todo_turno_a_IA_sem_mexer_na_definicao()
    {
        await using var e = await EnsaioDeWhatsApp.IniciarAsync(banco, "faqoff", new Dictionary<string, string?> { ["Agents:Options:FaqDirectAnswerEnabled"] = "false" });
        await e.Ia.CadastrarChaveAsync();
        e.Ia.OpenAi.Roteiros.Definir(
            EnsaioDeIa.ChaveCanario,
            (_, _) => Task.FromResult(ServidorOpenAiSimulado.Sucesso(CenarioDeAgentes.Responder("Resposta gerada pela IA simulada."), 1200, 0, 100, "req_off")));
        var slug = NovoSlug();
        var relatorio = await ProvisionarAsync(e, Especificacao(slug, FaqComRespostaDireta));
        var (waba, numero) = TenantProvisioner.SimulatedIdentifiers(slug);
        var webhook = Guid.Parse(relatorio.WebhookPath!["/webhooks/whatsapp/".Length..]);

        var corpo = EnsaioDeWhatsApp.MontarPayloadDeTexto(waba, numero, "wamid.OFF0001", "5511999990702", "Cliente", Horario);
        Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(corpo, EnsaioDeWhatsApp.Assinar(corpo, TenantProvisioner.SimulatedAppSecret), webhook));
        await e.EsperarAsync(async () => e.Graph.Capturas.Count == 1, "a IA deveria responder, com a função desligada");

        Assert.Equal("Resposta gerada pela IA simulada.", Assert.Single(e.Graph.Capturas).Texto);
        Assert.Single(e.Ia.OpenAi.Roteiros.DaCredencial(EnsaioDeIa.ChaveCanario));
    }
}
