using IaBimeta.Channels.Contracts;
using IaBimeta.SharedKernel;

namespace IaBimeta.Channels.Domain;

/// <summary>
/// Um número de atendimento do tenant (no WhatsApp, um Phone Number ID de uma WABA). Pertence a exatamente um tenant e a uma
/// organização, e pode servir várias unidades: <see cref="AuthorizedUnits"/> é a lista das únicas unidades a que este número
/// pode atender, e ela vale no roteamento inicial e em toda transferência posterior. A unidade padrão (último recurso de um
/// número corporativo) precisa estar nessa lista; configuração inválida é recusada aqui, nunca corrigida por escolha
/// arbitrária de outra unidade.
/// <para>
/// A lista, os padrões e a situação formam um agregado: qualquer mudança sobe <see cref="Version"/>, que é a marca de
/// concorrência. Duas alterações simultâneas sobre o mesmo canal não se sobrepõem em silêncio: a segunda falha.
/// </para>
/// </summary>
public sealed class ChannelInstance : ITenantOwned
{
    public const int NameMaxLength = 120;
    public const int DisplayPhoneMaxLength = 32;

    private readonly List<ChannelInstanceUnit> _authorizedUnits = new();

    // Usado pela fábrica e pelo EF Core (que liga os parâmetros às propriedades pelo nome).
    private ChannelInstance(
        ChannelInstanceId id,
        TenantId tenantId,
        Guid accountId,
        Guid organizationId,
        ChannelProvider provider,
        string phoneNumberId,
        string? displayPhoneNumber,
        string name,
        DateTimeOffset createdAt)
    {
        Id = id;
        TenantId = tenantId;
        AccountId = accountId;
        OrganizationId = organizationId;
        Provider = provider;
        PhoneNumberId = phoneNumberId;
        DisplayPhoneNumber = displayPhoneNumber;
        Name = name;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
        Status = ChannelStatus.Inactive;
        Version = 1;
    }

    public ChannelInstanceId Id { get; }

    public TenantId TenantId { get; }

    public Guid AccountId { get; }

    /// <summary>A organização do canal, igual à da conta. É ela que decide quem pode ler e gerir (TEN-2).</summary>
    public Guid OrganizationId { get; }

    public ChannelProvider Provider { get; }

    /// <summary>O identificador do número no provedor (Phone Number ID). Imutável: trocar de número é cadastrar outra instância.</summary>
    public string PhoneNumberId { get; }

    /// <summary>O número como as pessoas o leem (por exemplo, <c>+55 11 99999-0000</c>). Só rótulo: nunca identifica nem roteia.</summary>
    public string? DisplayPhoneNumber { get; }

    public string Name { get; }

    public ChannelStatus Status { get; private set; }

    /// <summary>O idioma padrão deste número, ou <c>null</c> se o canal não define (vale então o do tenant).</summary>
    public Locale? DefaultLocale { get; private set; }

    public Guid? DefaultBusinessUnitId { get; private set; }

    public Guid? DefaultDepartmentId { get; private set; }

