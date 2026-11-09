using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using IaBimeta.Channels.Contracts;
using IaBimeta.Channels.Contracts.Providers;

namespace IaBimeta.Adapters.WhatsApp;

/// <summary>
/// O adaptador do WhatsApp Cloud API (Meta) para a porta <see cref="IChannelProviderAdapter"/> (ADR 0011, seção B.2; BOUNDARIES §7): <c>HttpClient</c> direto na Graph API, sem SDK. Só sabe falar com o provedor:
/// não conhece tenant, sessão, fila nem banco. O App Secret e o token de acesso chegam a cada chamada, decifrados na hora; o token vai SÓ no cabeçalho daquela requisição e nada disso aparece em resultado,
/// código de erro, exceção nem log. A falha do provedor é RESULTADO, nunca exceção: só o cancelamento do chamador sai como <see cref="OperationCanceledException"/>. Não repete envio por conta própria.
/// <para>
/// <b>Estratégia de efeito: <see cref="ProviderEffectStrategy.None"/> (c).</b> A Meta não documenta chave de idempotência de envio nem consulta por identificador próprio (ADR 0011, A.2, item 11): o resultado
/// ambíguo (tempo esgotado, rede, 5xx, resposta ilegível) é <see cref="ProviderSendOutcome.Unknown"/> e nunca é reenviado automaticamente.
/// </para>
/// <para>
/// <b>Fatos oficiais (consulta somente leitura de 05/11/2026, documentação em inglês e português da Meta).</b> Fontes: <c>https://developers.facebook.com/documentation/business-messaging/whatsapp/</c> —
/// <c>webhooks/reference/messages</c> (e <c>/audio</c>, <c>/image</c>, <c>/document</c>, <c>/video</c>), <c>webhooks/reference/messages/status</c>, <c>messages/text-messages</c>,
/// <c>reference/whatsapp-business-phone-number/message-api</c>, <c>support/error-codes</c>; <c>https://developers.facebook.com/docs/graph-api/webhooks/getting-started</c> (verificação por GET e
/// <c>X-Hub-Signature-256</c>); <c>https://developers.facebook.com/docs/graph-api/changelog/versions</c> (versões).
/// </para>
/// <list type="bullet">
/// <item>Webhook: <c>object = whatsapp_business_account</c>, <c>entry[].changes[].value</c> com <c>metadata.phone_number_id</c>, <c>contacts[]</c> (<c>wa_id</c>, <c>profile.name</c>), <c>messages[]</c> (<c>from</c>, <c>id</c>,
/// <c>timestamp</c> como TEXTO em segundos Unix, <c>type</c>, <c>text.body</c>; mídia com <c>id</c>, <c>mime_type</c>, <c>sha256</c>, <c>caption</c>; o áudio de voz é <c>type = audio</c> com <c>voice = true</c>) e
/// <c>statuses[]</c> (<c>id</c>, <c>status</c>, <c>timestamp</c>, <c>recipient_id</c>, <c>biz_opaque_callback_data</c> se enviado, <c>errors[]</c> só na falha). A página de mensagens não lista todos os valores de
/// <c>type</c>: o que não é texto nem mídia conhecida vira <see cref="ProviderMessageKind.Other"/>. A lista de estados também inclui <c>played</c>, sem equivalente no contrato do MVP (descartado e contado).</item>
/// <item>Assinatura: <c>X-Hub-Signature-256: sha256=...</c>, HMAC-SHA256 do payload com o App Secret. A página NÃO detalha o tratamento de bytes e Unicode: por isso o HMAC é sobre os BYTES brutos, e há teste com
/// payload acentuado. Verificação por GET: <c>hub.mode=subscribe</c>, <c>hub.verify_token</c>, <c>hub.challenge</c>; a resposta é o valor de <c>hub.challenge</c>.</item>
/// <item>Envio: <c>POST https://graph.facebook.com/&lt;versão&gt;/&lt;phone_number_id&gt;/messages</c> com <c>Authorization: Bearer</c>; corpo com <c>messaging_product</c>, <c>recipient_type</c>, <c>to</c>, <c>type</c>,
/// <c>text.preview_url</c> e <c>text.body</c> (até 4096 caracteres); sucesso com <c>messages[0].id</c>. A página de referência consultada NÃO descreve o <c>biz_opaque_callback_data</c> no envio (só o devolve nos
/// status, ADR 0011, A.2, item 11): o campo é enviado, e se a Meta o recusar o efeito será uma falha definitiva visível no teste com o número de teste (5.2).</item>
/// <item>Erros: o corpo traz <c>error.code</c> inteiro (a Meta recomenda construir o tratamento em torno dele). Os códigos 130429, 131056, 131000, 131047, 131049, 131026, 131048, 190 e 368 são os do ADR 0011 (A.2).
/// A documentação NÃO fixa o estado HTTP de cada um (só o de alguns da Marketing Messages API): o estado só decide quando o código é desconhecido.</item>
/// <item>Versão da Graph API: a tabela oficial de versões lista <c>v26.0</c> (29/07/2026) como a mais recente, com <c>v25.0</c> (18/02/2026) ainda suportada; as páginas do WhatsApp já usam <c>v27.0</c> nos exemplos,
/// mas ela não está na tabela: o padrão é <see cref="WhatsAppOptions.DefaultGraphApiVersion"/> e a troca é configuração.</item>
/// </list>
/// </summary>
public sealed class WhatsAppCloudAdapter : IChannelProviderAdapter, IDisposable
{
    private const string SignaturePrefix = "sha256=";
    private const int SignatureHexLength = 64;
    private const int MaxChallengeLength = 256;

