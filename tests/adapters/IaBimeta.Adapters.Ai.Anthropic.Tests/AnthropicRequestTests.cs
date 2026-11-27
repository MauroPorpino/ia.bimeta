using System.Net;
using IaBimeta.Adapters.Ai.Anthropic.Testing;
using IaBimeta.AiGateway.Contracts;
using IaBimeta.AiGateway.Contracts.Ports;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.Adapters.Ai.Anthropic;

/// <summary>
/// O PEDIDO à Messages API: endereço, cabeçalhos obrigatórios, autenticação por requisição, instrução de sistema, histórico, modelo, teto de saída, perfil, saída estruturada e o isolamento da credencial.
/// HTTP simulado: nenhuma rede e nenhuma chave real.
/// </summary>
public sealed class AnthropicRequestTests
{
    [Fact]
    public async Task O_pedido_vai_para_a_Messages_API_com_os_cabecalhos_oficiais_e_a_credencial_so_no_cabecalho_da_requisicao()
    {
        var (provider, handler, http) = Requests.Provider();

        var resultado = await provider.CompleteAsync(Requests.Request(), default);

        Assert.True(resultado.IsSuccess);
        var pedido = Assert.Single(handler.Requests);
        Assert.Equal((HttpMethod.Post, "https://api.anthropic.com/v1/messages"), (pedido.Method, pedido.Uri.ToString()));
        Assert.Equal($"Bearer {Requests.Canary}", pedido.Authorization);
        Assert.Null(pedido.ApiKeyHeader);
        Assert.Equal("2026-10-21", pedido.Version);
        Assert.Equal(("application/json", "application/json"), (pedido.Accept, pedido.ContentType));

        // A credencial só viaja no cabeçalho: nunca no corpo, nem num cabeçalho padrão do cliente compartilhado.
        Assert.DoesNotContain(Requests.Canary, pedido.Body);
        Assert.Empty(http.DefaultRequestHeaders);
    }

    [Fact]
    public async Task O_modelo_a_instrucao_de_sistema_e_o_historico_vao_no_corpo_com_os_papeis_e_na_ordem()
    {
        var (provider, handler, _) = Requests.Provider();
        var mensagens = new[]
        {
            new AiMessage(AiMessageRole.User, "Quero saber sobre o exame A."),
            new AiMessage(AiMessageRole.Assistant, "Claro, o exame A é feito pela manhã."),
            new AiMessage(AiMessageRole.User, "Preciso de jejum para esse exame?"),
        };

        await provider.CompleteAsync(Requests.Request(system: "Instrução da camada base e do tenant.", messages: mensagens, model: "claude-sonnet-5-5"), default);

        var corpo = Assert.Single(handler.Requests).Json;
        Assert.Equal("claude-sonnet-5-5", (string)corpo["model"]!);
        Assert.Equal("Instrução da camada base e do tenant.", (string)corpo["system"]!);
        var lista = corpo["messages"]!.AsArray();
        Assert.Equal(new[] { "user", "assistant", "user" }, lista.Select(m => (string)m!["role"]!).ToArray());
        Assert.Equal(new[] { "Quero saber sobre o exame A.", "Claro, o exame A é feito pela manhã.", "Preciso de jejum para esse exame?" }, lista.Select(m => (string)m!["content"]!).ToArray());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(300)]
    [InlineData(4000)]
    public async Task O_teto_de_saida_vai_em_max_tokens_e_o_adaptador_nunca_envia_parametros_removidos_nem_raciocinio_nem_ferramentas(int teto)
    {
        var (provider, handler, _) = Requests.Provider();

        await provider.CompleteAsync(Requests.Request(maxOutput: teto), default);

        var corpo = Assert.Single(handler.Requests).Json.AsObject();
        Assert.Equal(teto, (int)corpo["max_tokens"]!);

        // Na versão 2026-10-21 estes parâmetros retornam HTTP 400, e o adaptador não usa raciocínio explícito, ferramentas, streaming nem prefill.
        foreach (var proibido in new[] { "temperature", "top_p", "top_k", "thinking", "tools", "tool_choice", "stream", "stop_sequences", "metadata" })
        {
            Assert.False(corpo.ContainsKey(proibido), $"O pedido não pode conter '{proibido}'.");
        }
    }

