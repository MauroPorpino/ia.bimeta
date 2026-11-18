using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IaBimeta.Identity.Domain;
using IaBimeta.IntegrationTests.Api;
using IaBimeta.Tenancy.Application;
using IaBimeta.Tenancy.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.IntegrationTests.Embedded;

internal sealed record ConexaoDto(Guid Id, Guid OrganizationId, string Name, string Mode, string Provisioning, List<string> AllowedOrigins, bool AllowStartConversation, string Status);

internal sealed record ChaveCriadaDto(string KeyId, string Secret, DateTimeOffset CreatedAt, DateTimeOffset? OthersExpireAt);

internal sealed record ChaveDto(string KeyId, string Status, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? RevokedAt);

internal sealed record VinculoDto(Guid Id, Guid ConnectionId, string ExternalUserId, Guid UserId, string Source, string Status);

internal sealed record TicketDto(string Ticket, DateTimeOffset ExpiresAt);

internal sealed record UsuarioDaTrocaDto(Guid Id, string DisplayName);

internal sealed record TrocaDto(string Token, DateTimeOffset ExpiresAt, string Mode, Guid OrganizationId, string? CustomerRef, UsuarioDaTrocaDto User, Guid SessionId);

internal sealed record SessaoEmbutidaDto(Guid SessionId, string Mode, Guid OrganizationId, List<string> CustomerRefs, DateTimeOffset ExpiresAt);

/// <summary>
/// O "servidor do ERP" dos testes: assina as chamadas EXATAMENTE como o contrato do ADR 0039 manda (HMAC-SHA256 de <c>v1 ⏎ timestamp ⏎ nonce ⏎ MÉTODO ⏎ caminho ⏎ sha256hex(corpo)</c>), sem usar o código do servidor,
/// para que o teste prove o contrato e não a implementação.
/// </summary>
internal sealed class ErpAssinante(ApiServer api, string keyId, string segredo, RelogioManual? relogio = null)
{
    public string KeyId { get; } = keyId;

    public string Segredo { get; } = segredo;

