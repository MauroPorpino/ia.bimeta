using System.Text.Json;
using IaBimeta.Conversations.Application;
using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Domain;
using IaBimeta.Identity.Contracts.Integration;
using IaBimeta.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace IaBimeta.Conversations.Presentation;

/// <summary>
/// Quem estabelece o contexto confiável do tenant para uma chamada assinada do sistema externo. Só o host o implementa (TEN-1: o <c>RequestContext</c> só nasce nos hosts, e aqui vem do tenant que o AUTENTICADOR resolveu a
/// partir da chave, nunca do corpo, de cabeçalho ou de parâmetro). O contexto criado não tem usuário e tem a origem de integração (<see cref="ContactLinkService.IntegrationContextOrigin"/>).
/// </summary>
public interface IIntegrationRequestContext
{
    void Establish(TenantId tenant);
}

/// <summary>
/// As rotas servidor-a-servidor do vínculo (ADR 0041, seção 1; E2.2): chamadas assinadas por HMAC do servidor do sistema externo (ERP, CRM, e-commerce ou outro), autenticadas pela porta do Identity
/// (<see cref="IIntegrationRequestAuthenticator"/>), sem sessão de usuário. O tenant, a conexão e a organização vêm SÓ do autenticador. O sistema externo afirma identidade e nunca concede permissão: nada aqui amplia o
/// acesso de ninguém.
/// <list type="bullet">
/// <item>Autenticação: qualquer recusa é o mesmo 401 genérico (nada revela qual verificação falhou); 429 e 503 mantêm a distinção do protocolo.</item>
/// <item><c>PUT /embed/v1/customer-links</c>: <c>200 { resolved, shared }</c>. <c>resolved:false</c> cobre contato inexistente, fora da organização ou não resolvível, com o MESMO corpo e status.</item>
/// <item><c>DELETE /embed/v1/customer-links</c>: <c>204</c> sempre que autenticado e válido, existindo ou não vínculo ativo (sem oráculo).</item>
/// </list>
/// </summary>
public static class ConversationsIntegrationEndpoints
{
    /// <summary>Corpo máximo das chamadas assinadas: o mesmo do restante do protocolo (<c>/embed/v1</c>).</summary>
    public const int MaxBodyBytes = 8 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <param name="rateLimitPolicy">O nome da política de limite de taxa das chamadas assinadas (cota por origem de rede, anterior à autenticação). O host registra a política; sem ela a rota falha em vez de ficar sem limite.</param>
    public static IEndpointRouteBuilder MapConversationsIntegrationEndpoints(this IEndpointRouteBuilder app, string rateLimitPolicy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rateLimitPolicy);

        var embed = app.MapGroup("/embed/v1");

        embed.MapPut("/customer-links", (HttpContext http, IIntegrationRequestAuthenticator autenticador, IIntegrationRequestContext contexto, ContactLinkService servico, CancellationToken ct) =>
            RunAsync<LinkCustomerBody>(http, autenticador, contexto, ct, async (chamador, corpo) =>
            {
                if (Invalid(corpo, phoneRequired: true) is { } invalido)
                {
                    return invalido;
                }

                var resultado = await servico.LinkByIntegrationAsync(chamador.ConnectionId, chamador.OrganizationId, corpo.CustomerRef, corpo.Phone, ct);
                return Results.Ok(new { resolved = resultado.Resolved, shared = resultado.Shared });
            })).RequireRateLimiting(rateLimitPolicy);

        embed.MapDelete("/customer-links", (HttpContext http, IIntegrationRequestAuthenticator autenticador, IIntegrationRequestContext contexto, ContactLinkService servico, CancellationToken ct) =>
            RunAsync<LinkCustomerBody>(http, autenticador, contexto, ct, async (chamador, corpo) =>
            {
                if (Invalid(corpo, phoneRequired: false) is { } invalido)
                {
                    return invalido;
                }

                await servico.UnlinkByIntegrationAsync(chamador.ConnectionId, chamador.OrganizationId, corpo.CustomerRef, corpo.Phone, ct);
                return Results.NoContent();
            })).RequireRateLimiting(rateLimitPolicy);

