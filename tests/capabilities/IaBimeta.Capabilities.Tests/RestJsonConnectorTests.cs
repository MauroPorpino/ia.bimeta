using System.Text;
using System.Text.Json;
using IaBimeta.Capabilities.Contracts;
using IaBimeta.Capabilities.Contracts.Ports;
using IaBimeta.Capabilities.Domain;
using IaBimeta.Capabilities.Infrastructure.Outbound;
using IaBimeta.Capabilities.Tests.Testing;
using Microsoft.Extensions.Logging;

namespace IaBimeta.Capabilities.Tests;

/// <summary>O conector REST/JSON com um transporte simulado: montagem da chamada, autenticação, retentativas, erros estruturados e ausência de segredo nos logs.</summary>
public class RestJsonConnectorTests
{
    private const string Base = "https://erp.example.com/api";

    private readonly FakeOutbound _http = new();
    private readonly FakeClock _clock = new();
    private readonly CapturingLogger _log = new();

    private RestJsonConnector Connector(CapabilitiesOptions? options = null) =>
        new(_http, new OAuthTokenCache(_clock), options ?? new CapabilitiesOptions { RetryBaseDelayMs = 0, RetryMaxDelayMs = 0 }, _log);

    private static ConnectorConnection Connection(
        ConnectorAuthType auth = ConnectorAuthType.None,
        Dictionary<string, string>? settings = null,
        Dictionary<string, string>? secret = null,
        Dictionary<string, string>? defaults = null,
        string[]? allowedHeaders = null,
        int secretVersion = 1) =>
        new(Guid.Parse("c0000000-0000-0000-0000-00000000000c"), TestContexts.TenantA.Value, secretVersion, Base, auth, settings ?? [], defaults ?? [], allowedHeaders ?? [], 5, 4096, "/health", new ConnectorSecret(secret ?? []));