    public static string NonceNovo() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));

    public static string Assinar(string segredo, string timestamp, string nonce, string metodo, string caminho, byte[] corpo)
    {
        var canonico = $"v1\n{timestamp}\n{nonce}\n{metodo.ToUpperInvariant()}\n{caminho}\n{Convert.ToHexStringLower(SHA256.HashData(corpo))}";
        return Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(segredo), Encoding.UTF8.GetBytes(canonico)));
    }

    /// <summary>Envia uma chamada assinada. Os parâmetros opcionais permitem os ataques: instante, nonce, segredo ou corpo diferentes do assinado.</summary>
    public async Task<HttpResponseMessage> EnviarAsync(
        string caminho,
        object? corpo,
        string? nonce = null,
        DateTimeOffset? instante = null,
        string? segredoUsado = null,
        string? keyIdUsado = null,
        byte[]? corpoEnviado = null,
        string? caminhoAssinado = null,
        Action<HttpRequestMessage>? ajustar = null)
    {
        var bytes = corpo is null ? Array.Empty<byte>() : JsonSerializer.SerializeToUtf8Bytes(corpo, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var timestamp = (instante ?? relogio?.GetUtcNow() ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds().ToString();
        var n = nonce ?? NonceNovo();
        var assinatura = Assinar(segredoUsado ?? Segredo, timestamp, n, "POST", caminhoAssinado ?? caminho, bytes);

        using var requisicao = new HttpRequestMessage(HttpMethod.Post, caminho) { Content = new ByteArrayContent(corpoEnviado ?? bytes) };
        requisicao.Content.Headers.ContentType = new("application/json");
        requisicao.Headers.Add("X-IaB-Key-Id", keyIdUsado ?? KeyId);
        requisicao.Headers.Add("X-IaB-Timestamp", timestamp);
        requisicao.Headers.Add("X-IaB-Nonce", n);
        requisicao.Headers.Add("X-IaB-Signature", assinatura);
        ajustar?.Invoke(requisicao);
        return await api.Client.SendAsync(requisicao);
    }

    public async Task<TicketDto> EmitirAsync(string externalUserId, string challenge, string parentOrigin = EmbeddedTestSupport.Origem, string? cliente = null, string? nome = "Ana do ERP")
    {
        using var resposta = await EnviarAsync("/embed/v1/launch-tickets", new
        {
            externalUserId,
            displayName = nome,
            challenge,
            parentOrigin,
            customer = cliente is null ? null : new { @ref = cliente },
        });
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        Assert.Contains("no-store", resposta.Headers.CacheControl?.ToString());
        return (await resposta.Content.ReadFromJsonAsync<TicketDto>())!;
    }

    public async Task<TicketDto> EmitirContextoAsync(string externalUserId, string cliente, Guid sessionId)
    {
        using var resposta = await EnviarAsync("/embed/v1/context-tickets", new { externalUserId, sessionId, customer = new { @ref = cliente } });
        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        return (await resposta.Content.ReadFromJsonAsync<TicketDto>())!;
    }
}

internal static class EmbeddedTestSupport
{
    public const string Origem = "https://erp.exemplo.com.br";

    /// <summary>Faz o que o Worker faz (E1-R, quarta rodada): abre o contexto do tenant e entrega à trilha a auditoria do Embedded guardada na transação do consumo do ticket. Devolve quantas entregou.</summary>
    public static async Task<int> EntregarAuditoriaAsync(ApiServer api, IaBimeta.SharedKernel.TenantId tenant)
    {
        using var escopo = api.Services.CreateScope();
        escopo.ServiceProvider.GetRequiredService<IaBimeta.SharedKernel.RequestContextHolder>().Set(new IaBimeta.SharedKernel.RequestContext(tenant, "worker-reconciler"));
        return await escopo.ServiceProvider.GetRequiredService<IaBimeta.Identity.Application.IIdentityAuditOutbox>().FlushAsync(100, CancellationToken.None);
    }

    /// <summary>
    /// Resumo EXATO do que a outbox do tenant guarda agora, "operação=quantidade" em ordem alfabética. Existe para as asserções detectarem auditoria ESPÚRIA (uma operação a mais, ou a mais de uma vez): comparar com
    /// "pelo menos N" deixaria passar um evento de sucesso de uma criação que foi desfeita.
    /// </summary>
    public static Task<string> ResumoDaOutboxAsync(Guid tenant) =>
        ResumoAsync("SELECT operation, count(*) FROM identity.audit_outbox WHERE tenant_id = @t GROUP BY operation ORDER BY operation", tenant);

    /// <summary>O mesmo resumo para a trilha do tenant, só das operações do Embedded.</summary>
    public static Task<string> ResumoDaTrilhaAsync(Guid tenant) =>
        ResumoAsync("SELECT operation, count(*) FROM audit.audit_events WHERE tenant_id = @t AND operation LIKE 'identity.embed.%' GROUP BY operation ORDER BY operation", tenant);

    /// <summary>A linha de auditoria como ficou (ator, tipo e recurso), para conferir QUEM e SOBRE O QUÊ, não só quantas.</summary>
    public static async Task<IReadOnlyList<(Guid? Ator, string Tipo, string Recurso)>> LinhasDaOutboxAsync(Guid tenant, string operacao)
    {
        await using var conexao = new Npgsql.NpgsqlConnection(TestDatabase.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = new Npgsql.NpgsqlCommand("SELECT actor_user_id, resource_type, COALESCE(resource_id, '') FROM identity.audit_outbox WHERE tenant_id = @t AND operation = @o ORDER BY created_at, id", conexao);
        comando.Parameters.AddWithValue("t", tenant);
        comando.Parameters.AddWithValue("o", operacao);
        var linhas = new List<(Guid?, string, string)>();
        await using var leitor = await comando.ExecuteReaderAsync();
        while (await leitor.ReadAsync())
        {
            linhas.Add((leitor.IsDBNull(0) ? null : leitor.GetGuid(0), leitor.GetString(1), leitor.GetString(2)));
        }

        return linhas;
    }

    private static async Task<string> ResumoAsync(string sql, Guid tenant)
    {
        await using var conexao = new Npgsql.NpgsqlConnection(TestDatabase.ConnectionString);
        await conexao.OpenAsync();
        await using var comando = new Npgsql.NpgsqlCommand(sql, conexao);
        comando.Parameters.AddWithValue("t", tenant);
        var partes = new List<string>();
        await using var leitor = await comando.ExecuteReaderAsync();
        while (await leitor.ReadAsync())
        {
            partes.Add($"{leitor.GetString(0)}={leitor.GetInt64(1)}");
        }

        return string.Join(";", partes);
    }

    public static string NovoVerifier() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public static string Challenge(string verifier) => Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static Dictionary<string, string?> Configuracao(Action<Dictionary<string, string?>>? ajustar = null)
    {
        var configuracao = ApiServer.ConfiguracaoDeProducao();
        configuracao["Identity:LoginRateLimit:PermitLimit"] = "1000";
        configuracao["Embedded:RateLimit:IntegrationPermitLimit"] = "100000";
        configuracao["Embedded:RateLimit:ExchangePermitLimit"] = "100000";
        configuracao["Identity:Secrets:ActiveKeyId"] = "t1";
        configuracao["Identity:Secrets:Keys:t1"] = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)(i * 7 + 3)).ToArray());
        ajustar?.Invoke(configuracao);
        return configuracao;
    }

    public static async Task<TrocaDto> TrocarAsync(ApiServer api, string ticket, string verifier, string parentOrigin = Origem)
    {
        using var resposta = await api.Client.PostAsJsonAsync("/embed/v1/exchange", new { ticket, verifier, parentOrigin });
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Contains("no-store", resposta.Headers.CacheControl?.ToString());
        return (await resposta.Content.ReadFromJsonAsync<TrocaDto>())!;
    }

    public static async Task<HttpStatusCode> TentarTrocarAsync(ApiServer api, string ticket, string verifier, string parentOrigin = Origem)
    {
        using var resposta = await api.Client.PostAsJsonAsync("/embed/v1/exchange", new { ticket, verifier, parentOrigin });
        return resposta.StatusCode;
    }

    public static async Task<string> CodigoDoProblemaAsync(HttpResponseMessage resposta)
    {
        var json = await resposta.Content.ReadFromJsonAsync<JsonElement>();
        return json.TryGetProperty("code", out var codigo) ? codigo.GetString() ?? string.Empty : string.Empty;
    }
}

