using System.Net;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using IaBimeta.Api.Provisioning;
using IaBimeta.IntegrationTests.Agents;

namespace IaBimeta.IntegrationTests.Api;

/// <summary>
/// O comando <c>provision</c> (colocar uma empresa em funcionamento) contra a composição REAL do Api, com Api e Worker reais, PostgreSQL real e a Meta e a OpenAI simuladas: cria tudo, é idempotente, completa o que faltou
/// depois de uma falha, entrega segredos gerados uma única vez, não vaza segredo no relatório, e a empresa nova percorre o fluxo inteiro (cliente no WhatsApp, IA, transferência, atendente, resposta ao cliente) sem
/// enxergar nem ser enxergada pela outra empresa.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ProvisionamentoDeEmpresaTests(PostgresFixture banco)
{
    private const string SenhaDoAdministrador = "Senha-Do-Admin-2026!";
    private const string SenhaDaAtendente = "Senha-Da-Atendente-2026!";

    private sealed class Segredos(IReadOnlyDictionary<string, string> valores) : IProvisioningSecrets
    {
        public string? Get(string name) => valores.GetValueOrDefault(name);
    }

    private static string NovoSlug() => $"emp-{Guid.NewGuid().ToString("N")[..10]}";

    private static ProvisioningSpec Especificacao(string slug, string modelo = EnsaioDeIa.ModeloDoEnsaio, string tenantExtra = "\"unlimitedUsers\": true", string? atendentes = null, string instrucoes = "Você é a assistente do laboratório de teste.", string? whatsApp = null, string? administrador = null)
    {
        var adm = administrador ?? $"\"email\": \"admin@{slug}.local\", \"displayName\": \"Administradora\", \"passwordEnv\": \"ADM_SENHA\"";
        var wa = whatsApp ?? "{ \"mode\": \"simulated\" }";
        return ProvisioningSpec.Parse($$"""
        {
          "tenant": { "slug": "{{slug}}", "name": "Laboratório {{slug}}", {{tenantExtra}} },
          "administrator": { {{adm}} },
          "organization": { "name": "Laboratório {{slug}}", "unitName": "Matriz" },
          "queue": { "name": "Atendimento humano" },
          "ai": {
            "providerId": "openai",
            "modelId": "{{modelo}}",
            "instructions": "{{instrucoes}}",
            "handoffNotice": "Vou chamar uma pessoa da nossa equipe. Um instante!",
            "faq": [ { "question": "Qual o horário?", "answer": "Das 7h às 17h." } ],
            "apiKeyEnv": "IA_CHAVE"
          },
          "whatsApp": {{wa}},
          "attendants": {{atendentes ?? "[ { \"email\": \"ana@" + slug + ".local\", \"displayName\": \"Ana Atendente\", \"passwordEnv\": \"ATD_SENHA\" } ]"}}
        }
        """);
    }

    private static IReadOnlyDictionary<string, string> SegredosPadrao() => new Dictionary<string, string>
    {
        ["ADM_SENHA"] = SenhaDoAdministrador,
        ["ATD_SENHA"] = SenhaDaAtendente,
        ["IA_CHAVE"] = EnsaioDeIa.ChaveCanario,
    };

    private static async Task<string> LoginAsync(EnsaioDeWhatsApp e, string tenant, string email, string senha)
    {
        using var resposta = await e.Ia.Api.Client.PostAsJsonAsync("/auth/login", new { tenant, email, password = senha });
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        return (await resposta.Content.ReadFromJsonAsync<LoginDto>())!.Token;
    }

    private async Task<EnsaioDeWhatsApp> IniciarAsync(string prefixo)
    {
        var e = await EnsaioDeWhatsApp.IniciarAsync(banco, prefixo);
        await e.Ia.CadastrarChaveAsync();
        return e;
    }

    private static Task<ProvisioningReport> ProvisionarAsync(EnsaioDeWhatsApp e, ProvisioningSpec spec, IReadOnlyDictionary<string, string>? segredos = null) =>
        new TenantProvisioner(e.Ia.Api.Services, new Segredos(segredos ?? SegredosPadrao())).ProvisionAsync(spec);

    [Fact]
    public async Task Empresa_nova_e_provisionada_por_completo_e_repetir_o_comando_nao_duplica_nada()
    {
        await using var e = await IniciarAsync("provtudo");
        var slug = NovoSlug();
        var spec = Especificacao(slug);

        var primeira = await ProvisionarAsync(e, spec);

        Assert.True(primeira.Succeeded, string.Join(" | ", primeira.Steps.Select(s => $"{s.Name}: {s.Outcome} {s.Detail}")));
        Assert.All(primeira.Steps.Where(s => s.Name != "WhatsApp"), s => Assert.NotEqual(StepOutcome.Failed, s.Outcome));
        Assert.Equal(StepOutcome.Created, primeira.Steps.Single(s => s.Name == "Tenant").Outcome);
        Assert.Equal(StepOutcome.Created, primeira.Steps.Single(s => s.Name == "Administrador").Outcome);
        Assert.Equal(StepOutcome.Created, primeira.Steps.Single(s => s.Name == "Agente de IA").Outcome);
        Assert.Equal(StepOutcome.Created, primeira.Steps.Single(s => s.Name == "WhatsApp").Outcome);
        Assert.Empty(primeira.GeneratedSecrets); // todas as senhas e chaves vieram do ambiente
        Assert.True(primeira.WhatsAppIsActive);
        Assert.StartsWith("/webhooks/whatsapp/", primeira.WebhookPath);

        // Quem foi criado entra pela API: o administrador (acesso ao tenant todo) e a atendente (só a fila dela).
        var admin = await LoginAsync(e, slug, $"admin@{slug}.local", SenhaDoAdministrador);
        Assert.Equal($"admin@{slug}.local", (await e.Ia.Api.LerAsync<EuDto>(admin, "/auth/me")).Email);
        var ana = await LoginAsync(e, slug, $"ana@{slug}.local", SenhaDaAtendente);
        var filas = (await e.Ia.Api.LerAsync<JsonNode>(ana, "/contact-center/me/queues")).AsArray();
        Assert.Equal("Atendimento humano", Assert.Single(filas)!["name"]!.GetValue<string>());
        Assert.Equal(primeira.QueueId, Assert.Single(filas)!["id"]!.GetValue<Guid>());

        // Repetir: tudo já existe, nada novo é criado, as senhas não mudam e o webhook é o mesmo.
        var segunda = await ProvisionarAsync(e, spec);
        Assert.True(segunda.Succeeded);
        Assert.All(segunda.Steps, s => Assert.True(s.Outcome is StepOutcome.Exists, $"{s.Name}: {s.Outcome} {s.Detail}"));
        Assert.Equal(primeira.WebhookPath, segunda.WebhookPath);
        Assert.Equal(primeira.OrganizationId, segunda.OrganizationId);
        Assert.Equal(primeira.QueueId, segunda.QueueId);
        _ = await LoginAsync(e, slug, $"admin@{slug}.local", SenhaDoAdministrador);

        // Trocar as instruções ou as perguntas frequentes publica uma versão nova do agente (e só ela).
        var terceira = await ProvisionarAsync(e, Especificacao(slug, instrucoes: "Você é a assistente do laboratório de teste. Seja breve."));
        Assert.Equal(StepOutcome.Updated, terceira.Steps.Single(s => s.Name == "Agente de IA").Outcome);
        Assert.Equal(1, terceira.Steps.Count(s => s.Outcome != StepOutcome.Exists));
    }

    [Fact]
    public async Task Os_modelos_de_mensagem_aprovados_sao_associados_a_conta_da_empresa_certa_repetir_atualiza_e_modelo_nao_suportado_e_recusado_com_o_motivo()
    {
        await using var e = await IniciarAsync("provmodelos");
        var slug = NovoSlug();
        var modelos = """
            "templates": [
              { "name": "resultado_disponivel", "language": "pt_BR", "category": "UTILITY", "status": "APPROVED",
                "components": [ { "type": "BODY", "text": "Olá {{1}}, o exame {{2}} está pronto." } ] },
              { "name": "com_imagem", "language": "pt_BR", "category": "MARKETING", "status": "APPROVED",
                "components": [ { "type": "HEADER", "format": "IMAGE" }, { "type": "BODY", "text": "Oi" } ] }
            ]
            """;

        var primeira = await ProvisionarAsync(e, Especificacao(slug, whatsApp: "{ \"mode\": \"simulated\", " + modelos + " }"));

        Assert.True(primeira.Succeeded, string.Join(" | ", primeira.Steps.Select(s => $"{s.Name}: {s.Outcome} {s.Detail}")));
        var etapa = primeira.Steps.Single(s => s.Name == "Modelos de mensagem");
        Assert.Equal(StepOutcome.Created, etapa.Outcome);
        Assert.Contains("1 novo(s)", etapa.Detail);
        Assert.Contains("1 aprovado(s)", etapa.Detail);
        Assert.Contains("IMAGE", etapa.Detail); // o recusado, com o motivo

        // Os modelos estão na conta DESTA empresa (e só nela): o administrador da empresa os vê pela API.
        var admin = await LoginAsync(e, slug, $"admin@{slug}.local", SenhaDoAdministrador);
        var lista = (await e.Ia.Api.LerAsync<JsonNode>(admin, $"/channels/accounts/{primeira.ChannelAccountId}/templates")).AsArray();
        Assert.Equal("resultado_disponivel", Assert.Single(lista)!["name"]!.GetValue<string>());
        Assert.Empty((await e.Ia.Api.LerAsync<JsonNode>(await e.TokenDeAsync(IaBimeta.Identity.Domain.Role.OrganizationAdmin), $"/channels/accounts/{e.ContaId}/templates")).AsArray());

        // Repetir não muda nada; o texto novo na Meta atualiza o mesmo modelo.
        var segunda = await ProvisionarAsync(e, Especificacao(slug, whatsApp: "{ \"mode\": \"simulated\", " + modelos + " }"));
        Assert.Equal(StepOutcome.Exists, segunda.Steps.Single(s => s.Name == "Modelos de mensagem").Outcome);
        var terceira = await ProvisionarAsync(e, Especificacao(slug, whatsApp: "{ \"mode\": \"simulated\", " + modelos.Replace("está pronto", "já está disponível") + " }"));
        Assert.Equal(StepOutcome.Updated, terceira.Steps.Single(s => s.Name == "Modelos de mensagem").Outcome);

        // Se NENHUM modelo é aceito, a etapa falha com o motivo e o relatório diz.
        var slugRuim = NovoSlug();
        var ruim = await ProvisionarAsync(e, Especificacao(slugRuim, whatsApp: "{ \"mode\": \"simulated\", \"templates\": [ { \"name\": \"x\", \"language\": \"pt_BR\", \"category\": \"AUTHENTICATION\", \"status\": \"APPROVED\", \"components\": [ { \"type\": \"BODY\", \"text\": \"Código {{1}}\" } ] } ] }"));
        Assert.False(ruim.Succeeded);
        Assert.Contains("autenticação", ruim.Steps.Single(s => s.Name == "Modelos de mensagem").Detail);
    }

    [Fact]
    public void O_arquivo_da_empresa_recusa_modelo_que_nao_e_objeto_e_lista_grande_demais()
    {
        var naoObjeto = Assert.Throws<ProvisioningSpecException>(() => Especificacao("abc-modelo", whatsApp: """{ "mode": "simulated", "templates": [ "texto" ] }"""));
        Assert.Contains("whatsApp.templates[0]", naoObjeto.Message);

        var demais = string.Join(",", Enumerable.Repeat("""{ "name": "a" }""", IaBimeta.Channels.Application.ChannelTemplateService.MaxTemplatesPerRegistration + 1));
        var grande = Assert.Throws<ProvisioningSpecException>(() => Especificacao("abc-modelo", whatsApp: "{ \"mode\": \"simulated\", \"templates\": [" + demais + "] }"));
        Assert.Contains("no máximo", grande.Message);
    }

    [Fact]
    public async Task A_empresa_provisionada_percorre_o_fluxo_completo_e_nao_enxerga_nem_e_vista_pela_outra()
    {
        await using var e = await IniciarAsync("provfluxo");
        e.Ia.OpenAi.Roteiros.Definir(EnsaioDeIa.ChaveCanario, (_, _) => Task.FromResult(ServidorOpenAiSimulado.Sucesso(CenarioDeAgentes.Transferir("price"), 150, 0, 15, "req_prov")));
        var slug = NovoSlug();
        var relatorio = await ProvisionarAsync(e, Especificacao(slug));
        Assert.True(relatorio.Succeeded);
        var (waba, numero) = TenantProvisioner.SimulatedIdentifiers(slug);
        var webhook = Guid.Parse(relatorio.WebhookPath!["/webhooks/whatsapp/".Length..]);
        var ana = await LoginAsync(e, slug, $"ana@{slug}.local", SenhaDaAtendente);
        const string cliente = "5511999990401";

        // O cliente escreve pelo WhatsApp (webhook assinado com o segredo do canal simulado): a IA transfere e o cliente recebe o aviso DA EMPRESA NOVA.
        var corpo = EnsaioDeWhatsApp.MontarPayloadDeTexto(waba, numero, "wamid.PROV0001", cliente, "Cliente da Empresa Nova", "Quanto custa o hemograma?");
        Assert.Equal(HttpStatusCode.OK, await e.EnviarWebhookAsync(corpo, EnsaioDeWhatsApp.Assinar(corpo, TenantProvisioner.SimulatedAppSecret), webhook));
        await e.EsperarAsync(async () => e.Graph.Capturas.Count == 1, "o aviso de transferência deveria sair pelo WhatsApp");
        var aviso = Assert.Single(e.Graph.Capturas);
        Assert.Equal((cliente, "Vou chamar uma pessoa da nossa equipe. Um instante!"), (aviso.Para, aviso.Texto));
        Assert.EndsWith($"/{numero}/messages", aviso.Caminho);
        Assert.Equal(TenantProvisioner.SimulatedAccessToken, aviso.Credencial);

        // A conversa espera na fila da empresa nova; a atendente dela assume e responde, e a resposta chega SÓ ao cliente certo.
        string? itemId = null;
        await e.EsperarAsync(
            async () =>
            {
                itemId = (await e.Ia.Api.LerAsync<JsonNode>(ana, "/contact-center/items?status=Waiting&take=20")).AsArray().FirstOrDefault()?["id"]?.GetValue<string>();
                return itemId is not null;
            },
            "a conversa deveria esperar na fila da empresa nova");
        Assert.Equal(HttpStatusCode.OK, await e.Ia.Api.StatusAsync(ana, HttpMethod.Post, $"/contact-center/items/{itemId}/claim", new { requestId = Guid.NewGuid() }));
        Assert.Equal(HttpStatusCode.OK, await e.Ia.Api.StatusAsync(ana, HttpMethod.Post, $"/contact-center/items/{itemId}/reply", new { requestId = Guid.NewGuid(), text = "Olá! O hemograma segue a tabela do convênio." }));
        await e.EsperarAsync(async () => e.Graph.Capturas.Count == 2, "a resposta da atendente deveria sair pelo WhatsApp");
        Assert.Equal((cliente, "Olá! O hemograma segue a tabela do convênio."), (e.Graph.Capturas[1].Para, e.Graph.Capturas[1].Texto));

        // Isolamento: um atendente da OUTRA empresa (a do ensaio) não vê a conversa nem a lista da nova, e o contrário também.
        var (_, tokenDoOutro) = await e.NovoAtendenteAsync();
        Assert.DoesNotContain(itemId, (await e.Ia.Api.LerAsync<JsonNode>(tokenDoOutro, "/contact-center/items?take=50")).AsArray().Select(i => i!["id"]!.GetValue<string>()));
        Assert.Equal(HttpStatusCode.NotFound, await e.Ia.Api.StatusAsync(tokenDoOutro, HttpMethod.Get, $"/contact-center/items/{itemId}"));
        Assert.Equal(HttpStatusCode.NotFound, await e.Ia.Api.StatusAsync(tokenDoOutro, HttpMethod.Post, $"/contact-center/items/{itemId}/reply", new { requestId = Guid.NewGuid(), text = "Intruso" }));

        // Encerrar converge.
        Assert.Equal(HttpStatusCode.OK, await e.Ia.Api.StatusAsync(ana, HttpMethod.Post, $"/contact-center/items/{itemId}/close", new { requestId = Guid.NewGuid(), reasonCode = "resolved" }));
    }

    [Fact]
    public async Task Falha_numa_etapa_diz_o_que_corrigir_e_repetir_completa_o_resto_e_a_senha_gerada_vale_e_sai_uma_vez_so()
    {
        await using var e = await IniciarAsync("provfalha");
        var slug = NovoSlug();
        var semSenhas = new Dictionary<string, string> { ["IA_CHAVE"] = EnsaioDeIa.ChaveCanario, ["ATD_SENHA"] = SenhaDaAtendente };

        // Modelo fora do catálogo: tudo o que vem antes é criado, a IA falha com a dica, e o que depende dela não roda.
        var falhou = await ProvisionarAsync(e, Especificacao(slug, modelo: "modelo-que-nao-existe"), semSenhas);

        Assert.False(falhou.Succeeded);
        var etapa = falhou.Steps.Single(s => s.Outcome == StepOutcome.Failed);
        Assert.Equal("IA do tenant", etapa.Name);
        Assert.Contains("AiGateway:Catalog:Models", etapa.Detail);
        Assert.Equal(StepOutcome.Created, falhou.Steps.Single(s => s.Name == "Fila de atendimento humano").Outcome);
        Assert.DoesNotContain(falhou.Steps, s => s.Name == "WhatsApp"); // depende da IA nesta ordem: não rodou
        Assert.DoesNotContain(falhou.Steps, s => s.Name.StartsWith("Atendente", StringComparison.Ordinal));

        // Sem a variável da senha do administrador, uma senha FORTE é gerada e entregue uma vez; ela é a que vale no login.
        var gerada = Assert.Single(falhou.GeneratedSecrets);
        Assert.Equal($"admin@{slug}.local", gerada.Owner);
        Assert.True(gerada.Value.Length >= 20);
        _ = await LoginAsync(e, slug, $"admin@{slug}.local", gerada.Value);

        // Corrigido o arquivo, repetir completa o resto sem recriar nada e sem gerar outra senha (a do administrador não muda).
        var completo = await ProvisionarAsync(e, Especificacao(slug), semSenhas);
        Assert.True(completo.Succeeded, string.Join(" | ", completo.Steps.Select(s => $"{s.Name}: {s.Outcome} {s.Detail}")));
        Assert.Equal(StepOutcome.Exists, completo.Steps.Single(s => s.Name == "Tenant").Outcome);
        Assert.Equal(StepOutcome.Exists, completo.Steps.Single(s => s.Name == "Administrador").Outcome);
        Assert.Equal(StepOutcome.Created, completo.Steps.Single(s => s.Name == "WhatsApp").Outcome);
        Assert.Empty(completo.GeneratedSecrets);
        _ = await LoginAsync(e, slug, $"admin@{slug}.local", gerada.Value);
    }

    [Fact]
    public async Task Conta_real_nasce_inativa_o_token_de_verificacao_e_gerado_e_nenhum_segredo_aparece_no_relatorio()
    {
        await using var e = await IniciarAsync("provreal");
        var slug = NovoSlug();
        const string appSecret = "app-secret-real-canario-9876543210";
        const string accessToken = "EAAG-token-real-canario-0123456789";
        var segredos = new Dictionary<string, string>(SegredosPadrao())
        {
            ["WA_APP_SECRET"] = appSecret,
            ["WA_ACCESS_TOKEN"] = accessToken,
        };
        var spec = Especificacao(
            slug,
            whatsApp: $$"""
            { "mode": "real", "accountName": "Conta real de teste", "wabaId": "{{slug}}-waba", "phoneNumberId": "55{{Random.Shared.NextInt64(100000000000, 999999999999)}}", "displayPhoneNumber": "+55 11 3000-1111", "appSecretEnv": "WA_APP_SECRET", "accessTokenEnv": "WA_ACCESS_TOKEN" }
            """);

        var relatorio = await ProvisionarAsync(e, spec, segredos);

        Assert.True(relatorio.Succeeded, string.Join(" | ", relatorio.Steps.Select(s => $"{s.Name}: {s.Outcome} {s.Detail}")));
        Assert.False(relatorio.WhatsAppIsActive); // nada sai para clientes reais antes do teste de prontidão
        Assert.Contains("INATIVO", relatorio.Steps.Single(s => s.Name == "WhatsApp").Detail);
        var verificacao = Assert.Single(relatorio.GeneratedSecrets);
        Assert.Contains("verificação", verificacao.Kind);
        Assert.Equal(32, verificacao.Value.Length);

        // O que o operador vê (relatório impresso) nunca traz o que veio do ambiente: nem senha, nem chave da IA, nem credencial da Meta.
        var saida = new StringWriter();
        await ProvisionCommand.PrintAsync(relatorio, spec, saida);
        var texto = saida.ToString();
        foreach (var segredo in new[] { appSecret, accessToken, SenhaDoAdministrador, SenhaDaAtendente, EnsaioDeIa.ChaveCanario })
        {
            Assert.DoesNotContain(segredo, texto, StringComparison.Ordinal);
        }

        Assert.Contains(verificacao.Value, texto, StringComparison.Ordinal); // o gerado SIM, uma vez
        Assert.DoesNotContain(e.Ia.Logs.Linhas, l => l.Contains(appSecret) || l.Contains(accessToken) || l.Contains(EnsaioDeIa.ChaveCanario));

        // Conta real sem a credencial no ambiente: erro claro, nenhuma conta pela metade.
        var outroSlug = NovoSlug();
        var sufixo = Guid.NewGuid().ToString("N")[..10];
        var semCredencial = Especificacao(outroSlug, whatsApp: $$"""{ "mode": "real", "wabaId": "waba-{{sufixo}}", "phoneNumberId": "55{{Random.Shared.NextInt64(100000000000, 999999999999)}}", "appSecretEnv": "WA_APP_SECRET_DEPOIS", "accessTokenEnv": "WA_ACCESS_TOKEN" }""");
        var falhou = await ProvisionarAsync(e, semCredencial, segredos);
        Assert.False(falhou.Succeeded);
        Assert.Contains("WA_APP_SECRET_DEPOIS", falhou.Steps.Single(s => s.Outcome == StepOutcome.Failed).Detail);

        // Nada ficou pela metade: o nome da conta continua livre, e repetir com a variável definida cria a conta normalmente.
        var comCredencial = new Dictionary<string, string>(segredos) { ["WA_APP_SECRET_DEPOIS"] = "app-secret-definido-depois-0123456789" };
        var completo = await ProvisionarAsync(e, semCredencial, comCredencial);
        Assert.True(completo.Succeeded, string.Join(" | ", completo.Steps.Select(s => $"{s.Name}: {s.Outcome} {s.Detail}")));
        Assert.Equal(StepOutcome.Created, completo.Steps.Single(s => s.Name == "WhatsApp").Outcome);
    }

    [Fact]
    public void O_arquivo_da_empresa_recusa_campo_desconhecido_valor_no_lugar_do_nome_da_variavel_e_campo_obrigatorio_ausente()
    {
        // Erro de digitação não vira configuração ignorada em silêncio.
        var desconhecido = Assert.Throws<ProvisioningSpecException>(() => ProvisioningSpec.Parse("""{ "tenant": { "slug": "abc", "name": "A", "slugg": "x" } }"""));
        Assert.Contains("slugg", desconhecido.Message);

        // O arquivo guarda o NOME da variável de ambiente, nunca o segredo.
        var valor = Assert.Throws<ProvisioningSpecException>(() => Especificacao("abc-teste", administrador: "\"email\": \"a@b.c\", \"displayName\": \"A\", \"passwordEnv\": \"minha senha real 123\""));
        Assert.Contains("administrator.passwordEnv", valor.Message);

        var faltando = Assert.Throws<ProvisioningSpecException>(() => ProvisioningSpec.Parse("{}"));
        Assert.Contains("tenant.slug", faltando.Message);
        Assert.Contains("ai.modelId", faltando.Message);

        Assert.Throws<ProvisioningSpecException>(() => ProvisioningSpec.Parse("isto nao e json"));
        Assert.Throws<ProvisioningSpecException>(() => ProvisioningSpec.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json")));
    }

    [Fact]
    public async Task O_arquivo_de_exemplo_entregue_com_o_script_e_valido_e_o_validar_so_nao_toca_em_nada()
    {
        // O exemplo que o operador copia não pode apodrecer: é lido (com os comentários) pelo mesmo leitor do comando.
        var pasta = new DirectoryInfo(AppContext.BaseDirectory);
        while (pasta is not null && !File.Exists(Path.Combine(pasta.FullName, "tools", "ops", "empresa-exemplo.json")))
        {
            pasta = pasta.Parent;
        }

        Assert.NotNull(pasta);
        var exemplo = Path.Combine(pasta!.FullName, "tools", "ops", "empresa-exemplo.json");
        var spec = ProvisioningSpec.Load(exemplo);
        Assert.Equal("laboratorio-exemplo", spec.Tenant.Slug);
        Assert.Equal(WhatsAppMode.Simulated, spec.WhatsApp!.Mode);
        Assert.Equal(2, spec.Attendants.Count);
        Assert.Equal(2, spec.Ai.Faq.Count);
        Assert.Equal(2, spec.WhatsApp.Templates.Count);

        // Os modelos do exemplo passam no leitor do cadastro de verdade (nenhum é recusado).
        foreach (var modelo in spec.WhatsApp.Templates)
        {
            Assert.Equal(IaBimeta.Channels.Contracts.TemplateStatus.Approved, IaBimeta.Channels.Application.TemplateDefinitionParser.Parse(modelo).Status);
        }

        var saida = new StringWriter();
        Assert.Equal(0, await ProvisionCommand.RunAsync(new[] { "--file", exemplo, "--validate-only" }, saida));
        Assert.Contains("Nada foi alterado", saida.ToString());
    }

    [Fact]
    public async Task O_comando_com_uso_errado_ou_arquivo_invalido_sai_com_2_sem_tocar_no_banco()
    {
        var saida = new StringWriter();
        Assert.Equal(2, await ProvisionCommand.RunAsync(new[] { "--bogus" }, saida));
        Assert.Equal(2, await ProvisionCommand.RunAsync(Array.Empty<string>(), saida));
        var arquivo = Path.Combine(Path.GetTempPath(), $"empresa-invalida-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(arquivo, "{ \"tenant\": { \"slug\": \"x\" } }");
        try
        {
            Assert.Equal(2, await ProvisionCommand.RunAsync(new[] { "--file", arquivo }, saida));
        }
        finally
        {
            File.Delete(arquivo);
        }

        Assert.Contains("Uso: provision", saida.ToString());
        Assert.Contains("tenant.name", saida.ToString());
    }

    // Etapa 01: limite de usuários e primeiro administrador.

    [Fact]
    public async Task Empresa_nova_sem_limite_de_usuarios_e_recusada_a_nao_ser_pela_excecao_explicita()
    {
        await using var e = await IniciarAsync("provlimite");
        var slug = NovoSlug();

        var recusada = await ProvisionarAsync(e, Especificacao(slug, tenantExtra: "", atendentes: "[]"));

        Assert.False(recusada.Succeeded);
        var passo = recusada.Steps.Single(s => s.Name == "Política de usuários");
        Assert.Equal(StepOutcome.Failed, passo.Outcome);
        Assert.Contains("tenant.maxUsers", passo.Detail);
        await using var escopo = e.Ia.Api.Services.CreateAsyncScope();
        // A empresa pode ter sido cadastrada, mas NÃO foi liberada: sem política explícita, nada além do tenant existe.
        Assert.False((await escopo.ServiceProvider.GetRequiredService<IaBimeta.Identity.Application.TenantUserPolicyService>().GetAsync(slug))!.Value.Policy.IsExplicit);
    }

    [Fact]
    public async Task Empresa_nova_nasce_com_o_limite_e_o_cadastro_informados_e_o_comando_tenant_users_altera_e_audita()
    {
        await using var e = await IniciarAsync("provpolitica");
        var slug = NovoSlug();

        var relatorio = await ProvisionarAsync(e, Especificacao(slug, tenantExtra: "\"maxUsers\": 3, \"userRegistrationEnabled\": true", atendentes: "[]"));
        Assert.True(relatorio.Succeeded, string.Join(" | ", relatorio.Steps.Select(s => $"{s.Name}: {s.Outcome} {s.Detail}")));

        async Task<IaBimeta.Identity.Application.UserPolicy> LerAsync()
        {
            await using var escopo = e.Ia.Api.Services.CreateAsyncScope();
            return (await escopo.ServiceProvider.GetRequiredService<IaBimeta.Identity.Application.TenantUserPolicyService>().GetAsync(slug))!.Value.Policy;
        }

        var nascida = await LerAsync();
        Assert.Equal(3, nascida.MaxUsers);
        Assert.True(nascida.RegistrationEnabled);
        Assert.True(nascida.IsExplicit);

        var saida = new StringWriter();
        Assert.Equal(0, await TenantUsersCommand.ApplyAsync(e.Ia.Api.Services, slug, 10, false, false, saida));
        var depois = await LerAsync();
        Assert.Equal(10, depois.MaxUsers);
        Assert.False(depois.RegistrationEnabled);
        Assert.Contains("desabilitado", saida.ToString());

        Assert.Equal(0, await TenantUsersCommand.ApplyAsync(e.Ia.Api.Services, slug, null, true, null, new StringWriter()));
        Assert.Null((await LerAsync()).MaxUsers);

        Assert.Equal(2, await TenantUsersCommand.ApplyAsync(e.Ia.Api.Services, slug, 0, false, null, new StringWriter()));
        Assert.Equal(1, await TenantUsersCommand.ApplyAsync(e.Ia.Api.Services, "nao-existe-" + slug, 5, false, null, new StringWriter()));
    }

    [Fact]
    public async Task Primeiro_administrador_pelo_comando_emite_token_uma_vez_e_nao_se_repete()
    {
        await using var e = await IniciarAsync("provprimeiro");
        var tenant = await banco.RegistrarTenantAsync("primeiro");
        var email = $"dono{Guid.NewGuid().ToString("N")[..6]}@teste.local";

        // Sem a política de usuários definida, o primeiro administrador é recusado (a falta de política nunca vira empresa ilimitada).
        var semPolitica = new StringWriter();
        Assert.Equal(2, await FirstAdminCommand.IssueAsync(e.Ia.Api.Services, tenant.Slug, email, "Dona da Empresa", semPolitica));
        Assert.DoesNotContain("ibr_", semPolitica.ToString());
        Assert.Equal(0, await TenantUsersCommand.ApplyAsync(e.Ia.Api.Services, tenant.Slug, 5, false, null, new StringWriter()));

        var saida = new StringWriter();
        Assert.Equal(0, await FirstAdminCommand.IssueAsync(e.Ia.Api.Services, tenant.Slug, email, "Dona da Empresa", saida));
        Assert.Contains("ibr_", saida.ToString());
        Assert.Contains("UMA vez", saida.ToString());

        var repetido = new StringWriter();
        Assert.Equal(2, await FirstAdminCommand.IssueAsync(e.Ia.Api.Services, tenant.Slug, "outro" + email, "Outro", repetido));
        Assert.DoesNotContain("ibr_", repetido.ToString());
        Assert.Equal(1, await FirstAdminCommand.IssueAsync(e.Ia.Api.Services, "nao-existe-" + tenant.Slug, email, "X", new StringWriter()));
    }

    [Fact]
    public async Task Provisionamento_comercial_so_cria_o_primeiro_administrador_e_nunca_atendentes_nem_um_segundo_administrador()
    {
        // Atendentes no arquivo só em ambiente interno (unlimitedUsers): numa empresa com limite, quem cadastra usuários é o administrador da empresa.
        var erro = Assert.Throws<ProvisioningSpecException>(() => Especificacao(NovoSlug(), tenantExtra: "\"maxUsers\": 5"));
        Assert.Contains(erro.Errors, m => m.Contains("attendants", StringComparison.Ordinal));

        await using var e = await IniciarAsync("provunico");
        var slug = NovoSlug();
        var spec = Especificacao(slug, tenantExtra: "\"maxUsers\": 5", atendentes: "[]");
        Assert.True((await ProvisionarAsync(e, spec)).Succeeded);

        // Repetir com OUTRO e-mail de administrador NÃO cria um segundo administrador (o provisionamento não é meio de cadastrar administradores).
        var outro = Especificacao(slug, tenantExtra: "\"maxUsers\": 5", atendentes: "[]", administrador: $"\"email\": \"outro@{slug}.local\", \"displayName\": \"Outro\", \"passwordEnv\": \"ADM_SENHA\"");
        var segunda = await ProvisionarAsync(e, outro);

        Assert.False(segunda.Succeeded);
        Assert.Equal(StepOutcome.Failed, segunda.Steps.Single(s => s.Name == "Administrador").Outcome);
        await using var escopo = e.Ia.Api.Services.CreateAsyncScope();
        var tenant = (await escopo.ServiceProvider.GetRequiredService<IaBimeta.Tenancy.Contracts.Queries.ITenantDirectory>().FindBySlugAsync(slug))!;
        escopo.ServiceProvider.GetRequiredService<IaBimeta.SharedKernel.RequestContextHolder>().Set(new IaBimeta.SharedKernel.RequestContext(tenant.TenantId, "teste"));
        var uso = await escopo.ServiceProvider.GetRequiredService<IaBimeta.Identity.Application.IUserAdminStore>().GetSeatUsageAsync(tenant.TenantId, DateTimeOffset.UtcNow, default);
        Assert.Equal(1, uso.SeatsUsed);
    }

    [Fact]
    public async Task Politica_e_criada_antes_de_qualquer_usuario_falha_parcial_nao_libera_a_empresa_e_repetir_completa()
    {
        await using var e = await IniciarAsync("provparcial");
        var slug = NovoSlug();

        // 1.ª execução com o arquivo sem limite: o tenant é cadastrado, mas a política é obrigatória e NADA além dele é criado (nem organização, nem administrador).
        var falha = await ProvisionarAsync(e, Especificacao(slug, tenantExtra: "", atendentes: "[]"));
        Assert.False(falha.Succeeded);
        Assert.Equal(StepOutcome.Failed, falha.Steps.Single(s => s.Name == "Política de usuários").Outcome);
        Assert.DoesNotContain(falha.Steps, s => s.Name is "Organização" or "Administrador");

        await using (var escopo = e.Ia.Api.Services.CreateAsyncScope())
        {
            var sp = escopo.ServiceProvider;
            var estado = (await sp.GetRequiredService<IaBimeta.Identity.Application.TenantUserPolicyService>().GetAsync(slug))!.Value;
            Assert.False(estado.Policy.IsExplicit);
            sp.GetRequiredService<IaBimeta.SharedKernel.RequestContextHolder>().Set(new IaBimeta.SharedKernel.RequestContext(estado.TenantId, "teste"));
            Assert.Equal(0, (await sp.GetRequiredService<IaBimeta.Identity.Application.IUserAdminStore>().GetSeatUsageAsync(estado.TenantId, DateTimeOffset.UtcNow, default)).SeatsUsed);

            // Mesmo chamando o primeiro administrador direto, sem política explícita, ele é recusado.
            var usuarios = sp.GetRequiredService<IaBimeta.Identity.Application.UserService>();
            await Assert.ThrowsAsync<IaBimeta.Identity.Domain.IdentityValidationException>(() => usuarios.EnsureFirstTenantAdministratorAsync($"admin@{slug}.local", "Administradora", SenhaDoAdministrador));
        }

        // 2.ª execução, agora com o limite: completa o que faltava (o tenant já existe; política, organização e administrador são criados).
        var segunda = await ProvisionarAsync(e, Especificacao(slug, tenantExtra: "\"maxUsers\": 3", atendentes: "[]"));
        Assert.True(segunda.Succeeded, string.Join(" | ", segunda.Steps.Select(s => $"{s.Name}: {s.Outcome} {s.Detail}")));
        Assert.Equal(StepOutcome.Exists, segunda.Steps.Single(s => s.Name == "Tenant").Outcome);
        Assert.Equal(StepOutcome.Created, segunda.Steps.Single(s => s.Name == "Política de usuários").Outcome);
        Assert.Equal(StepOutcome.Created, segunda.Steps.Single(s => s.Name == "Administrador").Outcome);

        // 3.ª execução: tudo existe, e o limite NÃO é alterado por um arquivo diferente.
        var terceira = await ProvisionarAsync(e, Especificacao(slug, tenantExtra: "\"maxUsers\": 50", atendentes: "[]"));
        Assert.True(terceira.Succeeded);
        Assert.Equal(StepOutcome.Exists, terceira.Steps.Single(s => s.Name == "Política de usuários").Outcome);
        await using var final = e.Ia.Api.Services.CreateAsyncScope();
        Assert.Equal(3, (await final.ServiceProvider.GetRequiredService<IaBimeta.Identity.Application.TenantUserPolicyService>().GetAsync(slug))!.Value.Policy.MaxUsers);
    }

    [Fact]
    public async Task Atendentes_do_provisionamento_so_existem_em_Development_mesmo_com_unlimitedUsers_e_o_seed_interno_so_e_chamado_pelo_codigo_de_desenvolvimento()
    {
        // 1) Num ambiente que não é Development, "unlimitedUsers" sozinho não libera atendentes pelo provisionamento.
        await using var api = await ApiServer.StartAsync("Production", ApiServer.ConfiguracaoDeProducao());
        var slug = NovoSlug();
        // Modelo do catálogo de produção e sem chave de IA (nenhuma credencial real): o provisionamento chega ao passo dos atendentes.
        var semChave = SegredosPadrao().Where(p => p.Key != "IA_CHAVE").ToDictionary(p => p.Key, p => p.Value);
        var relatorio = await new TenantProvisioner(api.Services, new Segredos(semChave)).ProvisionAsync(Especificacao(slug, modelo: "gpt-6-luna", whatsApp: "null"));
        var atendente = relatorio.Steps.SingleOrDefault(s => s.Name.StartsWith("Atendente ", StringComparison.Ordinal));
        Assert.True(atendente is not null, string.Join(" | ", relatorio.Steps.Select(s => $"{s.Name}: {s.Outcome} {s.Detail}")));
        Assert.Equal(StepOutcome.Failed, atendente!.Outcome);
        Assert.Contains("Development", atendente.Detail, StringComparison.Ordinal);

        // 2) O seed interno (senha escolhida por quem opera) só é chamado pelo código de desenvolvimento: nenhum código de produção o chama.
        var pasta = new DirectoryInfo(AppContext.BaseDirectory);
        while (pasta is not null && !Directory.Exists(Path.Combine(pasta.FullName, "backend", "host")))
        {
            pasta = pasta.Parent;
        }

        Assert.NotNull(pasta);
        var chamadores = Directory.EnumerateFiles(Path.Combine(pasta!.FullName, "backend"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains(".EnsureTenantAdministratorAsync(", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(pasta.FullName, f).Replace(Path.DirectorySeparatorChar, '/'))
            .ToList();
        Assert.All(chamadores, f => Assert.StartsWith("backend/host/IaBimeta.Api/Development/", f, StringComparison.Ordinal));
    }
}