        // O consentimento (E2.4): a organização é SEMPRE a da conexão (do autenticador); o telefone, a finalidade, a situação, a evidência e a data da coleta vêm do corpo; o cliente vem do caminho. Não exige vínculo nem
        // contato: o cliente que nunca escreveu precisa de consentimento antes do início ativo.
        embed.MapPut("/customers/{customerRef}/consents", (HttpContext http, string customerRef, IIntegrationRequestAuthenticator autenticador, IIntegrationRequestContext contexto, ConsentService servico, CancellationToken ct) =>
            AmbiguousConsentPath(SignedPath(http))
                ? Task.FromResult(Problem(StatusCodes.Status400BadRequest, "Dados inválidos", "O caminho é ambíguo: use a codificação percentual padrão, sem barra, ponto-segmento nem controle no identificador do cliente.", "invalid-path"))
                : RunAsync<ConsentBody>(http, autenticador, contexto, ct, async (chamador, corpo) =>
            {
                if (!TryParsePurpose(corpo.Purpose, out var finalidade))
                {
                    return Problem(StatusCodes.Status400BadRequest, "Dados inválidos", "A finalidade deve ser Utility ou Marketing.", "invalid-purpose");
                }

                if (!TryParseStatus(corpo.Status, out var situacao))
                {
                    return Problem(StatusCodes.Status400BadRequest, "Dados inválidos", "A situação deve ser Granted ou Revoked.", "invalid-status");
                }

                var resultado = await servico.RecordByIntegrationAsync(chamador.ConnectionId, chamador.OrganizationId, customerRef, corpo.Phone, finalidade, situacao, corpo.Evidence, ParseCollectedAt(corpo.CollectedAt), ct);
                return resultado.Outcome switch
                {
                    ConsentRecordOutcome.Recorded or ConsentRecordOutcome.Unchanged => Results.Ok(new { recorded = resultado.Outcome == ConsentRecordOutcome.Recorded, status = resultado.Status.ToString(), purpose = resultado.Purpose.ToString() }),
                    ConsentRecordOutcome.Superseded => Problem(StatusCodes.Status409Conflict, "Registro defasado", "Há uma revogação posterior à data da coleta informada.", "consent-superseded"),
                    _ => Problem(StatusCodes.Status400BadRequest, "Dados inválidos", "Corpo inválido.", "invalid-request"),
                };
            })).RequireRateLimiting(rateLimitPolicy);

