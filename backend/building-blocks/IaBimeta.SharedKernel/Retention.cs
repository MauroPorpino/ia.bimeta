namespace IaBimeta.SharedKernel;

/// <summary>
/// As categorias TÉCNICAS que a política de retenção expurga sozinha (ADR 0025): estado operacional sem valor de negócio depois de um prazo. Dados pessoais e registros de referência legal
/// (mensagens, contatos, atendimentos, trilha de auditoria, ledger de IA) NÃO estão aqui: o expurgo deles depende de decisão jurídica e de regras de referência, e hoje só se relata o que existe.
/// </summary>
public enum RetentionCategory
{
    /// <summary>Sessões de login vencidas ou revogadas (só o hash do token e o vínculo ao usuário).</summary>
    Sessions = 1,

    /// <summary>Tokens de redefinição de senha usados, revogados ou vencidos (só o hash).</summary>
    PasswordResetTokens = 2,

    /// <summary>Contadores de falhas de login sem bloqueio ativo e com janela encerrada.</summary>
    LoginThrottles = 3,

    /// <summary>Eventos do outbox já PROCESSADOS: o payload do evento do canal carrega o texto do cliente, e é ele que expira aqui. Os mortos nunca entram (ver ADR 0011, DP-06).</summary>
    ProcessedEvents = 4,

    /// <summary>Marcas de idempotência dos consumidores (só identificadores).</summary>
    InboxMarkers = 5,
}

/// <summary>
/// A retenção em dias, por ambiente (seção <c>Retention</c>) e por empresa (<c>Retention:Tenants:&lt;slug&gt;</c>, que só sobrescreve as categorias citadas). Os padrões são os da política inicial aprovada (DP-06): eventos
/// processados 7 dias, marcas do Inbox 30 dias. Valores inválidos falham na partida, nunca no meio de um expurgo.
/// </summary>
public sealed class RetentionOptions
{
    public const string SectionName = "Retention";

    public const int MaximumDays = 3650;

    /// <summary>Os dias por categoria. O binder acrescenta ou troca só as chaves informadas; as demais ficam com o padrão.</summary>
    public Dictionary<RetentionCategory, int> Days { get; set; } = new()
    {
        [RetentionCategory.Sessions] = 30,
        [RetentionCategory.PasswordResetTokens] = 30,
        [RetentionCategory.LoginThrottles] = 7,
        [RetentionCategory.ProcessedEvents] = 7,
        [RetentionCategory.InboxMarkers] = 30,
    };

    /// <summary>Sobrescritas por empresa (o slug do tenant), só das categorias citadas.</summary>
    public Dictionary<string, Dictionary<RetentionCategory, int>> Tenants { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public int DaysFor(RetentionCategory category, string? tenantSlug = null)
    {
        if (tenantSlug is not null && Tenants.TryGetValue(tenantSlug, out var sobrescritas) && sobrescritas.TryGetValue(category, out var dias))
        {
            return dias;
        }

        return Days[category];
    }

    /// <exception cref="InvalidOperationException">Algum valor é inválido.</exception>
    public void Validate()
    {
        foreach (var categoria in Enum.GetValues<RetentionCategory>())
        {
            if (!Days.ContainsKey(categoria))
            {
                throw new InvalidOperationException($"Retention:Days:{categoria} não pode ser removido.");
            }
        }

        var escopos = new List<(string Nome, string? Slug)> { ("padrão", null) };
        foreach (var slug in Tenants.Keys)
        {
            if (string.IsNullOrWhiteSpace(slug))
            {
                throw new InvalidOperationException("Retention:Tenants tem uma empresa sem nome.");
            }

            escopos.Add(($"empresa '{slug}'", slug));
        }

        foreach (var (nome, slug) in escopos)
        {
            foreach (var categoria in Enum.GetValues<RetentionCategory>())
            {
                var dias = DaysFor(categoria, slug);
                if (dias is < 1 or > MaximumDays)
                {
                    throw new InvalidOperationException($"A retenção de {categoria} ({nome}) deve ficar entre 1 e {MaximumDays} dias.");
                }
            }

            // A marca de idempotência tem de sobreviver ao evento que ela protege: nunca expira antes do evento processado.
            if (DaysFor(RetentionCategory.InboxMarkers, slug) <= DaysFor(RetentionCategory.ProcessedEvents, slug))
            {
                throw new InvalidOperationException($"A retenção das marcas do Inbox ({nome}) deve ser maior que a dos eventos processados.");
            }
        }
    }
}

/// <summary>
/// Uma passada do expurgo: ou UMA empresa com a retenção própria (<see cref="TenantId"/>), ou TODAS as demais (<see cref="TenantId"/> nulo, menos as de <see cref="ExcludedTenants"/>, que têm passada própria).
/// O corte (<see cref="Cutoff"/>) já vem calculado: o que for anterior a ele pode ser expurgado.
/// </summary>
public sealed record RetentionPass(Guid? TenantId, IReadOnlyList<Guid> ExcludedTenants, int Days, DateTimeOffset Cutoff, string Label)
{
    /// <summary>Se o tenant está dentro desta passada.</summary>
    public bool Includes(Guid tenantId) => TenantId is { } proprio ? proprio == tenantId : !ExcludedTenants.Contains(tenantId);
}

/// <summary>De onde o expurgo tira o prazo de cada categoria e a quem ele se aplica.</summary>
public interface IRetentionPolicy
{
    /// <summary>As passadas da categoria: uma por empresa com sobrescrita e uma para as demais. Falha se a configuração cita empresa que não existe (falha fechada: nunca expurga com um prazo suposto).</summary>
    Task<IReadOnlyList<RetentionPass>> PassesAsync(RetentionCategory category, DateTimeOffset now, CancellationToken cancellationToken = default);
}

/// <summary>A política lida da configuração. O slug vira o tenant pela função recebida (o host usa o diretório de tenants), porque o SharedKernel não conhece o Tenancy.</summary>
public sealed class ConfiguredRetentionPolicy : IRetentionPolicy
{
    private readonly RetentionOptions _options;
    private readonly Func<string, CancellationToken, Task<Guid?>> _resolveTenant;