/// <summary>Um tenant com organização, administrador do tenant, uma conexão de integração e uma chave ativa, prontos para o teste.</summary>
internal sealed class EmbeddedCenario
{
    private EmbeddedCenario(
        PostgresFixture banco, ApiServer api, TenantInfo tenant, OrganizationView organizacao, UsuarioCriado admin, string tokenDoAdmin, ConexaoDto conexao, ChaveCriadaDto chave, ErpAssinante erp)
    {
        Banco = banco;
        Api = api;
        Tenant = tenant;
        Organizacao = organizacao;
        Admin = admin;
        TokenDoAdmin = tokenDoAdmin;
        Conexao = conexao;
        Chave = chave;
        Erp = erp;
    }

    public PostgresFixture Banco { get; }

    public ApiServer Api { get; }

    public TenantInfo Tenant { get; }

    public OrganizationView Organizacao { get; }

    public UsuarioCriado Admin { get; }

    public string TokenDoAdmin { get; }

    public ConexaoDto Conexao { get; }

    public ChaveCriadaDto Chave { get; }

    public ErpAssinante Erp { get; }

    /// <param name="api">Um servidor já subido (para dois tenants no mesmo host). Sem ele, sobe um novo.</param>
    public static async Task<EmbeddedCenario> CriarAsync(
        PostgresFixture banco,
        string prefixo,
        string provisionamento = "JitAttendant",
        string modo = "FullAttendant",
        RelogioManual? relogio = null,
        ApiServer? api = null,
        Action<Dictionary<string, string?>>? configurar = null)
    {
        var tenant = await banco.RegistrarTenantAsync(prefixo);
        var organizacao = await banco.CriarOrganizacaoAsync(tenant, $"Organização {prefixo}");
        var admin = await banco.CriarAdministradorDoTenantAsync(tenant);
        api ??= await ApiServer.StartAsync("Production", EmbeddedTestSupport.Configuracao(configurar), relogio);

        var token = await api.LoginAsync(admin);
        var conexao = await api.CriarAsync<ConexaoDto>(token, "/embedded/connections", new
        {
            organizationId = organizacao.Id,
            name = $"ERP {prefixo}",
            mode = modo,
            provisioning = provisionamento,
            allowedOrigins = new[] { EmbeddedTestSupport.Origem },
            allowStartConversation = false,
        });
        var chave = await api.CriarAsync<ChaveCriadaDto>(token, $"/embedded/connections/{conexao.Id}/keys", new { });
        return new EmbeddedCenario(banco, api, tenant, organizacao, admin, token, conexao, chave, new ErpAssinante(api, chave.KeyId, chave.Secret, relogio));
    }

    /// <summary>Emite o ticket, troca no "iframe" e devolve a sessão embutida (o caminho feliz completo).</summary>
    public async Task<(TrocaDto Troca, string Verifier)> EntrarAsync(string externalUserId, string? cliente = null, string? nome = "Ana do ERP")
    {
        var verifier = EmbeddedTestSupport.NovoVerifier();
        var ticket = await Erp.EmitirAsync(externalUserId, EmbeddedTestSupport.Challenge(verifier), cliente: cliente, nome: nome);
        return (await EmbeddedTestSupport.TrocarAsync(Api, ticket.Ticket, verifier), verifier);
    }

    public Task<HttpResponseMessage> ComoAsync(string token, HttpMethod metodo, string caminho, object? corpo = null) => Api.EnviarAsync(token, metodo, caminho, corpo);
}

/// <summary>Captura os erros que o servidor registra em log, para o teste mostrá-los quando um status inesperado (500) aparecer sob concorrência.</summary>
internal sealed class CapturaDeErros(System.Collections.Concurrent.ConcurrentBag<string> registros) : Microsoft.Extensions.Logging.ILoggerProvider, Microsoft.Extensions.Logging.ILogger
{
    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoria) => this;

    public void Dispose()
    {
    }

    public IDisposable? BeginScope<TState>(TState estado)
        where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel nivel) => nivel >= Microsoft.Extensions.Logging.LogLevel.Error;

    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel nivel, Microsoft.Extensions.Logging.EventId evento, TState estado, Exception? excecao, Func<TState, Exception?, string> formatador) =>
        registros.Add(formatador(estado, excecao) + " :: " + excecao);
}
