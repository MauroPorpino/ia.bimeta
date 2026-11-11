using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using IaBimeta.Audit.Contracts;
using IaBimeta.Capabilities.Contracts.Ports;
using IaBimeta.Capabilities.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Capabilities.Application;

/// <summary>O segredo de uma conexão enviado pelo administrador: só os campos do tipo de autenticação (<c>apiKey</c>; <c>username</c> e <c>password</c>; <c>clientId</c> e <c>clientSecret</c>).</summary>
public sealed record ConnectionSecretRequest(IReadOnlyDictionary<string, string> Values, int ExpectedRevision);

/// <summary>
/// A administração das conexões (ADR 0035): cadastrar, configurar, definir o segredo, habilitar e testar. Cada operação confere a permissão sobre a organização DA CONEXÃO e é auditada sem valores. O segredo é
/// cifrado na gravação e NUNCA volta por nenhuma leitura. O endereço passa pela política de destino já no cadastro (a mesma que vale na hora de conectar). A conexão nasce desabilitada.
/// </summary>
public sealed partial class ConnectionService
{
    private static readonly Dictionary<ConnectorAuthType, string[]> SecretFields = new()
    {
        [ConnectorAuthType.None] = [],
        [ConnectorAuthType.ApiKey] = ["apiKey"],
        [ConnectorAuthType.Basic] = ["username", "password"],
        [ConnectorAuthType.OAuth2ClientCredentials] = ["clientId", "clientSecret"],
    };

    private readonly CapabilitiesAuthorizer _auth;
    private readonly IConnectionStore _store;
    private readonly ISecretProtector _protector;
    private readonly IConnectorRegistry _connectors;
    private readonly OutboundDestinationPolicy _policy;
    private readonly CapabilitiesOptions _options;
    private readonly TimeProvider _clock;

    // O limite de testes por conexão é uma proteção contra abuso (cada teste faz uma chamada externa): em memória, por processo.
    private static readonly ConcurrentDictionary<(Guid Tenant, Guid Connection), Queue<DateTimeOffset>> TestWindow = new();

    internal ConnectionService(
        CapabilitiesAuthorizer auth,
        IConnectionStore store,
        ISecretProtector protector,
        IConnectorRegistry connectors,
        OutboundDestinationPolicy policy,
        CapabilitiesOptions options,
        TimeProvider clock)
    {
        _auth = auth;
        _store = store;
        _protector = protector;
        _connectors = connectors;
        _policy = policy;
        _options = options;
        _clock = clock;
    }