    private static ConnectorCall Call(CapabilityOperationSpec spec, object values, ConnectorConnection? conexao = null, string? idemKey = null, bool allowRetries = true, int attempts = 3, bool? isWrite = null)
    {
        var tipados = JsonSerializer.SerializeToElement(values).EnumerateObject().ToDictionary(
            p => p.Name,
            p => p.Value.ValueKind switch
            {
                JsonValueKind.Number => p.Value.TryGetInt64(out var l) ? (object)l : p.Value.GetDecimal(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => p.Value.GetString()!,
            });
        return new ConnectorCall(conexao ?? Connection(), spec, tipados, idemKey, allowRetries, isWrite ?? spec.Method != "GET", attempts);
    }

    private static CapabilityOperationSpec Get(params CapabilityParameterSpec[] extras) => new("GET", "/pedidos/{numero}", [Specs.Param("numero", CapabilityParameterType.String, CapabilityParameterLocation.Path), .. extras], [], IdempotencySupport.Unspecified);

    // ------------------------------------------------------------ montagem

    [Fact]
    public async Task Monta_caminho_consulta_e_cabecalhos_a_partir_do_molde_com_escape()
    {
        _http.Script.Add(_ => FakeOutbound.Json(200, "{}"));
        var spec = Get(Specs.Param("busca", wire: "q"), Specs.Param("filial", where: CapabilityParameterLocation.Header, wire: "X-Filial"));
        var conexao = Connection(defaults: new() { ["X-Origem"] = "ia-bimeta" }, allowedHeaders: ["X-Filial"]);

        var r = await Connector().InvokeAsync(Call(spec, new { numero = "A-1", busca = "a&b=c d/é", filial = "01" }, conexao));

        Assert.Equal(ConnectorOutcome.Success, r.Outcome);
        var req = Assert.Single(_http.Requests);
        Assert.Equal("https://erp.example.com/api/pedidos/A-1?q=a%26b%3Dc%20d%2F%C3%A9", req.Uri.AbsoluteUri);
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Null(req.Body);
        Assert.Contains(req.Headers, h => h.Key == "X-Filial" && h.Value == "01");
        Assert.Contains(req.Headers, h => h.Key == "X-Origem" && h.Value == "ia-bimeta");
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("a/b")]
    [InlineData("evil.com/x")]
    [InlineData("a?x=1#y")]
    [InlineData("%2e%2e")]
    [InlineData("..")]
    [InlineData(".")]
    public async Task Valor_de_caminho_com_separador_escape_ou_travessia_nunca_sai_mesmo_que_a_validacao_fosse_burlada(string valor)
    {
        var r = await Connector().InvokeAsync(Call(Get(), new { numero = valor }));

        Assert.Equal(ConnectorOutcome.Blocked, r.Outcome);
        Assert.Empty(_http.Requests);
    }

    [Theory]
    [InlineData("@evil.com")]
    [InlineData("user:pw@x")]
    [InlineData("a b")]
    public async Task Valor_de_caminho_com_caracteres_especiais_seguros_e_escapado_e_o_servidor_nao_muda(string valor)
    {
        _http.Script.Add(_ => FakeOutbound.Json(200, "{}"));

        await Connector().InvokeAsync(Call(Get(), new { numero = valor }));

        var req = Assert.Single(_http.Requests);
        Assert.Equal("erp.example.com", req.Uri.Host);
        Assert.StartsWith("/api/pedidos/", req.Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal(string.Empty, req.Uri.UserInfo);
    }

    [Fact]
    public async Task Corpo_json_e_tipado_e_aceita_caminho_aninhado()
    {
        _http.Script.Add(_ => FakeOutbound.Json(200, "{}"));
        var spec = new CapabilityOperationSpec(
            "POST", "/agendamentos",
            [Specs.Param("doc", where: CapabilityParameterLocation.Body, wire: "paciente.documento"), Specs.Param("nome", where: CapabilityParameterLocation.Body, wire: "paciente.nome"), Specs.Param("qtd", CapabilityParameterType.Integer, CapabilityParameterLocation.Body), Specs.Param("urgente", CapabilityParameterType.Boolean, CapabilityParameterLocation.Body)],
            [], IdempotencySupport.None);

        await Connector().InvokeAsync(Call(spec, new { doc = "123", nome = "Maria \"A\"", qtd = 3, urgente = true }, allowRetries: false));

        var req = Assert.Single(_http.Requests);
        var corpo = JsonDocument.Parse(req.Body!).RootElement;
        Assert.Equal("123", corpo.GetProperty("paciente").GetProperty("documento").GetString());
        Assert.Equal("Maria \"A\"", corpo.GetProperty("paciente").GetProperty("nome").GetString());
        Assert.Equal(3, corpo.GetProperty("qtd").GetInt32());
        Assert.True(corpo.GetProperty("urgente").GetBoolean());
        Assert.Equal("application/json", req.ContentType);
    }

    [Fact]
    public async Task Cabecalho_de_parametro_fora_dos_permitidos_ou_reservado_nao_sai_e_nada_e_enviado()
    {
        var spec = Get(Specs.Param("h", where: CapabilityParameterLocation.Header, wire: "X-Qualquer"));

        var r = await Connector().InvokeAsync(Call(spec, new { numero = "1", h = "v" }, Connection(allowedHeaders: [])));
        Assert.Equal(ConnectorOutcome.Blocked, r.Outcome);

        var reservado = Get(Specs.Param("h", where: CapabilityParameterLocation.Header, wire: "Authorization"));
        r = await Connector().InvokeAsync(Call(reservado, new { numero = "1", h = "Bearer roubado" }, Connection(allowedHeaders: ["Authorization"])));
        Assert.Equal(ConnectorOutcome.Blocked, r.Outcome);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task Cabecalho_de_idempotencia_so_vai_em_escrita_e_com_a_chave()
    {
        _http.Script.Add(_ => FakeOutbound.Json(200, "{}"));

        await Connector().InvokeAsync(Call(Specs.Write(), new { numero = 7, motivo = "x" }, idemKey: "chave-123456"));

        var req = Assert.Single(_http.Requests);
        Assert.Equal("https://erp.example.com/api/pedidos/7/cancelar", req.Uri.AbsoluteUri);
        Assert.Contains(req.Headers, h => h.Key == "Idempotency-Key" && h.Value == "chave-123456");
    }

    // ------------------------------------------------------------ autenticação

    [Fact]
    public async Task ApiKey_vai_no_cabecalho_com_prefixo_ou_na_consulta()
    {
        _http.Script.Add(_ => FakeOutbound.Json(200, "{}"));
        var spec = Get();

        await Connector().InvokeAsync(Call(spec, new { numero = "1" }, Connection(ConnectorAuthType.ApiKey, new() { ["location"] = "header", ["name"] = "Authorization", ["prefix"] = "Bearer " }, new() { ["apiKey"] = "K-SECRETA" })));
        await Connector().InvokeAsync(Call(spec, new { numero = "1" }, Connection(ConnectorAuthType.ApiKey, new() { ["location"] = "query", ["name"] = "api_key" }, new() { ["apiKey"] = "K&2" })));

        Assert.Contains(_http.Requests[0].Headers, h => h.Key == "Authorization" && h.Value == "Bearer K-SECRETA");
        Assert.Equal("https://erp.example.com/api/pedidos/1?api_key=K%262", _http.Requests[1].Uri.AbsoluteUri);
    }

    [Fact]
    public async Task Basic_envia_usuario_e_senha_em_base64()
    {
        _http.Script.Add(_ => FakeOutbound.Json(200, "{}"));

        await Connector().InvokeAsync(Call(Get(), new { numero = "1" }, Connection(ConnectorAuthType.Basic, secret: new() { ["username"] = "joao", ["password"] = "p:w" })));

        Assert.Contains(_http.Requests[0].Headers, h => h.Key == "Authorization" && h.Value == "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("joao:p:w")));
    }

    [Fact]
    public async Task OAuth_busca_o_token_uma_vez_guarda_em_memoria_e_renova_quando_vence()
    {
        var chamadasDeToken = 0;
        _http.ForUri = req =>
        {
            if (req.Uri.AbsoluteUri == "https://auth.example.com/oauth/token")
            {
                chamadasDeToken++;
                return FakeOutbound.Json(200, $$"""{"access_token":"TOKEN-{{chamadasDeToken}}","expires_in":300}""");
            }

            return FakeOutbound.Json(200, "{}");
        };
        var conexao = Connection(ConnectorAuthType.OAuth2ClientCredentials, new() { ["tokenUrl"] = "https://auth.example.com/oauth/token", ["scope"] = "leitura" }, new() { ["clientId"] = "cid", ["clientSecret"] = "csec" });
        var conector = Connector();

        await conector.InvokeAsync(Call(Get(), new { numero = "1" }, conexao));
        await conector.InvokeAsync(Call(Get(), new { numero = "2" }, conexao));

        Assert.Equal(1, chamadasDeToken);
        var pedidoDoToken = _http.Requests[0];
        Assert.Equal(HttpMethod.Post, pedidoDoToken.Method);
        Assert.Equal("grant_type=client_credentials&client_id=cid&client_secret=csec&scope=leitura", Encoding.UTF8.GetString(pedidoDoToken.Body!));
        Assert.Equal("application/x-www-form-urlencoded", pedidoDoToken.ContentType);
        Assert.Contains(_http.Requests[1].Headers, h => h.Key == "Authorization" && h.Value == "Bearer TOKEN-1");

        _clock.Advance(TimeSpan.FromSeconds(290));
        await conector.InvokeAsync(Call(Get(), new { numero = "3" }, conexao));
        Assert.Equal(2, chamadasDeToken);
        Assert.Contains(_http.Requests.Last().Headers, h => h.Key == "Authorization" && h.Value == "Bearer TOKEN-2");
    }

    [Fact]
    public async Task OAuth_o_token_de_uma_versao_de_segredo_nao_serve_a_outra()
    {
        var chamadas = 0;
        _http.ForUri = req => req.Uri.Host == "auth.example.com" ? FakeOutbound.Json(200, $$"""{"access_token":"T{{++chamadas}}","expires_in":300}""") : FakeOutbound.Json(200, "{}");
        var settings = new Dictionary<string, string> { ["tokenUrl"] = "https://auth.example.com/t" };
        var segredo = new Dictionary<string, string> { ["clientId"] = "a", ["clientSecret"] = "b" };
        var conector = Connector();

        await conector.InvokeAsync(Call(Get(), new { numero = "1" }, Connection(ConnectorAuthType.OAuth2ClientCredentials, settings, segredo, secretVersion: 1)));
        await conector.InvokeAsync(Call(Get(), new { numero = "1" }, Connection(ConnectorAuthType.OAuth2ClientCredentials, settings, segredo, secretVersion: 2)));

        Assert.Equal(2, chamadas);
    }

    [Fact]
    public async Task OAuth_com_401_renova_o_token_uma_vez_e_repete_sem_efeito_duplicado()
    {
        var tokens = 0;
        _http.ForUri = req =>
        {
            if (req.Uri.Host == "auth.example.com")
            {
                return FakeOutbound.Json(200, $$"""{"access_token":"T{{++tokens}}","expires_in":300}""");
            }

            return req.Headers.Any(h => h.Value == "Bearer T2") ? FakeOutbound.Json(200, "{}") : FakeOutbound.Json(401, "{}");
        };
        var conexao = Connection(ConnectorAuthType.OAuth2ClientCredentials, new() { ["tokenUrl"] = "https://auth.example.com/t" }, new() { ["clientId"] = "a", ["clientSecret"] = "b" });

        var r = await Connector().InvokeAsync(Call(Get(), new { numero = "1" }, conexao, allowRetries: false));

        Assert.Equal(ConnectorOutcome.Success, r.Outcome);
        Assert.Equal(2, tokens);
    }

    [Fact]
    public async Task OAuth_falha_ao_obter_o_token_vira_erro_de_autenticacao_sem_chamar_a_operacao()
    {
        _http.ForUri = _ => FakeOutbound.Json(400, """{"error":"invalid_client","error_description":"cliente csec invalido"}""");
        var conexao = Connection(ConnectorAuthType.OAuth2ClientCredentials, new() { ["tokenUrl"] = "https://auth.example.com/t" }, new() { ["clientId"] = "a", ["clientSecret"] = "csec" });

        var r = await Connector().InvokeAsync(Call(Get(), new { numero = "1" }, conexao));

        Assert.Equal(ConnectorOutcome.AuthFailed, r.Outcome);
        Assert.Single(_http.Requests);
        Assert.False(r.MayHaveExecuted);
    }

    [Fact]
    public async Task Segredo_ausente_nao_tenta_a_chamada()
    {
        var r = await Connector().InvokeAsync(Call(Get(), new { numero = "1" }, Connection(ConnectorAuthType.ApiKey, new() { ["name"] = "X-Key" }, secret: [])));

        Assert.Equal(ConnectorOutcome.SecretUnavailable, r.Outcome);
        Assert.Empty(_http.Requests);
    }

    // ------------------------------------------------------------ retentativas

    [Fact]
    public async Task Leitura_repete_503_ate_o_limite_e_tem_sucesso()
    {
        _http.Script.Add(_ => FakeOutbound.Json(503, "{}"));
        _http.Script.Add(_ => FakeOutbound.Json(429, "{}", TimeSpan.FromMilliseconds(10)));
        _http.Script.Add(_ => FakeOutbound.Json(200, """{"ok":true}"""));

        var r = await Connector().InvokeAsync(Call(Get(), new { numero = "1" }, attempts: 3));

        Assert.Equal(ConnectorOutcome.Success, r.Outcome);
        Assert.Equal(3, r.Attempts);
        Assert.Equal(3, _http.Requests.Count);
    }

    [Fact]
    public async Task Leitura_respeita_o_maximo_de_tentativas()
    {
        _http.Script.Add(_ => FakeOutbound.Json(503, "{}"));

        var r = await Connector().InvokeAsync(Call(Get(), new { numero = "1" }, attempts: 2));

        Assert.Equal(ConnectorOutcome.Unavailable, r.Outcome);
        Assert.Equal(503, r.UpstreamStatus);
        Assert.Equal(2, _http.Requests.Count);
    }

    [Fact]
    public async Task Sem_permissao_de_repetir_sai_uma_unica_vez_mesmo_com_varias_tentativas_pedidas()
    {
        _http.Script.Add(_ => FakeOutbound.Json(503, "{}"));

        var r = await Connector().InvokeAsync(Call(Specs.Write(IdempotencySupport.None) with { MaxAttempts = 1 }, new { numero = 1, motivo = "x" }, idemKey: "chave-123456", allowRetries: false, attempts: 3));

        Assert.Equal(ConnectorOutcome.Unavailable, r.Outcome);
        Assert.True(r.MayHaveExecuted);
        Assert.Single(_http.Requests);
    }

    [Fact]
    public async Task Escrita_comprovada_repete_com_a_mesma_chave_de_idempotencia()
    {
        _http.Script.Add(_ => FakeOutbound.Json(502, "{}"));
        _http.Script.Add(_ => FakeOutbound.Json(200, """{"protocolo":"P1"}"""));

        var r = await Connector().InvokeAsync(Call(Specs.Write(attempts: 3), new { numero = 1, motivo = "x" }, idemKey: "chave-123456", allowRetries: true, attempts: 3));

        Assert.Equal(ConnectorOutcome.Success, r.Outcome);
        Assert.Equal(2, _http.Requests.Count);
        Assert.All(_http.Requests, q => Assert.Contains(q.Headers, h => h.Key == "Idempotency-Key" && h.Value == "chave-123456"));
    }

    [Fact]
    public async Task Timeout_e_marcado_como_possivelmente_executado_e_erro_de_conexao_nao()
    {
        _http.ForUri = _ => throw new OutboundTimeoutException();
        var r = await Connector().InvokeAsync(Call(Get(), new { numero = "1" }, allowRetries: false));
        Assert.Equal(ConnectorOutcome.Timeout, r.Outcome);
        Assert.True(r.MayHaveExecuted);

        _http.ForUri = _ => throw new OutboundNetworkException(connectStage: true);
        r = await Connector().InvokeAsync(Call(Get(), new { numero = "1" }, allowRetries: false));
        Assert.Equal(ConnectorOutcome.NetworkError, r.Outcome);
        Assert.False(r.MayHaveExecuted);

        _http.ForUri = _ => throw new OutboundNetworkException(connectStage: false);
        r = await Connector().InvokeAsync(Call(Get(), new { numero = "1" }, allowRetries: false));
        Assert.True(r.MayHaveExecuted);
    }

    [Theory]
    [InlineData(400, ConnectorOutcome.Rejected)]
    [InlineData(404, ConnectorOutcome.Rejected)]
    [InlineData(409, ConnectorOutcome.Rejected)]
    [InlineData(422, ConnectorOutcome.Rejected)]
    [InlineData(401, ConnectorOutcome.AuthFailed)]
    [InlineData(403, ConnectorOutcome.AuthFailed)]
    [InlineData(500, ConnectorOutcome.Unavailable)]
    [InlineData(504, ConnectorOutcome.Unavailable)]
    [InlineData(408, ConnectorOutcome.Unavailable)]
    public async Task Status_http_vira_desfecho_estruturado_e_4xx_definitivo_nunca_e_repetido(int status, ConnectorOutcome esperado)
    {
        _http.Script.Add(_ => FakeOutbound.Json(status, """{"detalhe":"texto do sistema externo que nao deve sair"}"""));

        var r = await Connector().InvokeAsync(Call(Get(), new { numero = "1" }, attempts: 3));

        Assert.Equal(esperado, r.Outcome);
        Assert.Equal(status, r.UpstreamStatus);
        Assert.Null(r.Body);
        if (esperado is ConnectorOutcome.Rejected or ConnectorOutcome.AuthFailed)
        {
            Assert.Single(_http.Requests);
            Assert.False(r.MayHaveExecuted);
        }
    }

    // ------------------------------------------------------------ resposta

    [Fact]
    public async Task Redirecionamento_nunca_e_seguido()
    {
        _http.Script.Add(_ => new OutboundResponse(302, "text/html", [], null));

        var r = await Connector().InvokeAsync(Call(Get(), new { numero = "1" }, allowRetries: true));

        Assert.Equal(ConnectorOutcome.Redirected, r.Outcome);
        Assert.Single(_http.Requests);
    }

    [Fact]
    public async Task Resposta_que_nao_e_json_valido_ou_e_muito_profunda_vira_resposta_invalida()
    {
        _http.Script.Add(_ => new OutboundResponse(200, "text/html", Encoding.UTF8.GetBytes("<html>"), null));
        Assert.Equal(ConnectorOutcome.InvalidResponse, (await Connector().InvokeAsync(Call(Get(), new { numero = "1" }))).Outcome);

        _http.Script[0] = _ => FakeOutbound.Json(200, "{nao-e-json");
        Assert.Equal(ConnectorOutcome.InvalidResponse, (await Connector().InvokeAsync(Call(Get(), new { numero = "1" }))).Outcome);

        _http.Script[0] = _ => FakeOutbound.Json(200, string.Concat(Enumerable.Repeat("[", 100)) + string.Concat(Enumerable.Repeat("]", 100)));
        Assert.Equal(ConnectorOutcome.InvalidResponse, (await Connector().InvokeAsync(Call(Get(), new { numero = "1" }))).Outcome);
    }

    [Fact]
    public async Task Json_com_sufixo_e_corpo_vazio_sao_aceitos()
    {
        _http.Script.Add(_ => new OutboundResponse(200, "application/vnd.erp+json", Encoding.UTF8.GetBytes("""{"a":1}"""), null));
        var r = await Connector().InvokeAsync(Call(Get(), new { numero = "1" }));
        Assert.Equal(1, r.Body!.Value.GetProperty("a").GetInt32());

        _http.Script[0] = _ => new OutboundResponse(204, null, [], null);
        r = await Connector().InvokeAsync(Call(Get(), new { numero = "1" }));
        Assert.Equal(ConnectorOutcome.Success, r.Outcome);
        Assert.Null(r.Body);
    }

    [Fact]
    public async Task Excecoes_do_transporte_viram_desfechos_estruturados_e_nunca_vazam()
    {
        _http.ForUri = _ => throw new OutboundBlockedException("private-address");
        Assert.Equal(ConnectorOutcome.Blocked, (await Connector().InvokeAsync(Call(Get(), new { numero = "1" }))).Outcome);

        _http.ForUri = _ => throw new OutboundTooLargeException();
        Assert.Equal(ConnectorOutcome.TooLarge, (await Connector().InvokeAsync(Call(Get(), new { numero = "1" }))).Outcome);
    }

    [Fact]
    public async Task Cancelamento_do_pedido_interrompe_sem_nova_tentativa()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Connector().InvokeAsync(Call(Get(), new { numero = "1" }), cts.Token));
        Assert.Empty(_http.Requests);
    }

    // ------------------------------------------------------------ sonda e logs

    [Fact]
    public async Task A_sonda_chama_o_caminho_de_saude_com_a_autenticacao_e_nao_exige_json()
    {
        _http.Script.Add(_ => new OutboundResponse(200, "text/plain", Encoding.UTF8.GetBytes("OK"), null));

        var r = await Connector().ProbeAsync(Connection(ConnectorAuthType.ApiKey, new() { ["name"] = "X-Key" }, new() { ["apiKey"] = "K" }));

        Assert.Equal(ConnectorOutcome.Success, r.Outcome);
        Assert.Equal("https://erp.example.com/api/health", _http.Requests[0].Uri.AbsoluteUri);
        Assert.Contains(_http.Requests[0].Headers, h => h.Key == "X-Key");

        _http.Script[0] = _ => FakeOutbound.Json(404, "{}");
        Assert.Equal(ConnectorOutcome.Rejected, (await Connector().ProbeAsync(Connection())).Outcome);
        _http.Script[0] = _ => FakeOutbound.Json(401, "{}");
        Assert.Equal(ConnectorOutcome.AuthFailed, (await Connector().ProbeAsync(Connection())).Outcome);
    }

    [Fact]
    public async Task Os_logs_nunca_levam_segredo_parametro_nem_corpo()
    {
        _http.Script.Add(_ => FakeOutbound.Json(200, """{"cpf":"98765432100","token":"TOKEN-DA-RESPOSTA"}"""));
        var conexao = Connection(ConnectorAuthType.ApiKey, new() { ["name"] = "X-Key" }, new() { ["apiKey"] = "CHAVE-SECRETISSIMA" });

        await Connector().InvokeAsync(Call(Get(Specs.Param("busca")), new { numero = "12345678901", busca = "Maria da Silva" }, conexao));

        Assert.NotEmpty(_log.Messages);
        var tudo = string.Join('\n', _log.Messages);
        foreach (var proibido in new[] { "CHAVE-SECRETISSIMA", "12345678901", "Maria da Silva", "98765432100", "TOKEN-DA-RESPOSTA", "erp.example.com/api/pedidos" })
        {
            Assert.DoesNotContain(proibido, tudo, StringComparison.Ordinal);
        }
    }

    private sealed class CapturingLogger : ILogger<RestJsonConnector>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception) + (exception?.ToString() ?? string.Empty));
    }
}