    [Fact]
    public async Task Teto_de_saida_zero_nao_envia_nada_e_nao_cobra()
    {
        var (provider, handler, _) = Requests.Provider();

        var resultado = await provider.CompleteAsync(Requests.Request(maxOutput: 0), default);

        Assert.Empty(handler.Requests);
        Assert.Equal((AiErrorKind.InvalidRequest, "max-output-tokens-too-low", AiUsageCertainty.NoneIncurred), (resultado.Error!.Kind, resultado.Error.Code, resultado.UsageCertainty));
    }

    [Theory]
    [InlineData("atendimento-rapido", "low")]
    [InlineData("ATENDIMENTO-RAPIDO", "low")]
    [InlineData("perfil-desconhecido", null)]
    public async Task O_perfil_do_modelo_define_o_esforco_e_perfil_desconhecido_nao_envia_o_campo(string perfil, string? esforco)
    {
        var (provider, handler, _) = Requests.Provider();

        await provider.CompleteAsync(Requests.Request(profile: perfil), default);

        var corpo = Assert.Single(handler.Requests).Json.AsObject();
        if (esforco is null)
        {
            Assert.False(corpo.ContainsKey("output_config"));
        }
        else
        {
            Assert.Equal(esforco, (string)corpo["output_config"]!["effort"]!);
        }
    }

    [Fact]
    public async Task Um_perfil_configurado_troca_o_esforco_e_liga_o_cache_do_prompt_de_sistema_sem_mudar_o_contrato()
    {
        var (provider, handler, _) = Requests.Provider(o => o.Profiles["cacheado"] = new AnthropicProfile { Effort = "medium", CacheSystemPrompt = true });

        await provider.CompleteAsync(Requests.Request(profile: "cacheado", system: "Prompt estático."), default);

        var corpo = Assert.Single(handler.Requests).Json;
        Assert.Equal("medium", (string)corpo["output_config"]!["effort"]!);
        var bloco = Assert.Single(corpo["system"]!.AsArray());
        Assert.Equal(("text", "Prompt estático.", "ephemeral"), ((string)bloco!["type"]!, (string)bloco["text"]!, (string)bloco["cache_control"]!["type"]!));
    }

    [Fact]
    public async Task Sem_o_perfil_de_cache_o_prompt_de_sistema_vai_como_texto_simples_sem_cache_control()
    {
        var (provider, handler, _) = Requests.Provider();

        await provider.CompleteAsync(Requests.Request(), default);

        var sistema = Assert.Single(handler.Requests).Json["system"]!;
        Assert.Equal(System.Text.Json.JsonValueKind.String, sistema.GetValueKind());
        Assert.DoesNotContain("cache_control", Assert.Single(handler.Requests).Body);
    }

    [Fact]
    public async Task Saida_estruturada_usa_output_config_format_json_schema_e_o_modo_texto_nao_envia_formato()
    {
        var (provider, handler, _) = Requests.Provider();

        await provider.CompleteAsync(Requests.Request(mode: AiOutputMode.StructuredJson), default);
        await provider.CompleteAsync(Requests.Request(), default);

        var estruturado = handler.Requests[0].Json["output_config"]!;
        Assert.Equal("json_schema", (string)estruturado["format"]!["type"]!);
        Assert.Equal("low", (string)estruturado["effort"]!);
        Assert.Equal("object", (string)estruturado["format"]!["schema"]!["type"]!);
        Assert.False((bool)estruturado["format"]!["schema"]!["additionalProperties"]!);
        Assert.Null(handler.Requests[1].Json["output_config"]!["format"]);
    }

    [Fact]
    public async Task Substituto_UTF16_solto_no_texto_vira_U_FFFD_e_a_chamada_segue_sem_excecao()
    {
        var (provider, handler, _) = Requests.Provider();
        var quebrado = "texto com \uD83D solto";

        var resultado = await provider.CompleteAsync(Requests.Request(system: quebrado, messages: new[] { new AiMessage(AiMessageRole.User, quebrado) }), default);

        Assert.True(resultado.IsSuccess);
        Assert.Contains("texto com � solto", (string)Assert.Single(handler.Requests).Json["messages"]![0]!["content"]!);
    }