    private readonly HttpClient _http;
    private readonly TimeProvider _clock;
    private readonly bool _ownsClient;

    // Cópia dos valores JÁ VALIDADOS: mudar o objeto de opções depois da construção não redireciona o token da conta para outro endereço nem afrouxa os limites.
    private readonly Uri _versionedBase;
    private readonly TimeSpan _requestTimeout;
    private readonly int _maxResponseBytes;
    private readonly TimeSpan _mediaTimeout;
    private readonly string[] _mediaHostSuffixes;

    /// <param name="http">O cliente HTTP. NÃO deve ter cabeçalho padrão de autorização: o token é por requisição.</param>
    /// <param name="options">A configuração já validada.</param>
    /// <param name="clock">O relógio (a espera do <c>Retry-After</c> em data).</param>
    /// <param name="ownsClient">Verdadeiro quando o adaptador criou o cliente e deve descartá-lo.</param>
    public WhatsAppCloudAdapter(HttpClient http, WhatsAppOptions options, TimeProvider? clock = null, bool ownsClient = false)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _http = http;
        _versionedBase = options.VersionedBase;
        _requestTimeout = options.RequestTimeout;
        _maxResponseBytes = options.MaxResponseBytes;
        _mediaTimeout = options.MediaTimeout;
        _mediaHostSuffixes = (options.MediaHostSuffixes is { Count: > 0 } configurados ? configurados : WhatsAppOptions.DefaultMediaHostSuffixes.ToList())
            .Select(s => s.Trim().Trim('.').ToLowerInvariant()).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        _clock = clock ?? TimeProvider.System;
        _ownsClient = ownsClient;
    }

    public ChannelProvider Provider => ChannelProvider.WhatsAppCloud;

    public ProviderEffectStrategy EffectStrategy => ProviderEffectStrategy.None;

    /// <summary>A janela de atendimento do WhatsApp: 24 horas depois da última mensagem do cliente. Fora dela, só modelo aprovado (a Meta recusa o texto livre com o erro 131047).</summary>
    public TimeSpan? ReplyWindow => TimeSpan.FromHours(24);

    /// <summary>O adaptador com o próprio cliente HTTP: sem redirecionamento (o token nunca segue para outro endereço), sem tempo limite do cliente (cada chamada tem o dela) e com a vida da conexão limitada.</summary>
    public static WhatsAppCloudAdapter Create(WhatsAppOptions options, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var cliente = new HttpClient(CreateHandler(options)) { Timeout = Timeout.InfiniteTimeSpan };
        return new WhatsAppCloudAdapter(cliente, options, clock, ownsClient: true);
    }

    internal static SocketsHttpHandler CreateHandler(WhatsAppOptions options) => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        ConnectTimeout = options.ConnectTimeout,
        PooledConnectionLifetime = options.PooledConnectionLifetime,
        UseCookies = false,
    };

    // Assinatura, normalização e verificação do webhook

    /// <summary>
    /// O cabeçalho precisa ser exatamente <c>sha256=</c> mais 64 dígitos hexadecimais minúsculos (o HMAC-SHA256 do corpo BRUTO, em bytes, com o App Secret). A comparação é em tempo constante sobre os 32 bytes do
    /// MAC. Ausente, mal formado, segredo vazio ou diferente: falso. Nunca lança.
    /// </summary>
    public bool VerifySignature(ReadOnlySpan<byte> body, string? signatureHeader, string appSecret)
    {
        if (string.IsNullOrEmpty(signatureHeader)
            || string.IsNullOrEmpty(appSecret)
            || signatureHeader.Length != SignaturePrefix.Length + SignatureHexLength
            || !signatureHeader.StartsWith(SignaturePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        Span<byte> informada = stackalloc byte[SignatureHexLength / 2];
        if (!TryDecodeLowerHex(signatureHeader.AsSpan(SignaturePrefix.Length), informada))
        {
            return false;
        }

        var chave = Encoding.UTF8.GetBytes(appSecret);
        Span<byte> calculada = stackalloc byte[HMACSHA256.HashSizeInBytes];
        try
        {
            HMACSHA256.HashData(chave, body, calculada);
            return CryptographicOperations.FixedTimeEquals(calculada, informada);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(chave);
        }
    }

    public ParsedWebhook? Parse(ReadOnlySpan<byte> body) => WhatsAppWebhookParser.Parse(body);

    /// <summary>
    /// O GET de verificação: verdadeiro, com o <c>hub.challenge</c> a devolver, só se o modo é <c>subscribe</c> e o token confere (comparação em tempo constante). O desafio é devolvido por um endpoint público: precisa
    /// ser texto curto, visível e sem controle (nunca é refletido o que não parece um desafio).
    /// </summary>
    public bool TryAnswerVerification(string? mode, string? verifyToken, string? challenge, string expectedVerifyToken, out string? response)
    {
        response = null;
        if (!string.Equals(mode, "subscribe", StringComparison.Ordinal)
            || string.IsNullOrEmpty(expectedVerifyToken)
            || verifyToken is null
            || !FixedTimeTextEquals(verifyToken, expectedVerifyToken)
            || !IsSafeChallenge(challenge))
        {
            return false;
        }

        response = challenge;
        return true;
    }

    // Envio

    public Task<ProviderSendResult> SendTextAsync(ProviderSendRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return DispatchAsync(WhatsAppSendRequestFactory.Create(request, _versionedBase, out var recusa), recusa, cancellationToken);
    }

    /// <summary>
    /// O envio de um MODELO de mensagem aprovado (<c>type: "template"</c>), o único que a Meta aceita fora da janela de 24 horas. Mesmo canal HTTP, mesmo token só no cabeçalho do pedido, mesma leitura da resposta e mesma
    /// regra de dúvida do texto livre: o que não é recusa inequívoca do provedor é <see cref="ProviderSendOutcome.Unknown"/> e nunca é reenviado.
    /// </summary>
    public Task<ProviderSendResult> SendTemplateAsync(ProviderTemplateSendRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return DispatchAsync(WhatsAppSendRequestFactory.CreateTemplate(request, _versionedBase, out var recusa), recusa, cancellationToken);
    }

    /// <summary>
    /// O envio de um ARQUIVO (ADR 0031) em duas etapas, as duas com o token SÓ no cabeçalho do pedido: (1) a SUBIDA (<c>POST .../media</c>, multipart) e (2) a MENSAGEM que usa o identificador devolvido (<c>POST .../messages</c>,
    /// pelo mesmo ponto de envio do texto e do modelo). A subida não envia nada ao cliente, então qualquer falha dela (inclusive tempo esgotado e rede) é recusa transitória ou definitiva, nunca <see cref="ProviderSendOutcome.Unknown"/>:
    /// só a etapa da mensagem pode deixar dúvida, e a dúvida dela nunca é reenviada. O pedido é conferido por inteiro antes de qualquer chamada.
    /// </summary>
    public async Task<ProviderSendResult> SendMediaAsync(ProviderMediaSendRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (WhatsAppSendRequestFactory.ValidateMediaSend(request) is { } recusa)
        {
            return recusa;
        }

        string idDoArquivo;
        using (var subida = WhatsAppSendRequestFactory.CreateMediaUpload(request, _versionedBase))
        using (var limite = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            limite.CancelAfter(_mediaTimeout);
            HttpResponseMessage? resposta = null;
            try
            {
                resposta = await SendMediaStepAsync(subida, limite.Token);
                var corpo = await ReadBoundedAsync(resposta.Content, _maxResponseBytes, limite.Token);
                var (id, falha) = WhatsAppSendResponseReader.ReadUpload((int)resposta.StatusCode, corpo, RetryAfter(resposta));
                if (id is null)
                {
                    return falha!;
                }

                idDoArquivo = id;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return new ProviderSendResult(ProviderSendOutcome.RejectedTransient, null, "upload-timeout");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                return new ProviderSendResult(ProviderSendOutcome.RejectedTransient, null, "upload-network-error");
            }
            finally
            {
                resposta?.Dispose();
            }
        }

        return await DispatchAsync(WhatsAppSendRequestFactory.CreateMediaMessage(request, idDoArquivo, _versionedBase), null, cancellationToken);
    }

    private async Task<ProviderSendResult> DispatchAsync(HttpRequestMessage? mensagemPronta, ProviderSendResult? recusa, CancellationToken cancellationToken)
    {
        using var mensagem = mensagemPronta;
        if (mensagem is null)
        {
            // Recusado ANTES de chamar o provedor (token fora do formato, destinatário, texto ou parâmetro inválidos): nada foi enviado.
            return recusa!;
        }

        // O tempo limite da chamada é do adaptador: esgotado sem o chamador ter desistido, o resultado é ambíguo (a mensagem pode ter saído).
        using var limite = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limite.CancelAfter(_requestTimeout);

        HttpResponseMessage? resposta = null;
        try
        {
            resposta = await _http.SendAsync(mensagem, HttpCompletionOption.ResponseHeadersRead, limite.Token);
            var corpo = await ReadBoundedAsync(resposta.Content, _maxResponseBytes, limite.Token);
            return WhatsAppSendResponseReader.Read((int)resposta.StatusCode, corpo, RetryAfter(resposta));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // O chamador desistiu: a decisão é dele (o Sender sabe se há dúvida sobre o efeito).
            throw;
        }
        catch (OperationCanceledException)
        {
            return WhatsAppSendResponseReader.Unknown("timeout");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            // Só o TIPO da falha vira código: a mensagem pode carregar o endereço ou trechos do pedido.
            return WhatsAppSendResponseReader.Unknown("network-error");
        }
        finally
        {
            resposta?.Dispose();
        }
    }

    /// <summary>
    /// A sonda: lê o número no provedor (<c>GET</c>, nenhuma mensagem sai) para provar que o token é aceito e que o número existe e é acessível por ele. O tempo limite é o do envio; esgotado, rede caída ou 5xx dão
    /// <see cref="ProviderProbeOutcome.Unavailable"/>: nunca se afirma que a configuração está certa sem a resposta do provedor.
    /// </summary>
    public async Task<ProviderProbeResult> ProbeAsync(ProviderProbeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var mensagem = WhatsAppSendRequestFactory.CreateProbe(request, _versionedBase);
        if (mensagem is null)
        {
            // Token fora do formato ou número não numérico: nada foi pedido ao provedor, e o administrador precisa corrigir o cadastro.
            return new ProviderProbeResult(ProviderProbeOutcome.CredentialRejected, null, null, null, "credential-or-account-invalid-format");
        }

        using var limite = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limite.CancelAfter(_requestTimeout);

        HttpResponseMessage? resposta = null;
        try
        {
            resposta = await _http.SendAsync(mensagem, HttpCompletionOption.ResponseHeadersRead, limite.Token);
            var corpo = await ReadBoundedAsync(resposta.Content, _maxResponseBytes, limite.Token);
            return WhatsAppProbeResponseReader.Read((int)resposta.StatusCode, corpo, request.ExternalAccountId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return WhatsAppProbeResponseReader.Unavailable("timeout");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return WhatsAppProbeResponseReader.Unavailable("network-error");
        }
        finally
        {
            resposta?.Dispose();
        }
    }

    // Mídia (ADR 0028)

    /// <summary>
    /// O download de uma mídia recebida, em duas etapas: os dados (<c>GET /{versão}/{media-id}</c>, que devolvem o endereço do conteúdo, o tipo, o resumo e o tamanho) e o conteúdo (<c>GET</c> no endereço devolvido, com o
    /// mesmo token, como a documentação exige). O token só vai a endereço da Graph API configurada ou de um domínio conhecido da Meta, só por HTTPS (exceto o laço local da própria base, nos testes), sem redirecionamento
    /// e sem usuário na URL. O tamanho é conferido antes (o declarado) e durante a leitura (o medido): nunca mais que o limite entra na memória.
    /// </summary>
    public async Task<ProviderMediaResult> DownloadMediaAsync(ProviderMediaRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.MaxBytes < 1)
        {
            return WhatsAppMediaResponseReader.Fail(ProviderMediaOutcome.Rejected, "invalid-limit");
        }

        using var consulta = WhatsAppSendRequestFactory.CreateMediaLookup(request, _versionedBase, out var recusa);
        if (consulta is null)
        {
            // Token ou identificador fora do formato: nada foi pedido ao provedor.
            return recusa!;
        }

        using var limite = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            limite.CancelAfter(_mediaTimeout);
            WhatsAppMediaResponseReader.Lookup dados;
            using (var respostaDados = await SendMediaStepAsync(consulta, limite.Token))
            {
                var corpo = await ReadBoundedAsync(respostaDados.Content, _maxResponseBytes, limite.Token);
                var (lido, falha) = WhatsAppMediaResponseReader.ReadLookup((int)respostaDados.StatusCode, corpo);
                if (lido is null)
                {
                    return falha!;
                }

                dados = lido;
            }

            if (!Uri.TryCreate(dados.Url, UriKind.Absolute, out var endereco) || !IsAllowedMediaUrl(endereco))
            {
                // O provedor devolveu um endereço que não é dele: o token NÃO é enviado para lá.
                return WhatsAppMediaResponseReader.Fail(ProviderMediaOutcome.Rejected, "media-url-not-allowed");
            }

            if (dados.FileSize is { } declarado && declarado > request.MaxBytes)
            {
                return WhatsAppMediaResponseReader.Fail(ProviderMediaOutcome.TooLarge, "too-large");
            }

            limite.CancelAfter(_mediaTimeout);
            using var download = WhatsAppSendRequestFactory.CreateMediaDownload(request, endereco);
            using var respostaConteudo = await SendMediaStepAsync(download, limite.Token);
            var estado = (int)respostaConteudo.StatusCode;
            if (estado is < 200 or >= 300)
            {
                var erro = estado == 400 ? await ReadBoundedAsync(respostaConteudo.Content, _maxResponseBytes, limite.Token) : null;
                return WhatsAppMediaResponseReader.FromStatus(estado, erro, "content");
            }

            if (respostaConteudo.Content.Headers.ContentLength is { } tamanho && tamanho > request.MaxBytes)
            {
                return WhatsAppMediaResponseReader.Fail(ProviderMediaOutcome.TooLarge, "too-large");
            }

            var conteudo = await ReadUpToAsync(respostaConteudo.Content, request.MaxBytes, limite.Token);
            if (conteudo is null)
            {
                return WhatsAppMediaResponseReader.Fail(ProviderMediaOutcome.TooLarge, "too-large");
            }

            var tipo = dados.MimeType ?? respostaConteudo.Content.Headers.ContentType?.MediaType;
            return new ProviderMediaResult(ProviderMediaOutcome.Downloaded, conteudo, tipo, dados.Sha256, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return WhatsAppMediaResponseReader.Fail(ProviderMediaOutcome.Transient, "timeout");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return WhatsAppMediaResponseReader.Fail(ProviderMediaOutcome.Transient, "network-error");
        }
    }

    /// <summary>O ÚNICO ponto de chamada HTTP da mídia (as duas etapas do download e a subida do arquivo enviado passam por aqui).</summary>
    private Task<HttpResponseMessage> SendMediaStepAsync(HttpRequestMessage mensagem, CancellationToken cancellationToken) =>
        _http.SendAsync(mensagem, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

    /// <summary>
    /// O endereço do conteúdo só é seguido se for o da Graph API configurada (mesmo esquema e autoridade) ou HTTPS de um domínio da lista (o próprio domínio ou um subdomínio), na porta padrão, sem usuário nem
    /// fragmento. Endereço IP, outro domínio ou HTTP simples: recusado, e o token não sai.
    /// </summary>
    private bool IsAllowedMediaUrl(Uri endereco)
    {
        if (!string.IsNullOrEmpty(endereco.UserInfo) || !string.IsNullOrEmpty(endereco.Fragment))
        {
            return false;
        }

        if (string.Equals(endereco.Scheme, _versionedBase.Scheme, StringComparison.OrdinalIgnoreCase) && string.Equals(endereco.Authority, _versionedBase.Authority, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (endereco.Scheme != Uri.UriSchemeHttps || !endereco.IsDefaultPort || endereco.HostNameType != UriHostNameType.Dns)
        {
            return false;
        }

        var dominio = endereco.IdnHost.ToLowerInvariant();
        return _mediaHostSuffixes.Any(sufixo => dominio == sufixo || dominio.EndsWith("." + sufixo, StringComparison.Ordinal));
    }

    /// <summary>Lê o conteúdo até o limite. Mais que isso: nulo (grande demais), e o excesso nunca entra na memória.</summary>
    private static async Task<byte[]?> ReadUpToAsync(HttpContent conteudo, long limite, CancellationToken cancellationToken)
    {
        await using var fluxo = await conteudo.ReadAsStreamAsync(cancellationToken);
        using var memoria = new MemoryStream();
        var bloco = new byte[81920];
        int lidos;
        while ((lidos = await fluxo.ReadAsync(bloco, cancellationToken)) > 0)
        {
            if (memoria.Length + lidos > limite)
            {
                return null;
            }

            memoria.Write(bloco, 0, lidos);
        }

        return memoria.ToArray();
    }

    /// <summary>Lê o corpo até o limite. Acima dele (ou com o <c>Content-Length</c> acima) devolve nulo: a resposta é ilegível, e nada gigante entra na memória.</summary>
    private static async Task<byte[]?> ReadBoundedAsync(HttpContent conteudo, int limite, CancellationToken cancellationToken)
    {
        if (conteudo.Headers.ContentLength is { } declarado && declarado > limite)
        {
            return null;
        }

        await using var fluxo = await conteudo.ReadAsStreamAsync(cancellationToken);
        using var memoria = new MemoryStream();
        var bloco = new byte[8192];
        int lidos;
        while ((lidos = await fluxo.ReadAsync(bloco, cancellationToken)) > 0)
        {
            if (memoria.Length + lidos > limite)
            {
                return null;
            }

            memoria.Write(bloco, 0, lidos);
        }

        return memoria.ToArray();
    }

    /// <summary>A espera que o provedor sugeriu (<c>Retry-After</c> em segundos ou em data), quando houver. A documentação da Meta não a promete. Nunca negativa.</summary>
    private TimeSpan? RetryAfter(HttpResponseMessage resposta)
    {
        RetryConditionHeaderValue? espera = resposta.Headers.RetryAfter;
        if (espera is null)
        {
            return null;
        }

        if (espera.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        return espera.Date is { } data ? (data - _clock.GetUtcNow() is var resto && resto > TimeSpan.Zero ? resto : TimeSpan.Zero) : null;
    }

    // Auxiliares

    private static bool TryDecodeLowerHex(ReadOnlySpan<char> hex, Span<byte> destino)
    {
        if (hex.Length != destino.Length * 2)
        {
            return false;
        }

        for (var i = 0; i < destino.Length; i++)
        {
            var alto = Nibble(hex[2 * i]);
            var baixo = Nibble(hex[(2 * i) + 1]);
            if (alto < 0 || baixo < 0)
            {
                return false;
            }

            destino[i] = (byte)((alto << 4) | baixo);
        }

        return true;
    }

    private static int Nibble(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        _ => -1,
    };

    /// <summary>Compara dois textos em tempo constante, sem vazar o tamanho: compara os SHA-256 dos dois (sempre 32 bytes).</summary>
    private static bool FixedTimeTextEquals(string a, string b)
    {
        Span<byte> hashA = stackalloc byte[SHA256.HashSizeInBytes];
        Span<byte> hashB = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(a), hashA);
        SHA256.HashData(Encoding.UTF8.GetBytes(b), hashB);
        return CryptographicOperations.FixedTimeEquals(hashA, hashB);
    }

    private static bool IsSafeChallenge(string? desafio)
    {
        if (string.IsNullOrEmpty(desafio) || desafio.Length > MaxChallengeLength)
        {
            return false;
        }

        foreach (var c in desafio)
        {
            if (c is < '!' or > '~')
            {
                return false;
            }
        }

        return true;
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }
}