    /// <summary>Marca de concorrência do agregado (instância, unidades autorizadas e padrões).</summary>
    public int Version { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyList<ChannelInstanceUnit> AuthorizedUnits => _authorizedUnits;

    public static ChannelInstance Register(
        TenantId tenantId,
        ChannelAccount account,
        string phoneNumberId,
        string? displayPhoneNumber,
        string name,
        Locale? defaultLocale,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(account);

        if (account.TenantId != tenantId)
        {
            throw new ChannelsValidationException("A conta não pertence ao tenant da operação.");
        }

        var nomeValido = (name ?? string.Empty).Trim();
        if (nomeValido.Length == 0 || nomeValido.Length > NameMaxLength)
        {
            throw new ChannelsValidationException($"O nome do canal deve ter de 1 a {NameMaxLength} caracteres.");
        }

        var instancia = new ChannelInstance(
            ChannelInstanceId.New(),
            tenantId,
            account.Id,
            account.OrganizationId,
            account.Provider,
            ProviderIdentifier.Normalize(phoneNumberId, "do número"),
            NormalizeDisplayPhone(displayPhoneNumber),
            nomeValido,
            now);
        instancia.DefaultLocale = defaultLocale;
        return instancia;
    }

    public bool IsUnitAuthorized(Guid businessUnitId) => _authorizedUnits.Any(u => u.BusinessUnitId == businessUnitId);

    /// <summary>Autoriza o número a atender a unidade. Repetir é inofensivo (falso: nada mudou).</summary>
    public bool AuthorizeUnit(Guid businessUnitId, Guid? authorizedBy, DateTimeOffset now)
    {
        if (businessUnitId == Guid.Empty)
        {
            throw new ChannelsValidationException("A unidade é obrigatória.");
        }

        if (IsUnitAuthorized(businessUnitId))
        {
            return false;
        }

        _authorizedUnits.Add(ChannelInstanceUnit.Create(TenantId, Id, businessUnitId, authorizedBy, now));
        Touch(now);
        return true;
    }

    /// <summary>
    /// Retira a unidade das autorizadas. Não retira a unidade padrão (troque o padrão antes) nem a última unidade de um canal
    /// ativo (um canal ativo sem unidade recusaria todo atendimento). Falso: a unidade já não estava autorizada.
    /// </summary>
    public bool RevokeUnit(Guid businessUnitId, DateTimeOffset now)
    {
        var autorizada = _authorizedUnits.FirstOrDefault(u => u.BusinessUnitId == businessUnitId);
        if (autorizada is null)
        {
            return false;
        }

        if (DefaultBusinessUnitId == businessUnitId)
        {
            throw new ChannelsValidationException("Esta é a unidade padrão do canal: defina outra unidade padrão (ou nenhuma) antes de retirá-la das autorizadas.");
        }

        if (Status == ChannelStatus.Active && _authorizedUnits.Count == 1)
        {
            throw new ChannelsValidationException("Um canal ativo precisa de ao menos uma unidade autorizada: suspenda o canal antes de retirar a última.");
        }

        _authorizedUnits.Remove(autorizada);
        Touch(now);
        return true;
    }

    /// <summary>
    /// Define a unidade e o setor padrão (o último recurso quando nem o cliente nem uma regra escolhem). A unidade precisa estar
    /// entre as autorizadas e o setor exige a unidade; que o setor seja da unidade é conferido pelo caso de uso, no Tenancy.
    /// Sem unidade (nulo) o canal não tem padrão.
    /// </summary>
    public bool SetDefaults(Guid? businessUnitId, Guid? departmentId, DateTimeOffset now)
    {
        if (businessUnitId == Guid.Empty || departmentId == Guid.Empty)
        {
            throw new ChannelsValidationException("O identificador da unidade ou do setor padrão é inválido.");
        }

        if (departmentId is not null && businessUnitId is null)
        {
            throw new ChannelsValidationException("O setor padrão exige a unidade padrão.");
        }

        if (businessUnitId is { } unidade && !IsUnitAuthorized(unidade))
        {
            throw new ChannelsValidationException("A unidade padrão precisa estar entre as unidades autorizadas do canal.");
        }

        if (DefaultBusinessUnitId == businessUnitId && DefaultDepartmentId == departmentId)
        {
            return false;
        }

        DefaultBusinessUnitId = businessUnitId;
        DefaultDepartmentId = departmentId;
        Touch(now);
        return true;
    }

    public bool ChangeDefaultLocale(Locale? locale, DateTimeOffset now)
    {
        if (Equals(DefaultLocale, locale))
        {
            return false;
        }

        DefaultLocale = locale;
        Touch(now);
        return true;
    }

    /// <summary>Ativa (ou reativa) o canal. Exige ao menos uma unidade autorizada. Falso: já estava ativo.</summary>
    public bool Activate(DateTimeOffset now)
    {
        if (Status == ChannelStatus.Active)
        {
            return false;
        }

        if (_authorizedUnits.Count == 0)
        {
            throw new ChannelsValidationException("Para ativar o canal, autorize ao menos uma unidade.");
        }

        Status = ChannelStatus.Active;
        Touch(now);
        return true;
    }

    /// <summary>Suspende um canal ativo. Falso: já estava suspenso. Um canal ainda não ativado não se suspende.</summary>
    public bool Suspend(DateTimeOffset now)
    {
        if (Status == ChannelStatus.Suspended)
        {
            return false;
        }

        if (Status != ChannelStatus.Active)
        {
            throw new ChannelsValidationException("Só um canal ativo pode ser suspenso.");
        }

        Status = ChannelStatus.Suspended;
        Touch(now);
        return true;
    }

    private void Touch(DateTimeOffset now)
    {
        Version++;
        UpdatedAt = now;
    }

    private static string? NormalizeDisplayPhone(string? value)
    {
        var texto = value?.Trim();
        if (string.IsNullOrEmpty(texto))
        {
            return null;
        }

        if (texto.Length > DisplayPhoneMaxLength || texto.Any(c => !(char.IsAsciiDigit(c) || c is '+' or ' ' or '(' or ')' or '-')))
        {
            throw new ChannelsValidationException($"O número de exibição deve ter até {DisplayPhoneMaxLength} caracteres: dígitos, +, espaço, parênteses e hífen.");
        }

        return texto;
    }
}