        return app;
    }

    /// <summary>O corpo do consentimento. A finalidade e a situação são NOMES (não números); a data da coleta é texto ISO 8601 com fuso, para a recusa ter o código do campo.</summary>
    public sealed record ConsentBody(string? Phone, string? Purpose, string? Status, string? Evidence, string? CollectedAt);

    private static bool TryParsePurpose(string? valor, out ConsentPurpose finalidade)
    {
        finalidade = default;
        switch (valor?.Trim().ToLowerInvariant())
        {
            case "utility":
                finalidade = ConsentPurpose.Utility;
                return true;
            case "marketing":
                finalidade = ConsentPurpose.Marketing;
                return true;
            default:
                return false;
        }
    }

    private static bool TryParseStatus(string? valor, out ConsentStatus situacao)
    {
        situacao = default;
        switch (valor?.Trim().ToLowerInvariant())
        {
            case "granted":
                situacao = ConsentStatus.Granted;
                return true;
            case "revoked":
                situacao = ConsentStatus.Revoked;
                return true;
            default:
                return false;
        }
    }

    // Só ISO 8601 COM fuso (Z ou ±hh:mm): sem fuso a data seria ambígua. Qualquer outra coisa vira "ausente", e o serviço a recusa com collected-at-invalid.
    private static DateTimeOffset? ParseCollectedAt(string? texto) =>
        texto is not null
        && System.Text.RegularExpressions.Regex.IsMatch(texto.Trim(), @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d{1,7})?)?(Z|[+-]\d{2}:\d{2})$")
        && DateTimeOffset.TryParse(texto.Trim(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var data)
            ? data
            : null;

    /// <summary>O corpo das duas rotas do vínculo: o cliente (referência opaca do sistema externo) e o telefone (obrigatório para vincular; opcional para desvincular).</summary>
    public sealed record LinkCustomerBody(string? CustomerRef, string? Phone);

    /// <summary>
    /// O fluxo comum, na ordem: consulta recusada; corpo lido com teto; autenticação (falha fechada); contexto do tenant do autenticador; corpo interpretado; operação. A validação do corpo só vem DEPOIS da
    /// autenticação: quem não se autentica nunca aprende nada sobre o formato.
    /// </summary>
    private static async Task<IResult> RunAsync<TBody>(
        HttpContext http, IIntegrationRequestAuthenticator autenticador, IIntegrationRequestContext contexto, CancellationToken ct, Func<IntegrationCaller, TBody, Task<IResult>> operacao)
        where TBody : class
    {
        // As rotas assinadas não têm parâmetros de consulta: a assinatura cobre só método, caminho e corpo, então uma consulta seria dado NÃO assinado.
        if (http.Request.QueryString.HasValue)
        {
            return Problem(StatusCodes.Status400BadRequest, "Dados inválidos", "Esta rota não aceita parâmetros de consulta.", "invalid-request");
        }

        byte[] corpo;
        try
        {
            corpo = await ReadBodyAsync(http, ct);
        }
        catch (BodyTooLargeException)
        {
            return Problem(StatusCodes.Status400BadRequest, "Dados inválidos", "Corpo grande demais.", "invalid-request");
        }

        var cabecalhos = http.Request.Headers;
        var chamada = new IntegrationSignedCall(
            Single(cabecalhos["X-IaB-Key-Id"]), Single(cabecalhos["X-IaB-Timestamp"]), Single(cabecalhos["X-IaB-Nonce"]), Single(cabecalhos["X-IaB-Signature"]),
            http.Request.Method, SignedPath(http), corpo);

        var autenticacao = await autenticador.AuthenticateAsync(chamada, ct);
        switch (autenticacao.Outcome)
        {
            case IntegrationAuthenticationOutcome.Authenticated when autenticacao.IsAuthenticated:
                break;
            case IntegrationAuthenticationOutcome.RateLimited:
                http.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling((autenticacao.RetryAfter ?? TimeSpan.FromSeconds(1)).TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                return Problem(StatusCodes.Status429TooManyRequests, "Limite excedido", "Requisições demais para esta chave.", "embedded-rate-limited");
            case IntegrationAuthenticationOutcome.Unavailable:
                // Contenção ou falha operacional: nada foi aceito nem gravado, repetir é seguro. Sem detalhe do erro.
                http.Response.Headers.RetryAfter = "1";
                return Problem(StatusCodes.Status503ServiceUnavailable, "Ocupado", "O serviço está ocupado. Tente novamente em instantes.", "embedded-busy");
            default:
                // Falha fechada: qualquer coisa que não seja um chamador autenticado é a mesma recusa genérica.
                return Problem(StatusCodes.Status401Unauthorized, "Autenticação do Embedded recusada", "Autenticação do Embedded recusada.", "embedded-authentication-failed");
        }

        var chamador = autenticacao.Caller!;
        contexto.Establish(chamador.TenantId);

        TBody? pedido;
        try
        {
            pedido = JsonSerializer.Deserialize<TBody>(corpo, JsonOptions);
        }
        catch (JsonException)
        {
            pedido = null;
        }

        if (pedido is null)
        {
            return Problem(StatusCodes.Status400BadRequest, "Dados inválidos", "Corpo inválido.", "invalid-body");
        }

        try
        {
            return await operacao(chamador, pedido);
        }
        catch (ConversationsValidationException ex)
        {
            return Problem(StatusCodes.Status400BadRequest, "Dados inválidos", ex.Message, ex.Code ?? "invalid-request");
        }
        catch (ContactLinkContentionException)
        {
            http.Response.Headers.RetryAfter = "1";
            return Problem(StatusCodes.Status503ServiceUnavailable, "Ocupado", "O serviço está ocupado. Tente novamente em instantes.", "embedded-busy");
        }
    }

    // A validação com o código do campo (o sistema externo corrige o dado): a referência do cliente (1 a 128, sem controle) e o telefone no formato internacional ESTRITO (+ ou 00 e o código do país).
    private static IResult? Invalid(LinkCustomerBody corpo, bool phoneRequired)
    {
        var cliente = (corpo.CustomerRef ?? string.Empty).Trim();
        if (cliente.Length == 0 || cliente.Length > ContactExternalLink.CustomerRefMaxLength || cliente.Any(char.IsControl))
        {
            return Problem(StatusCodes.Status400BadRequest, "Dados inválidos", "O identificador do cliente deve ter de 1 a 128 caracteres, sem controle.", "invalid-customer-ref");
        }

        if ((phoneRequired || corpo.Phone is not null) && !E164PhoneNumber.TryNormalizeInternational(corpo.Phone, out _))
        {
            return Problem(StatusCodes.Status400BadRequest, "Dados inválidos", "O telefone deve estar no formato internacional (E.164), com o prefixo + e o código do país.", "invalid-phone");
        }

        return null;
    }

    private static IResult Problem(int status, string title, string detail, string code) =>
        Results.Problem(title: title, detail: detail, statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = code });

    /// <summary>
    /// O caminho que a assinatura cobre: o caminho EXATAMENTE como foi enviado (codificado em porcentagem), que é o que o sistema externo tem em mãos para assinar. Um identificador de cliente com espaço ou acento
    /// (<c>JOÃO 7</c>) chega ao servidor DECODIFICADO em <c>Request.Path</c>; assinar essa forma exigiria que o sistema externo adivinhasse a decodificação. Sem o alvo bruto (servidor de teste), recodifica o caminho.
    /// </summary>
    private static string SignedPath(HttpContext http)
    {
        var bruto = http.Features.Get<IHttpRequestFeature>()?.RawTarget;
        if (!string.IsNullOrEmpty(bruto))
        {
            var fim = bruto.IndexOf('?', StringComparison.Ordinal);
            return CanonicalizePercentEncoding(fim < 0 ? bruto : bruto[..fim]);
        }

        return CanonicalizePercentEncoding(http.Request.Path.ToUriComponent());
    }

    /// <summary>
    /// A forma CANÔNICA do caminho assinado: o caminho como enviado, com a codificação percentual em hexadecimal MAIÚSCULO (<c>%c3</c> vira <c>%C3</c>). Clientes HTTP, proxies e servidores normalizam a caixa do hexadecimal de
    /// formas diferentes; sem uma forma única, a mesma requisição teria duas assinaturas possíveis. Nada mais é alterado (nem decodificado): para caminhos sem porcentagem (todas as rotas anteriores) o resultado é idêntico ao enviado.
    /// </summary>
    public static string CanonicalizePercentEncoding(string path)
    {
        if (!path.Contains('%', StringComparison.Ordinal))
        {
            return path;
        }

        var sb = new System.Text.StringBuilder(path.Length);
        for (var i = 0; i < path.Length; i++)
        {
            if (path[i] == '%' && i + 2 < path.Length && Uri.IsHexDigit(path[i + 1]) && Uri.IsHexDigit(path[i + 2]))
            {
                sb.Append('%').Append(char.ToUpperInvariant(path[i + 1])).Append(char.ToUpperInvariant(path[i + 2]));
                i += 2;
            }
            else
            {
                sb.Append(path[i]);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// A REPRESENTAÇÃO ASSINADA do caminho do consentimento (E2.4, ratificada com condições): exatamente o caminho como foi enviado, em codificação percentual, <c>/embed/v1/customers/{cliente}/consents</c>, sem barra final,
    /// sem consulta. Um caminho que admite duas leituras é recusado ANTES da autenticação (400 <c>invalid-path</c>, nada é aprendido): barra ou contrabarra no identificador (literal ou <c>%2F</c>/<c>%5C</c>), segmento
    /// vazio ou de pontos (<c>.</c>, <c>..</c>, também codificados), caractere de controle (<c>%00</c> a <c>%1F</c>, <c>%7F</c>), porcentagem malformada e barra final. A caixa da codificação hexadecimal é CANONICALIZADA
    /// para MAIÚSCULAS antes de verificar a assinatura (<see cref="CanonicalizePercentEncoding"/>): o sistema externo assina a forma canônica, e uma assinatura sobre a forma em minúsculas não confere (401). Esta regra não toca nas demais rotas assinadas.
    /// </summary>
    public static bool AmbiguousConsentPath(string path)
    {
        const string prefixo = "/embed/v1/customers/";
        const string sufixo = "/consents";
        if (!path.StartsWith(prefixo, StringComparison.Ordinal) || !path.EndsWith(sufixo, StringComparison.Ordinal) || path.Length <= prefixo.Length + sufixo.Length)
        {
            return true;
        }

        var segmento = path[prefixo.Length..^sufixo.Length];
        if (segmento.Length == 0 || segmento.Contains('/') || segmento.Contains('\\'))
        {
            return true;
        }

        for (var i = 0; i < segmento.Length; i++)
        {
            var c = segmento[i];
            if (c == '%')
            {
                if (i + 2 > segmento.Length - 1)
                {
                    return true; // porcentagem sem os dois dígitos
                }

                if (!Uri.IsHexDigit(segmento[i + 1]) || !Uri.IsHexDigit(segmento[i + 2]))
                {
                    return true;
                }

                var valor = Convert.ToInt32(segmento.Substring(i + 1, 2), 16);
                if (valor is 0x2F or 0x5C or 0x7F or < 0x20)
                {
                    return true; // barra, contrabarra, controle
                }

                i += 2;
                continue;
            }

            if (char.IsControl(c))
            {
                return true;
            }
        }

        // Segmento só de pontos (literais ou codificados): navegação de diretório.
        var decodificado = Uri.UnescapeDataString(segmento);
        return decodificado.All(c => c == '.');
    }

    /// <summary>Um cabeçalho repetido é recusado (devolve <c>null</c>): a assinatura não pode ser ambígua.</summary>
    private static string? Single(Microsoft.Extensions.Primitives.StringValues valores) => valores.Count == 1 ? valores[0] : null;

    private sealed class BodyTooLargeException : Exception;

    private static async Task<byte[]> ReadBodyAsync(HttpContext http, CancellationToken ct)
    {
        // O teto vale também para o servidor (corpo sem Content-Length, em pedaços): estoura antes de qualquer leitura grande.
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limite)
        {
            limite.MaxRequestBodySize = MaxBodyBytes;
        }

        if (http.Request.ContentLength is { } tamanho && tamanho > MaxBodyBytes)
        {
            throw new BodyTooLargeException();
        }

        using var memoria = new MemoryStream();
        var buffer = new byte[2048];
        int lidos;
        while ((lidos = await http.Request.Body.ReadAsync(buffer, ct)) > 0)
        {
            if (memoria.Length + lidos > MaxBodyBytes)
            {
                throw new BodyTooLargeException();
            }

            memoria.Write(buffer, 0, lidos);
        }

        return memoria.ToArray();
    }
}
