using System.Net;
using IaBimeta.Adapters.Ai.OpenAI.Testing;
using IaBimeta.AiGateway.Contracts;
using Microsoft.Extensions.DependencyInjection;
using IaBimeta.AiGateway.Contracts.Ports;

namespace IaBimeta.Adapters.Ai.OpenAI;

/// <summary>
/// O PEDIDO à Responses API (ADR 0017, seção C e Anexo A.1): endereço, cabeçalhos, o teto de tokens de saída imposto na requisição, o perfil do modelo, a saída estruturada estrita, o texto sempre válido
/// e o isolamento da credencial por requisição. HTTP simulado: nenhuma rede e nenhuma chave real.
/// </summary>
public sealed class OpenAiRequestTests
{
    [Fact]
    public async Task O_pedido_vai_para_a_Responses_API_com_a_credencial_so_no_cabecalho_da_requisicao()
    {
        var (provider, handler, http) = Requests.Provider();
        var id = Guid.NewGuid();

        await provider.CompleteAsync(Requests.Request(id: id), default);

        var pedido = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, pedido.Method);
        Assert.Equal("https://api.openai.com/v1/responses", pedido.Uri.ToString());
        Assert.Equal($"Bearer {Requests.Canary}", pedido.Authorization);
        Assert.Equal(id.ToString("N"), pedido.ClientRequestId);
        Assert.Equal("application/json", pedido.Accept);

