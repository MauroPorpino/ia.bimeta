using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using IaBimeta.Adapters.WhatsApp.Testing;
using IaBimeta.Channels.Contracts.Providers;

namespace IaBimeta.Adapters.WhatsApp;

/// <summary>
/// O download de uma mídia recebida (ADR 0028): duas chamadas (os dados da mídia pelo identificador e o conteúdo no endereço devolvido), o token só nos cabeçalhos e só para um endereço do provedor, tamanho limitado,
/// sem redirecionamento e com o desfecho vindo do estado HTTP, nunca de texto do provedor. HTTP simulado: nenhuma rede e nenhum token real.
/// </summary>
public sealed class MediaDownloadTests
{
    private const string MediaId = "1182273660431264";
    private const string ContentUrl = "https://lookaside.fbsbx.com/whatsapp_business/attachments/?mid=1182273660431264&ext=1762000000&hash=ATsecreto";

    private static readonly byte[] Arquivo = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5, 6, 7, 8];

    private static ProviderMediaRequest Request(long max = 1024, string token = Sends.Canary, string id = MediaId) => new(token, id, max);

    private static string Lookup(string url = ContentUrl, string? mime = "image/png", string? sha = "abc123", object? size = null)
    {
        var json = new JsonObject { ["messaging_product"] = "whatsapp", ["id"] = MediaId, ["url"] = url };
        if (mime is not null)
        {
            json["mime_type"] = mime;
        }

        if (sha is not null)
        {
            json["sha256"] = sha;
        }

        json["file_size"] = size switch
        {
            null => Arquivo.Length,
            string texto => texto,
            long numero => numero,
            int numero => numero,
            _ => throw new ArgumentException("tipo"),
        };
        return json.ToJsonString();
    }

    private static HttpResponseMessage Bytes(byte[] conteudo, string tipo = "application/octet-stream")
    {
        var resposta = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(conteudo) };
        resposta.Content.Headers.ContentType = new MediaTypeHeaderValue(tipo);
        return resposta;
    }

    /// <summary>A Graph API simulada: a consulta (caminho com a versão) devolve o JSON dos dados; qualquer outro endereço é o do conteúdo.</summary>
    private static (WhatsAppCloudAdapter Adapter, FakeHandler Handler) Servidor(
        Func<Captured, HttpResponseMessage>? lookup = null, Func<Captured, HttpResponseMessage>? conteudo = null, Action<WhatsAppOptions>? configure = null)
    {
        var (adaptador, handler, _) = Sends.Adapter(configure);
        handler.Behavior = (pedido, _) => Task.FromResult(pedido.Uri.AbsolutePath.StartsWith("/v26.0/", StringComparison.Ordinal)
            ? (lookup ?? (_ => FakeHandler.Json(HttpStatusCode.OK, Lookup())))(pedido)
            : (conteudo ?? (_ => Bytes(Arquivo)))(pedido));
        return (adaptador, handler);
    }

    [Fact]
    public async Task Baixa_em_duas_chamadas_GET_com_o_token_so_no_cabecalho_e_devolve_o_arquivo_com_o_tipo_e_o_resumo_do_provedor()
    {
        var (adaptador, handler) = Servidor();

        var resultado = await adaptador.DownloadMediaAsync(Request(), default);

        Assert.Equal(ProviderMediaOutcome.Downloaded, resultado.Outcome);
        Assert.Equal(Arquivo, resultado.Content);
        Assert.Equal("image/png", resultado.MimeType);
        Assert.Equal("abc123", resultado.Sha256);
        Assert.Null(resultado.ErrorCode);

        Assert.Equal(2, handler.Requests.Count);
        var consulta = handler.Requests[0];
        Assert.Equal(HttpMethod.Get, consulta.Method);
        Assert.Equal($"/v26.0/{MediaId}", consulta.Uri.AbsolutePath);
        Assert.Equal(string.Empty, consulta.Uri.Query);
        Assert.Equal("application/json", consulta.Accept);

        var conteudo = handler.Requests[1];
        Assert.Equal(HttpMethod.Get, conteudo.Method);
        Assert.Equal(new Uri(ContentUrl), conteudo.Uri);

        // O token vai SÓ no cabeçalho das duas chamadas, nunca na URL nem no resultado.
        Assert.All(handler.Requests, r =>
        {
            Assert.Equal($"Bearer {Sends.Canary}", r.Authorization);
            Assert.DoesNotContain(Sends.Canary, r.Uri.ToString(), StringComparison.Ordinal);
            Assert.Equal(string.Empty, r.Body);
        });
        Assert.DoesNotContain(Sends.Canary, resultado.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sem_tipo_nos_dados_vale_o_tipo_do_cabecalho_do_conteudo()
    {
        var (adaptador, _) = Servidor(lookup: _ => FakeHandler.Json(HttpStatusCode.OK, Lookup(mime: null, sha: null)), conteudo: _ => Bytes(Arquivo, "image/png"));

        var resultado = await adaptador.DownloadMediaAsync(Request(), default);

        Assert.Equal(ProviderMediaOutcome.Downloaded, resultado.Outcome);
        Assert.Equal("image/png", resultado.MimeType);
        Assert.Null(resultado.Sha256);
    }

    [Fact]
    public async Task O_tamanho_pode_vir_como_texto_de_digitos_e_acima_do_limite_nem_baixa_o_conteudo()
    {
        var (adaptador, handler) = Servidor(lookup: _ => FakeHandler.Json(HttpStatusCode.OK, Lookup(size: "2048")));

        var resultado = await adaptador.DownloadMediaAsync(Request(max: 1024), default);

        Assert.Equal(ProviderMediaOutcome.TooLarge, resultado.Outcome);
        Assert.Equal("too-large", resultado.ErrorCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task O_Content_Length_acima_do_limite_recusa_antes_de_ler_o_corpo()
    {
        var (adaptador, _) = Servidor(conteudo: _ => Bytes(new byte[2000]));

        var resultado = await adaptador.DownloadMediaAsync(Request(max: 1024), default);

        Assert.Equal(ProviderMediaOutcome.TooLarge, resultado.Outcome);
        Assert.Null(resultado.Content);
    }

    [Fact]
    public async Task Sem_Content_Length_o_excesso_e_medido_na_leitura_e_nunca_entra_inteiro_na_memoria()
    {
        var grande = new byte[5000];
        var (adaptador, _) = Servidor(conteudo: _ =>
        {
            var resposta = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ForwardOnlyStream(grande)) };
            resposta.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return resposta;
        });

        var resultado = await adaptador.DownloadMediaAsync(Request(max: 1024), default);

        Assert.Equal(ProviderMediaOutcome.TooLarge, resultado.Outcome);
        Assert.Null(resultado.Content);
    }

    [Fact]
    public async Task Exatamente_o_limite_e_aceito()
    {
        var (adaptador, _) = Servidor(lookup: _ => FakeHandler.Json(HttpStatusCode.OK, Lookup(size: Arquivo.Length)));

        var resultado = await adaptador.DownloadMediaAsync(Request(max: Arquivo.Length), default);

        Assert.Equal(ProviderMediaOutcome.Downloaded, resultado.Outcome);
        Assert.Equal(Arquivo.Length, resultado.Content!.Length);
    }

    [Theory]
    [InlineData("https://evil.example.com/arquivo")]
    [InlineData("http://lookaside.fbsbx.com/arquivo")]
    [InlineData("https://lookaside.fbsbx.com.evil.com/arquivo")]
    [InlineData("https://evilfbsbx.com/arquivo")]
    [InlineData("https://203.0.113.7/arquivo")]
    [InlineData("https://127.0.0.1/arquivo")]
    [InlineData("https://[::1]/arquivo")]
    [InlineData("https://usuario:senha@lookaside.fbsbx.com/arquivo")]
    [InlineData("https://lookaside.fbsbx.com:8443/arquivo")]
    [InlineData("https://lookaside.fbsbx.com/arquivo#fragmento")]
    [InlineData("ftp://lookaside.fbsbx.com/arquivo")]
    [InlineData("file:///c:/segredo.txt")]
    public async Task O_endereco_do_conteudo_que_nao_e_do_provedor_nunca_recebe_o_token(string endereco)
    {
        var (adaptador, handler) = Servidor(lookup: _ => FakeHandler.Json(HttpStatusCode.OK, Lookup(url: endereco)));

        var resultado = await adaptador.DownloadMediaAsync(Request(), default);

        Assert.Equal(ProviderMediaOutcome.Rejected, resultado.Outcome);
        Assert.Equal("media-url-not-allowed", resultado.ErrorCode);
        Assert.Null(resultado.Content);

        // Só a consulta ao provedor foi feita: o segundo pedido (com o token) nunca saiu.
        Assert.Single(handler.Requests);
        Assert.DoesNotContain(handler.Requests, r => r.Uri.ToString().Contains("evil", StringComparison.Ordinal) || r.Uri.Scheme != Uri.UriSchemeHttps);
    }

    [Theory]
    [InlineData("https://lookaside.fbsbx.com/arquivo")]
    [InlineData("https://scontent.whatsapp.net/v/t61/arquivo")]
    [InlineData("https://mmg.whatsapp.net/arquivo")]
    [InlineData("https://FBSBX.com/arquivo")]
    public async Task Dominios_da_Meta_sao_seguidos(string endereco)
    {
        var (adaptador, handler) = Servidor(lookup: _ => FakeHandler.Json(HttpStatusCode.OK, Lookup(url: endereco)));

        var resultado = await adaptador.DownloadMediaAsync(Request(), default);

        Assert.Equal(ProviderMediaOutcome.Downloaded, resultado.Outcome);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task A_lista_de_dominios_e_configuravel_e_a_configurada_substitui_a_padrao()
    {
        var (adaptador, handler) = Servidor(
            lookup: _ => FakeHandler.Json(HttpStatusCode.OK, Lookup(url: "https://midia.exemplo.com.br/a")),
            configure: o => o.MediaHostSuffixes = ["exemplo.com.br"]);

        Assert.Equal(ProviderMediaOutcome.Downloaded, (await adaptador.DownloadMediaAsync(Request(), default)).Outcome);

        // O domínio padrão da Meta deixou de valer.
        var (outro, _) = Servidor(lookup: _ => FakeHandler.Json(HttpStatusCode.OK, Lookup()), configure: o => o.MediaHostSuffixes = ["exemplo.com.br"]);
        Assert.Equal("media-url-not-allowed", (await outro.DownloadMediaAsync(Request(), default)).ErrorCode);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task O_endereco_da_propria_Graph_API_configurada_e_aceito_inclusive_no_laco_local_dos_testes()
    {
        var (adaptador, handler) = Servidor(
            lookup: _ => FakeHandler.Json(HttpStatusCode.OK, Lookup(url: "http://127.0.0.1:5999/media-content/1")),
            configure: o => o.BaseAddress = "http://127.0.0.1:5999/");

        Assert.Equal(ProviderMediaOutcome.Downloaded, (await adaptador.DownloadMediaAsync(Request(), default)).Outcome);
        Assert.Equal(2, handler.Requests.Count);

        // Outra porta do mesmo IP não é a Graph API configurada.
        var (outro, h2) = Servidor(
            lookup: _ => FakeHandler.Json(HttpStatusCode.OK, Lookup(url: "http://127.0.0.1:5998/media-content/1")),
            configure: o => o.BaseAddress = "http://127.0.0.1:5999/");
        Assert.Equal("media-url-not-allowed", (await outro.DownloadMediaAsync(Request(), default)).ErrorCode);
        Assert.Single(h2.Requests);
    }

    [Theory]
    [InlineData(404, ProviderMediaOutcome.NotFound, "media-not-found")]
    [InlineData(410, ProviderMediaOutcome.NotFound, "media-not-found")]
    [InlineData(401, ProviderMediaOutcome.AccountError, "credential-rejected")]
    [InlineData(403, ProviderMediaOutcome.AccountError, "credential-rejected")]
    [InlineData(429, ProviderMediaOutcome.Transient, "rate-limited")]
    [InlineData(408, ProviderMediaOutcome.Transient, "timeout")]
    [InlineData(500, ProviderMediaOutcome.Transient, "provider-error")]
    [InlineData(503, ProviderMediaOutcome.Transient, "provider-error")]
    [InlineData(302, ProviderMediaOutcome.Rejected, "unexpected-redirect")]
    [InlineData(418, ProviderMediaOutcome.Rejected, "http-lookup-418")]
    public async Task O_desfecho_da_consulta_vem_do_estado_HTTP_e_so_o_que_prova_a_recusa_e_definitivo(int estado, ProviderMediaOutcome esperado, string codigo)
    {
        var (adaptador, handler) = Servidor(lookup: _ => FakeHandler.Json((HttpStatusCode)estado, "{\"error\":{\"message\":\"TEXTO LIVRE DO PROVEDOR\",\"code\":0}}"));

        var resultado = await adaptador.DownloadMediaAsync(Request(), default);

        Assert.Equal(esperado, resultado.Outcome);
        Assert.Equal(codigo, resultado.ErrorCode);
        Assert.Null(resultado.Content);
        Assert.Single(handler.Requests);
        Assert.DoesNotContain("LIVRE", resultado.ErrorCode, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(404, ProviderMediaOutcome.NotFound, "media-not-found")]
    [InlineData(403, ProviderMediaOutcome.AccountError, "credential-rejected")]
    [InlineData(500, ProviderMediaOutcome.Transient, "provider-error")]
    [InlineData(302, ProviderMediaOutcome.Rejected, "unexpected-redirect")]
    [InlineData(418, ProviderMediaOutcome.Rejected, "http-content-418")]
    public async Task O_desfecho_do_conteudo_vem_do_estado_HTTP_e_o_endereco_expirado_e_nao_encontrado(int estado, ProviderMediaOutcome esperado, string codigo)
    {
        var (adaptador, handler) = Servidor(conteudo: _ => new HttpResponseMessage((HttpStatusCode)estado) { Content = new StringContent("corpo livre do provedor") });

        var resultado = await adaptador.DownloadMediaAsync(Request(), default);

        Assert.Equal(esperado, resultado.Outcome);
        Assert.Equal(codigo, resultado.ErrorCode);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(190, ProviderMediaOutcome.AccountError, "credential-rejected")]
    [InlineData(100, ProviderMediaOutcome.Rejected, "provider-rejected")]
    public async Task Erro_400_com_codigo_de_credencial_e_da_conta_e_os_demais_sao_recusa_definitiva(int codigoDoProvedor, ProviderMediaOutcome esperado, string codigo)
    {
        var (adaptador, _) = Servidor(lookup: _ => FakeHandler.Json(HttpStatusCode.BadRequest, SendBodies.Error(codigoDoProvedor)));

        var resultado = await adaptador.DownloadMediaAsync(Request(), default);

        Assert.Equal(esperado, resultado.Outcome);
        Assert.Equal(codigo, resultado.ErrorCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>login</html>")]
    [InlineData("[]")]
    [InlineData("{\"id\":\"1\"}")]
    [InlineData("{\"url\":123}")]
    [InlineData("{\"url\":\"\"}")]
    public async Task Resposta_de_sucesso_sem_o_endereco_do_conteudo_e_recusa_e_nunca_um_arquivo(string corpo)
    {
        var (adaptador, handler) = Servidor(lookup: _ => FakeHandler.Json(HttpStatusCode.OK, corpo));

        var resultado = await adaptador.DownloadMediaAsync(Request(), default);

        Assert.Equal(ProviderMediaOutcome.Rejected, resultado.Outcome);
        Assert.Equal("invalid-media-response", resultado.ErrorCode);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("../1182273660431264")]
    [InlineData("1182273660431264/../../me")]
    [InlineData("1182273660431264?fields=url")]
    [InlineData("a b")]
    [InlineData("")]
    public async Task Identificador_fora_do_formato_nao_vira_pedido_nenhum_pois_entraria_no_caminho_da_URL(string id)
    {
        var (adaptador, handler) = Servidor();

        var resultado = await adaptador.DownloadMediaAsync(Request(id: id), default);

        Assert.Equal(ProviderMediaOutcome.Rejected, resultado.Outcome);
        Assert.Equal("invalid-media-id", resultado.ErrorCode);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("com espaco")]
    [InlineData("token\r\nX-Injetado: 1")]
    [InlineData("")]
    public async Task Token_fora_do_formato_nunca_vai_a_um_cabecalho_e_e_problema_da_conta(string token)
    {
        var (adaptador, handler) = Servidor();

        var resultado = await adaptador.DownloadMediaAsync(Request(token: token), default);

        Assert.Equal(ProviderMediaOutcome.AccountError, resultado.Outcome);
        Assert.Equal("credential-invalid-format", resultado.ErrorCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Limite_invalido_e_recusado_sem_chamar_o_provedor()
    {
        var (adaptador, handler) = Servidor();

        var resultado = await adaptador.DownloadMediaAsync(Request(max: 0), default);

        Assert.Equal(ProviderMediaOutcome.Rejected, resultado.Outcome);
        Assert.Equal("invalid-limit", resultado.ErrorCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Falha_de_rede_e_passageira_e_o_tempo_esgotado_tambem()
    {
        var (adaptadorRede, handlerRede, _) = Sends.Adapter();
        handlerRede.Behavior = (_, _) => throw new HttpRequestException("conexao recusada com o token EAAG-CANARIO-DO-TENANT-A-9f3a1c");

        var rede = await adaptadorRede.DownloadMediaAsync(Request(), default);

        Assert.Equal(ProviderMediaOutcome.Transient, rede.Outcome);
        Assert.Equal("network-error", rede.ErrorCode);
        Assert.DoesNotContain(Sends.Canary, rede.ErrorCode, StringComparison.Ordinal);

        var (adaptadorLento, handlerLento, _) = Sends.Adapter(o => o.MediaTimeout = TimeSpan.FromMilliseconds(150));
        handlerLento.Behavior = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        };

        var lento = await adaptadorLento.DownloadMediaAsync(Request(), default);

        Assert.Equal(ProviderMediaOutcome.Transient, lento.Outcome);
        Assert.Equal("timeout", lento.ErrorCode);
    }

    [Fact]
    public async Task O_cancelamento_do_chamador_propaga_e_nao_vira_resultado()
    {
        var (adaptador, handler, _) = Sends.Adapter();
        using var origem = new CancellationTokenSource();
        handler.Behavior = async (_, ct) =>
        {
            await origem.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adaptador.DownloadMediaAsync(Request(), origem.Token));
    }

    [Fact]
    public async Task A_configuracao_de_tempo_da_midia_e_validada_na_partida()
    {
        var opcoes = new WhatsAppOptions { MediaTimeout = TimeSpan.Zero };
        Assert.Throws<InvalidOperationException>(opcoes.Validate);

        opcoes = new WhatsAppOptions { MediaTimeout = TimeSpan.FromMinutes(11) };
        Assert.Throws<InvalidOperationException>(opcoes.Validate);

        new WhatsAppOptions().Validate();
        await Task.CompletedTask;
    }
}