    [GeneratedRegex(@"^[a-z][a-z0-9-]{1,39}$")]
    private static partial Regex KeyRegex();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_.-]{0,63}$")]
    private static partial Regex QueryNameRegex();

    public async Task<IReadOnlyList<ConnectionView>> ListAsync(CancellationToken cancellationToken = default)
    {
        _auth.RequireAny(Permission.IntegrationRead);
        var contexto = _auth.Context;
        var visiveis = contexto.OrganizationsWithPermission(Permission.IntegrationRead);
        var todas = await _store.ListAsync(cancellationToken);
        return todas
            .Where(c => visiveis is null || (c.OrganizationId is { } o && visiveis.Contains(o)))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(ConnectionView.From)
            .ToList();
    }

    /// <summary>Uma conexão que o contexto não pode ler responde como se não existisse (404).</summary>
    public async Task<ConnectionView?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        _auth.RequireAny(Permission.IntegrationRead);
        var conexao = await _store.FindAsync(id, cancellationToken);
        return conexao is not null && _auth.Context.IsAllowed(Permission.IntegrationRead, conexao.OrganizationId) ? ConnectionView.From(conexao) : null;
    }

    public async Task<ConnectionView> CreateAsync(ConnectionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _auth.RequireAsync(Permission.IntegrationManage, request.OrganizationId, "capabilities.connection.create", CapabilitiesAuthorizer.ConnectionResource, null, cancellationToken);

        var configuracao = Normalize(request.Key, request.Name, request.BaseUrl, request.AuthType, request.AuthSettings, request.DefaultHeaders, request.AllowedHeaders, request.HealthPath, request.TimeoutSeconds, request.MaxResponseBytes, requireKey: true);
        if (await _store.CountAsync(cancellationToken) >= _options.MaxConnectionsPerTenant)
        {
            throw new CapabilitiesConflictException("limit-reached", "O limite de conexões desta empresa foi atingido.");
        }

        var contexto = _auth.Context;
        var conexao = IntegrationConnection.Create(
            contexto.TenantId, request.OrganizationId, configuracao.Key, configuracao.Name, configuracao.BaseUrl, request.AuthType, configuracao.AuthSettings, configuracao.DefaultHeaders,
            configuracao.AllowedHeaders, configuracao.HealthPath, configuracao.TimeoutSeconds, configuracao.MaxResponseBytes, contexto.Principal?.UserId, _clock.GetUtcNow());
        _store.Add(conexao);
        await _store.SaveAsync(cancellationToken);
        await _auth.WriteAsync("capabilities.connection.create", CapabilitiesAuthorizer.ConnectionResource, conexao.Id, AuditOutcome.Success, cancellationToken);
        return ConnectionView.From(conexao);
    }

    public async Task<ConnectionView?> UpdateAsync(Guid id, ConnectionUpdate request, CancellationToken cancellationToken = default)
    {
        _auth.RequireAny(Permission.IntegrationManage);
        ArgumentNullException.ThrowIfNull(request);
        var conexao = await _store.FindAsync(id, cancellationToken);
        if (conexao is null)
        {
            return null;
        }

        await _auth.RequireAsync(Permission.IntegrationManage, conexao.OrganizationId, "capabilities.connection.update", CapabilitiesAuthorizer.ConnectionResource, id, cancellationToken);
        RequireRevision(conexao.Revision, request.ExpectedRevision);

        var configuracao = Normalize(conexao.Key, request.Name, request.BaseUrl, request.AuthType, request.AuthSettings, request.DefaultHeaders, request.AllowedHeaders, request.HealthPath, request.TimeoutSeconds, request.MaxResponseBytes, requireKey: false);
        conexao.Update(configuracao.Name, configuracao.BaseUrl, request.AuthType, configuracao.AuthSettings, configuracao.DefaultHeaders, configuracao.AllowedHeaders, configuracao.HealthPath, configuracao.TimeoutSeconds, configuracao.MaxResponseBytes, _clock.GetUtcNow());
        await _store.SaveAsync(cancellationToken);
        await _auth.WriteAsync("capabilities.connection.update", CapabilitiesAuthorizer.ConnectionResource, id, AuditOutcome.Success, cancellationToken);
        return ConnectionView.From(conexao);
    }

    /// <summary>Define (ou troca) o segredo. Cifrado antes de gravar; a resposta é a visão SEM segredo. Sem chave de cifra configurada: 503 e nada é gravado.</summary>
    public async Task<ConnectionView?> SetSecretAsync(Guid id, ConnectionSecretRequest request, CancellationToken cancellationToken = default)
    {
        _auth.RequireAny(Permission.IntegrationManage);
        ArgumentNullException.ThrowIfNull(request);
        var conexao = await _store.FindAsync(id, cancellationToken);
        if (conexao is null)
        {
            return null;
        }

        await _auth.RequireAsync(Permission.IntegrationManage, conexao.OrganizationId, "capabilities.connection.set-secret", CapabilitiesAuthorizer.ConnectionResource, id, cancellationToken);
        RequireRevision(conexao.Revision, request.ExpectedRevision);

        var esperados = SecretFields[conexao.AuthType];
        if (esperados.Length == 0)
        {
            throw new CapabilitiesValidationException("secret: esta conexão não usa autenticação.");
        }

        var problemas = new List<string>();
        var valores = request.Values ?? new Dictionary<string, string>();
        foreach (var campo in esperados)
        {
            if (!valores.TryGetValue(campo, out var valor) || string.IsNullOrEmpty(valor))
            {
                problemas.Add($"secret.{campo}: obrigatório.");
            }
            else if (valor.Length > 2000 || valor.Any(char.IsControl))
            {
                problemas.Add($"secret.{campo}: inválido (até 2000 caracteres, sem controle).");
            }
        }

        if (valores.Keys.Any(k => !esperados.Contains(k, StringComparer.Ordinal)))
        {
            problemas.Add("secret: há campo que este tipo de autenticação não usa.");
        }

        if (problemas.Count > 0)
        {
            throw new CapabilitiesValidationException(problemas);
        }

        var proxima = conexao.SecretVersion + 1;
        var protegido = _protector.Protect(
            JsonSerializer.Serialize(esperados.ToDictionary(c => c, c => valores[c])),
            new SecretBinding("connection-secret", conexao.TenantId, conexao.Id, $"{(int)conexao.AuthType}:{proxima}"));
        conexao.SetSecret(protegido.Ciphertext, protegido.KeyId, _clock.GetUtcNow());
        await _store.SaveAsync(cancellationToken);
        await _auth.WriteAsync("capabilities.connection.set-secret", CapabilitiesAuthorizer.ConnectionResource, id, AuditOutcome.Success, cancellationToken);
        return ConnectionView.From(conexao);
    }

    /// <summary>Habilitar exige o segredo definido (se o tipo de autenticação o usa). Desabilitar vale já na próxima execução.</summary>
    public async Task<ConnectionView?> SetEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken = default)
    {
        _auth.RequireAny(Permission.IntegrationManage);
        var conexao = await _store.FindAsync(id, cancellationToken);
        if (conexao is null)
        {
            return null;
        }

        var operacao = enabled ? "capabilities.connection.enable" : "capabilities.connection.disable";
        await _auth.RequireAsync(Permission.IntegrationManage, conexao.OrganizationId, operacao, CapabilitiesAuthorizer.ConnectionResource, id, cancellationToken);

        if (enabled && conexao.AuthType != ConnectorAuthType.None && !conexao.HasSecret)
        {
            throw new CapabilitiesValidationException("enabled: defina o segredo antes de habilitar a conexão.");
        }

        if (_connectors.Get(conexao.Kind) is null)
        {
            throw new CapabilitiesValidationException("kind: não há conector registrado para este tipo.");
        }

        conexao.SetEnabled(enabled, _clock.GetUtcNow());
        await _store.SaveAsync(cancellationToken);
        await _auth.WriteAsync(operacao, CapabilitiesAuthorizer.ConnectionResource, id, AuditOutcome.Success, cancellationToken);
        return ConnectionView.From(conexao);
    }

    /// <summary>Sonda o alcance e a autenticação da conexão (uma chamada ao caminho de saúde). Não executa nenhuma operação do catálogo. Funciona mesmo com a conexão desabilitada (é como se prepara).</summary>
    public async Task<ConnectionTestResult?> TestAsync(Guid id, CancellationToken cancellationToken = default)
    {
        _auth.RequireAny(Permission.IntegrationTest);
        var conexao = await _store.FindAsync(id, cancellationToken);
        if (conexao is null)
        {
            return null;
        }

        await _auth.RequireAsync(Permission.IntegrationTest, conexao.OrganizationId, "capabilities.connection.test", CapabilitiesAuthorizer.ConnectionResource, id, cancellationToken);

        if (!TryTake(conexao))
        {
            return new ConnectionTestResult(false, "rate-limited", null, 0);
        }

        var conector = _connectors.Get(conexao.Kind);
        if (conector is null)
        {
            return new ConnectionTestResult(false, "connector-unavailable", null, 0);
        }

        ConnectorSecret segredo;
        try
        {
            segredo = CapabilityRuntime.ReadSecret(_protector, conexao);
        }
        catch (CapabilitiesUnavailableException)
        {
            await _auth.WriteAsync("capabilities.connection.test", CapabilitiesAuthorizer.ConnectionResource, id, AuditOutcome.Failed, cancellationToken);
            return new ConnectionTestResult(false, "secret-unavailable", null, 0);
        }

        var resultado = await conector.ProbeAsync(CapabilityRuntime.ToConnector(conexao, segredo), cancellationToken);
        var ok = resultado.Outcome == ConnectorOutcome.Success;
        await _auth.WriteAsync("capabilities.connection.test", CapabilitiesAuthorizer.ConnectionResource, id, ok ? AuditOutcome.Success : AuditOutcome.Failed, cancellationToken);
        return new ConnectionTestResult(ok, CapabilityRuntime.OutcomeCode(resultado.Outcome), resultado.UpstreamStatus, resultado.ElapsedMs);
    }

    private bool TryTake(IntegrationConnection conexao)
    {
        var agora = _clock.GetUtcNow();
        var fila = TestWindow.GetOrAdd((conexao.TenantId.Value, conexao.Id), _ => new Queue<DateTimeOffset>());
        lock (fila)
        {
            while (fila.Count > 0 && agora - fila.Peek() > TimeSpan.FromMinutes(1))
            {
                fila.Dequeue();
            }

            if (fila.Count >= _options.TestsPerConnectionPerMinute)
            {
                return false;
            }

            fila.Enqueue(agora);
            return true;
        }
    }

    private static void RequireRevision(int atual, int esperada)
    {
        if (atual != esperada)
        {
            throw new CapabilitiesConflictException("changed", "A conexão foi alterada por outra operação. Releia e tente de novo.");
        }
    }

    private sealed record Normalized(
        string Key,
        string Name,
        string BaseUrl,
        IReadOnlyDictionary<string, string> AuthSettings,
        IReadOnlyDictionary<string, string> DefaultHeaders,
        IReadOnlyList<string> AllowedHeaders,
        string HealthPath,
        int TimeoutSeconds,
        int MaxResponseBytes);

    private Normalized Normalize(
        string? key,
        string? name,
        string? baseUrl,
        ConnectorAuthType authType,
        IReadOnlyDictionary<string, string>? authSettings,
        IReadOnlyDictionary<string, string>? defaultHeaders,
        IReadOnlyList<string>? allowedHeaders,
        string? healthPath,
        int? timeoutSeconds,
        int? maxResponseBytes,
        bool requireKey)
    {
        var problemas = new List<string>();

        if (requireKey && !KeyRegex().IsMatch(key ?? string.Empty))
        {
            problemas.Add("key: letras minúsculas, números e hífen, começando por letra (2 a 40).");
        }

        var nome = (name ?? string.Empty).Trim();
        if (nome.Length is < 1 or > 100 || nome.Any(char.IsControl))
        {
            problemas.Add("name: de 1 a 100 caracteres.");
        }

        var url = (baseUrl ?? string.Empty).Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            problemas.Add("baseUrl: endereço inválido.");
        }
        else
        {
            var motivo = _policy.CheckUri(uri);
            if (motivo is not null)
            {
                problemas.Add($"baseUrl: destino não permitido ({motivo}).");
            }
            else if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            {
                problemas.Add("baseUrl: sem consulta nem fragmento (a consulta vem do molde da operação).");
            }
            else
            {
                url = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
            }
        }

        if (!Enum.IsDefined(authType))
        {
            problemas.Add("authType: inválido.");
        }

        var ajustes = new Dictionary<string, string>(authSettings ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        ValidateAuthSettings(authType, ajustes, problemas);

        var cabecalhos = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (nomeCab, valor) in defaultHeaders ?? new Dictionary<string, string>())
        {
            if (!OperationSpecRules.HeaderNameRegex().IsMatch(nomeCab) || OperationSpecRules.IsForbiddenHeader(nomeCab) || valor is null || valor.Length > 500 || valor.Any(char.IsControl))
            {
                problemas.Add("defaultHeaders: cabeçalho inválido ou reservado.");
                continue;
            }

            cabecalhos[nomeCab] = valor;
        }

        var permitidos = new List<string>();
        foreach (var cab in allowedHeaders ?? [])
        {
            if (!OperationSpecRules.HeaderNameRegex().IsMatch(cab) || OperationSpecRules.IsForbiddenHeader(cab))
            {
                problemas.Add("allowedHeaders: cabeçalho inválido ou reservado.");
            }
            else if (!permitidos.Contains(cab, StringComparer.OrdinalIgnoreCase))
            {
                permitidos.Add(cab);
            }
        }

        if (permitidos.Count > 20 || cabecalhos.Count > 20)
        {
            problemas.Add("headers: no máximo 20 cabeçalhos.");
        }

        var saude = string.IsNullOrWhiteSpace(healthPath) ? "/" : healthPath.Trim();
        if (saude.Length > 200 || !saude.StartsWith('/') || saude.Contains("..", StringComparison.Ordinal) || saude.Any(c => char.IsControl(c) || c is '\\' or '#' || char.IsWhiteSpace(c)))
        {
            problemas.Add("healthPath: precisa começar com / (sem .., espaço nem fragmento).");
        }

        var tempo = timeoutSeconds ?? _options.DefaultTimeoutSeconds;
        if (tempo < 1 || tempo > _options.MaxTimeoutSeconds)
        {
            problemas.Add($"timeoutSeconds: de 1 a {_options.MaxTimeoutSeconds}.");
        }

        var tamanho = maxResponseBytes ?? _options.DefaultMaxResponseBytes;
        if (tamanho < 1024 || tamanho > _options.MaxResponseBytesCeiling)
        {
            problemas.Add($"maxResponseBytes: de 1024 a {_options.MaxResponseBytesCeiling}.");
        }

        if (problemas.Count > 0)
        {
            throw new CapabilitiesValidationException(problemas);
        }

        return new Normalized(key ?? string.Empty, nome, url, ajustes, cabecalhos, permitidos, saude, tempo, tamanho);
    }

    private void ValidateAuthSettings(ConnectorAuthType authType, Dictionary<string, string> ajustes, List<string> problemas)
    {
        string[] permitidos = authType switch
        {
            ConnectorAuthType.ApiKey => ["location", "name", "prefix"],
            ConnectorAuthType.OAuth2ClientCredentials => ["tokenUrl", "scope", "clientAuth"],
            _ => [],
        };

        if (ajustes.Keys.Any(k => !permitidos.Contains(k, StringComparer.Ordinal)))
        {
            problemas.Add("authSettings: há campo que este tipo de autenticação não usa.");
            return;
        }

        if (authType == ConnectorAuthType.ApiKey)
        {
            ajustes.TryAdd("location", "header");
            if (ajustes["location"] is not ("header" or "query"))
            {
                problemas.Add("authSettings.location: header ou query.");
            }

            var nomeValido = ajustes.TryGetValue("name", out var nome)
                && (ajustes["location"] == "query" ? QueryNameRegex().IsMatch(nome) : OperationSpecRules.HeaderNameRegex().IsMatch(nome) && !IsReservedForApiKey(nome));
            if (!nomeValido)
            {
                problemas.Add("authSettings.name: nome do cabeçalho ou do parâmetro de consulta inválido.");
            }

            if (ajustes.TryGetValue("prefix", out var prefixo) && (prefixo.Length > 30 || prefixo.Any(char.IsControl)))
            {
                problemas.Add("authSettings.prefix: até 30 caracteres, sem controle.");
            }
        }
        else if (authType == ConnectorAuthType.OAuth2ClientCredentials)
        {
            if (!ajustes.TryGetValue("tokenUrl", out var tokenUrl) || !Uri.TryCreate(tokenUrl, UriKind.Absolute, out var uri))
            {
                problemas.Add("authSettings.tokenUrl: endereço do token obrigatório.");
            }
            else if (_policy.CheckUri(uri) is { } motivo)
            {
                problemas.Add($"authSettings.tokenUrl: destino não permitido ({motivo}).");
            }

            ajustes.TryAdd("clientAuth", "body");
            if (ajustes["clientAuth"] is not ("body" or "basic"))
            {
                problemas.Add("authSettings.clientAuth: body ou basic.");
            }

            if (ajustes.TryGetValue("scope", out var escopo) && (escopo.Length > 200 || escopo.Any(char.IsControl)))
            {
                problemas.Add("authSettings.scope: até 200 caracteres, sem controle.");
            }
        }
    }

    // Os cabeçalhos de identidade só podem ser o "Authorization" da própria autenticação (nome liberado aqui) e outros nomes comuns de chave; os de enquadramento HTTP nunca.
    private static bool IsReservedForApiKey(string nome) => OperationSpecRules.IsForbiddenHeader(nome) && !nome.Equals("Authorization", StringComparison.OrdinalIgnoreCase);
}