        // A credencial NUNCA está no corpo nem num cabeçalho padrão do cliente compartilhado.
        Assert.DoesNotContain(Requests.Canary, pedido.Body);
        Assert.Null(http.DefaultRequestHeaders.Authorization);
        Assert.Empty(http.DefaultRequestHeaders);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(300)]
    [InlineData(4000)]
    public async Task O_teto_de_saida_vai_em_max_output_tokens_o_estado_nunca_e_guardado_e_nao_ha_temperatura(int teto)
    {
        var (provider, handler, _) = Requests.Provider();

        await provider.CompleteAsync(Requests.Request(maxOutput: teto), default);

        var corpo = Assert.Single(handler.Requests).Json;
        Assert.Equal(teto, (int)corpo["max_output_tokens"]!);
        Assert.False((bool)corpo["store"]!);
        Assert.Equal("gpt-6-luna", (string)corpo["model"]!);
        Assert.Null(corpo["temperature"]);
        Assert.Null(corpo["top_p"]);
        Assert.Null(corpo["tools"]);
    }

    [Fact]
    public async Task O_teto_abaixo_do_minimo_da_API_nao_envia_nada_e_nao_cobra()
    {
        var (provider, handler, _) = Requests.Provider();

        var resultado = await provider.CompleteAsync(Requests.Request(maxOutput: 15), default);

        Assert.Empty(handler.Requests);
        Assert.Equal((AiErrorKind.InvalidRequest, "max-output-tokens-too-low", AiUsageCertainty.NoneIncurred), (resultado.Error!.Kind, resultado.Error.Code, resultado.UsageCertainty));
        Assert.Null(resultado.Completion);
    }

    [Theory]
    [InlineData("atendimento-rapido", "none")]
    [InlineData("ATENDIMENTO-RAPIDO", "none")]
    [InlineData("perfil-desconhecido", null)]
    public async Task O_perfil_do_modelo_define_o_esforco_de_raciocinio_e_perfil_desconhecido_nao_envia_o_campo(string perfil, string? esforco)
    {
        var (provider, handler, _) = Requests.Provider();

        await provider.CompleteAsync(Requests.Request(profile: perfil), default);

        var corpo = Assert.Single(handler.Requests).Json;
        Assert.Equal(esforco, (string?)corpo["reasoning"]?["effort"]);
    }

    [Fact]
    public async Task Um_perfil_configurado_troca_o_esforco_sem_mudar_o_contrato()
    {
        var (provider, handler, _) = Requests.Provider(o => o.Profiles["qualidade"] = new OpenAiProfile { ReasoningEffort = "low" });

        await provider.CompleteAsync(Requests.Request(profile: "qualidade"), default);

        Assert.Equal("low", (string?)Assert.Single(handler.Requests).Json["reasoning"]!["effort"]);
    }

    [Fact]
    public async Task Saida_estruturada_envia_o_esquema_estrito_e_o_modo_texto_nao_envia_formato()
    {
        var (provider, handler, _) = Requests.Provider();

        await provider.CompleteAsync(Requests.Request(mode: AiOutputMode.StructuredJson), default);
        await provider.CompleteAsync(Requests.Request(mode: AiOutputMode.Text), default);

        var estruturada = handler.Requests[0].Json["text"]!["format"]!;
        Assert.Equal(("json_schema", "agent_decision", true), (((string)estruturada["type"]!), ((string)estruturada["name"]!), (bool)estruturada["strict"]!));
        Assert.Equal("object", (string)estruturada["schema"]!["type"]!);
        Assert.False((bool)estruturada["schema"]!["additionalProperties"]!);
        Assert.Null(handler.Requests[1].Json["text"]);
    }

    [Fact]
    public async Task A_instrucao_vai_em_instructions_e_as_mensagens_viram_input_com_os_papeis()
    {
        var (provider, handler, _) = Requests.Provider();
        var mensagens = new[]
        {
            new AiMessage(AiMessageRole.User, "Primeira"),
            new AiMessage(AiMessageRole.Assistant, "Resposta"),
            new AiMessage(AiMessageRole.User, "Segunda"),
        };

        await provider.CompleteAsync(Requests.Request(system: "Camada base e instruções do tenant", messages: mensagens), default);

        var corpo = Assert.Single(handler.Requests).Json;
        Assert.Equal("Camada base e instruções do tenant", (string)corpo["instructions"]!);
        var entrada = corpo["input"]!.AsArray();
        Assert.Equal(new[] { "user", "assistant", "user" }, entrada.Select(m => (string)m!["role"]!).ToArray());
        Assert.Equal(new[] { "Primeira", "Resposta", "Segunda" }, entrada.Select(m => (string)m!["content"]!).ToArray());
    }

    [Fact]
    public async Task Substituto_UTF16_solto_no_texto_vira_U_FFFD_e_a_chamada_segue_sem_excecao()
    {
        var (provider, handler, _) = Requests.Provider();
        var texto = "oi \uD83D tchau \uDE00 fim 😀";

        var resultado = await provider.CompleteAsync(Requests.Request(messages: new[] { new AiMessage(AiMessageRole.User, texto) }), default);

        Assert.NotNull(resultado.Completion);
        Assert.Equal("oi � tchau � fim 😀", (string)Assert.Single(handler.Requests).Json["input"]![0]!["content"]!);
    }

    [Theory]
    [InlineData("")]
    [InlineData("sk com espaco")]
    [InlineData("sk-abc\r\nX-Injetado: 1")]
    [InlineData("sk-áéí")]
    public async Task Credencial_fora_do_formato_de_chave_nao_envia_nada_e_nao_aparece_no_resultado(string segredo)
    {
        var (provider, handler, _) = Requests.Provider();

        var resultado = await provider.CompleteAsync(Requests.Request(secret: segredo), default);

        Assert.Empty(handler.Requests);
        Assert.Equal((AiErrorKind.InvalidCredentials, "credential-invalid-format", AiUsageCertainty.NoneIncurred), (resultado.Error!.Kind, resultado.Error.Code, resultado.UsageCertainty));
        Assert.DoesNotContain("X-Injetado", resultado.ToString());
    }

    [Fact]
    public async Task Esquema_de_saida_que_nao_e_JSON_nao_envia_nada()
    {
        var (provider, handler, _) = Requests.Provider();
        var pedido = Requests.Request(mode: AiOutputMode.StructuredJson) with { StructuredOutput = new AiStructuredOutput("agent_decision", "isto nao e json") };

        var resultado = await provider.CompleteAsync(pedido, default);

        Assert.Empty(handler.Requests);
        Assert.Equal((AiErrorKind.InvalidRequest, "structured-output-schema-invalid"), (resultado.Error!.Kind, resultado.Error.Code));
    }

    // Isolamento entre tenants: o cliente é compartilhado, a credencial não

    [Fact]
    public async Task Chamadas_simultaneas_de_tenants_diferentes_nunca_misturam_a_credencial()
    {
        var (provider, handler, _) = Requests.Provider();
        handler.Behavior = async (captura, ct) =>
        {
            await Task.Delay(Random.Shared.Next(1, 20), ct);
            return FakeHandler.Json(HttpStatusCode.OK, OpenAiBodies.Ok(captura.Authorization!)); // o "modelo" devolve quem o chamou
        };

        var chamadas = Enumerable.Range(0, 40).Select(async i =>
        {
            var segredo = $"sk-TENANT-{i:D2}-{Guid.NewGuid():N}";
            var resultado = await provider.CompleteAsync(Requests.Request(secret: segredo, messages: new[] { new AiMessage(AiMessageRole.User, $"pergunta do tenant {i}") }), default);
            return (segredo, resultado);
        });
        var todas = await Task.WhenAll(chamadas);

        // Cada chamada recebeu a PRÓPRIA credencial no cabeçalho, e nenhum corpo carrega a de outro.
        Assert.All(todas, t => Assert.Equal($"Bearer {t.segredo}", t.resultado.Completion!.Text));
        Assert.Equal(40, handler.Requests.Count);
        foreach (var pedido in handler.Requests)
        {
            Assert.DoesNotContain("sk-TENANT", pedido.Body);
        }
    }

    // A construção

    [Fact]
    public void O_adaptador_cria_o_proprio_cliente_sem_redirecionamento_com_a_vida_da_conexao_limitada_e_sem_cookie()
    {
        var opcoes = new OpenAiOptions { PooledConnectionLifetime = TimeSpan.FromMinutes(1), ConnectTimeout = TimeSpan.FromSeconds(3) };

        using var manipulador = OpenAiProvider.CreateHandler(opcoes);

        // A credencial nunca segue um redirecionamento para outro endereço.
        Assert.False(manipulador.AllowAutoRedirect);
        Assert.False(manipulador.UseCookies);
        Assert.Equal((TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(3)), (manipulador.PooledConnectionLifetime, manipulador.ConnectTimeout));
    }

    [Fact]
    public async Task Redirecionamento_nao_e_seguido_e_a_credencial_nao_vai_para_outro_endereco()
    {
        var (provider, handler, _) = Requests.Provider();
        handler.Returns(HttpStatusCode.Found, "{}", ("Location", "https://outro.exemplo/captura"));

        var resultado = await provider.CompleteAsync(Requests.Request(), default);

        Assert.Single(handler.Requests);
        Assert.Equal((AiErrorKind.InvalidRequest, "http-302", AiUsageCertainty.Unknown), (resultado.Error!.Kind, resultado.Error.Code, resultado.UsageCertainty));
    }

    [Fact]
    public void O_provedor_se_chama_openai_e_o_registro_de_DI_o_expoe_como_IAiProvider()
    {
        var servicos = new ServiceCollection();
        servicos.AddOpenAiProvider(o => o.Profiles["extra"] = new OpenAiProfile { ReasoningEffort = "low" });
        using var raiz = servicos.BuildServiceProvider();

        var provedores = raiz.GetServices<IAiProvider>().ToList();

        Assert.Equal("openai", Assert.Single(provedores).ProviderId);
        Assert.IsType<OpenAiProvider>(provedores[0]);
    }

    [Fact]
    public void Configuracao_invalida_falha_no_registro_e_nao_no_meio_do_processamento()
    {
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddOpenAiProvider(o => o.BaseAddress = "http://api.exemplo.com/v1/"));
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddOpenAiProvider(o => o.BaseAddress = "https://usuario:senha@api.exemplo.com/v1/"));
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddOpenAiProvider(o => o.MaxResponseBytes = 10));
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddOpenAiProvider(o => o.Profiles["x"] = new OpenAiProfile { ReasoningEffort = "Alto!" }));

        // HTTP só em laço local (teste) é aceito.
        new ServiceCollection().AddOpenAiProvider(o => o.BaseAddress = "http://127.0.0.1:8080/v1/");
    }
}