    [Theory]
    [InlineData("")]
    [InlineData("chave com espaço")]
    [InlineData("chave\r\nX-Injetado: 1")]
    [InlineData("chave-com-acento-é")]
    public async Task Credencial_fora_do_formato_de_chave_nao_envia_nada_e_nao_aparece_no_resultado(string segredo)
    {
        var (provider, handler, _) = Requests.Provider();

        var resultado = await provider.CompleteAsync(Requests.Request(secret: segredo), default);

        Assert.Empty(handler.Requests);
        Assert.Equal((AiErrorKind.InvalidCredentials, "credential-invalid-format", AiUsageCertainty.NoneIncurred), (resultado.Error!.Kind, resultado.Error.Code, resultado.UsageCertainty));
        if (segredo.Length > 0)
        {
            Assert.DoesNotContain(segredo, resultado.ToString());
        }
    }

    [Fact]
    public async Task Esquema_de_saida_que_nao_e_JSON_nao_envia_nada()
    {
        var (provider, handler, _) = Requests.Provider();
        var pedido = Requests.Request(mode: AiOutputMode.StructuredJson) with { StructuredOutput = new AiStructuredOutput("x", "isto não é json") };

        var resultado = await provider.CompleteAsync(pedido, default);

        Assert.Empty(handler.Requests);
        Assert.Equal((AiErrorKind.InvalidRequest, "structured-output-schema-invalid", AiUsageCertainty.NoneIncurred), (resultado.Error!.Kind, resultado.Error.Code, resultado.UsageCertainty));
    }

    [Fact]
    public async Task Chamadas_simultaneas_de_tenants_diferentes_nunca_misturam_a_credencial()
    {
        var (provider, handler, _) = Requests.Provider();
        handler.Behavior = async (captura, ct) =>
        {
            await Task.Delay(Random.Shared.Next(1, 20), ct);
            return FakeHandler.Json(HttpStatusCode.OK, AnthropicBodies.Ok(captura.Authorization!)); // o "modelo" devolve quem o chamou
        };

        var chamadas = Enumerable.Range(0, 40).Select(async i =>
        {
            var segredo = $"sk-ant-TENANT-{i:D2}-{Guid.NewGuid():N}";
            var resultado = await provider.CompleteAsync(Requests.Request(secret: segredo, messages: new[] { new AiMessage(AiMessageRole.User, $"pergunta do tenant {i}") }), default);
            return (segredo, resultado);
        });
        var todas = await Task.WhenAll(chamadas);

        Assert.All(todas, t => Assert.Equal($"Bearer {t.segredo}", t.resultado.Completion!.Text));
        Assert.Equal(40, handler.Requests.Count);
        foreach (var pedido in handler.Requests)
        {
            Assert.DoesNotContain("sk-ant-TENANT", pedido.Body);
        }
    }

    // A construção

    [Fact]
    public void O_adaptador_cria_o_proprio_cliente_sem_redirecionamento_com_a_vida_da_conexao_limitada_e_sem_cookie()
    {
        var opcoes = new AnthropicOptions { PooledConnectionLifetime = TimeSpan.FromMinutes(1), ConnectTimeout = TimeSpan.FromSeconds(3) };

        using var manipulador = AnthropicProvider.CreateHandler(opcoes);

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
    public void O_provedor_se_chama_anthropic_e_o_registro_de_DI_o_expoe_como_IAiProvider()
    {
        var servicos = new ServiceCollection();
        servicos.AddAnthropicProvider(o => o.Profiles["extra"] = new AnthropicProfile { Effort = "high" });
        using var raiz = servicos.BuildServiceProvider();

        var provedores = raiz.GetServices<IAiProvider>().ToList();

        Assert.Equal("anthropic", Assert.Single(provedores).ProviderId);
        Assert.IsType<AnthropicProvider>(provedores[0]);
    }

    [Fact]
    public void Configuracao_invalida_falha_no_registro_e_nao_no_meio_do_processamento()
    {
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddAnthropicProvider(o => o.BaseAddress = "http://api.exemplo.com/v1/"));
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddAnthropicProvider(o => o.BaseAddress = "https://usuario:senha@api.exemplo.com/v1/"));
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddAnthropicProvider(o => o.MaxResponseBytes = 10));
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddAnthropicProvider(o => o.ApiVersion = "ontem"));
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddAnthropicProvider(o => o.Profiles["x"] = new AnthropicProfile { Effort = "altissimo" }));

        // HTTP só em laço local (teste) é aceito.
        new ServiceCollection().AddAnthropicProvider(o => o.BaseAddress = "http://127.0.0.1:8080/v1/");
    }
}
