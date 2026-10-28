using IaBimeta.Channels.Contracts;
using IaBimeta.Channels.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Channels.Application;

/// <summary>Visão de uma conta entregue à camada de apresentação (nunca a entidade). Não traz valor de credencial algum.</summary>
public sealed record ChannelAccountView(
    Guid Id,
    Guid OrganizationId,
    ChannelProvider Provider,
    string ProviderAccountId,
    string Name,
    DateTimeOffset CreatedAt,
    IReadOnlyList<ChannelSecretView> Secrets)
{
    internal static ChannelAccountView From(ChannelAccount account, IEnumerable<ChannelSecret> secrets) =>
        new(account.Id, account.OrganizationId, account.Provider, account.ProviderAccountId, account.Name, account.CreatedAt,
            secrets.OrderBy(s => s.Kind).Select(ChannelSecretView.From).ToList());
}

/// <summary>
/// Os metadados de uma credencial: o papel, a referência e quando mudou. NUNCA o valor, nem cifrado: a credencial é só de
/// escrita pela API.
/// </summary>
public sealed record ChannelSecretView(ChannelSecretKind Kind, Guid Reference, int Version, DateTimeOffset UpdatedAt)
{
    internal static ChannelSecretView From(ChannelSecret secret) => new(secret.Kind, secret.Id, secret.Version, secret.UpdatedAt);
}

public sealed record ChannelInstanceView(
    Guid Id,
    Guid AccountId,
    Guid OrganizationId,
    ChannelProvider Provider,
    string PhoneNumberId,
    string? DisplayPhoneNumber,
    string Name,
    ChannelStatus Status,
    Locale? DefaultLocale,
    Guid? DefaultBusinessUnitId,
    Guid? DefaultDepartmentId,
    IReadOnlyList<Guid> AuthorizedBusinessUnitIds,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    internal static ChannelInstanceView From(ChannelInstance i) =>
        new(i.Id.Value, i.AccountId, i.OrganizationId, i.Provider, i.PhoneNumberId, i.DisplayPhoneNumber, i.Name, i.Status,
            i.DefaultLocale, i.DefaultBusinessUnitId, i.DefaultDepartmentId,
            i.AuthorizedUnits.Select(u => u.BusinessUnitId).Order().ToList(), i.Version, i.CreatedAt, i.UpdatedAt);
}