    public ConfiguredRetentionPolicy(RetentionOptions options, Func<string, CancellationToken, Task<Guid?>> resolveTenant)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(resolveTenant);
        options.Validate();
        _options = options;
        _resolveTenant = resolveTenant;
    }

    public async Task<IReadOnlyList<RetentionPass>> PassesAsync(RetentionCategory category, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var proprias = new List<RetentionPass>();
        var excluidas = new List<Guid>();
        foreach (var (slug, sobrescritas) in _options.Tenants)
        {
            if (!sobrescritas.TryGetValue(category, out var dias))
            {
                continue;
            }

            var tenant = await _resolveTenant(slug, cancellationToken)
                ?? throw new InvalidOperationException($"A empresa '{slug}' de Retention:Tenants não existe: a retenção não será aplicada com um prazo suposto.");
            excluidas.Add(tenant);
            proprias.Add(new RetentionPass(tenant, Array.Empty<Guid>(), dias, now - TimeSpan.FromDays(dias), $"empresa '{slug}'"));
        }

        var padrao = _options.Days[category];
        var passadas = new List<RetentionPass> { new(null, excluidas, padrao, now - TimeSpan.FromDays(padrao), "padrão") };
        passadas.AddRange(proprias);
        return passadas;
    }
}

/// <summary>O resultado de uma tarefa numa passada. <paramref name="Skipped"/> preenchido = não rodou, e o motivo.</summary>
public sealed record RetentionTaskResult(string Task, RetentionCategory Category, string Pass, int Days, int Eligible, int Deleted, string? Skipped = null);

/// <summary>Uma tarefa de expurgo de UMA tabela técnica de UM módulo. Só dado técnico; nunca dado pessoal nem registro de referência legal.</summary>
public interface IRetentionTask
{
    /// <summary>O nome estável (<c>modulo.tabela</c>).</summary>
    string Name { get; }

    RetentionCategory Category { get; }

    /// <param name="dryRun">Só conta o que seria expurgado (<c>Deleted</c> fica 0).</param>
    Task<RetentionTaskResult> RunAsync(RetentionPass pass, bool dryRun, CancellationToken cancellationToken = default);
}

/// <summary>Quem sabe dizer quantos eventos MORTOS existem (e o mais antigo). Um morto pode ser reenfileirado, e reenfileirar depois de o Inbox esquecer a marca poderia repetir o efeito: o expurgo das marcas confere isto antes.</summary>
public interface IDeadLetterProbe
{
    string Name { get; }

    Task<(int Count, DateTimeOffset? Oldest)> DeadAsync(RetentionPass pass, CancellationToken cancellationToken = default);
}
