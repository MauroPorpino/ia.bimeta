using IaBimeta.Channels.Contracts;
using IaBimeta.Channels.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Channels.Presentation;

/// <summary>Nenhum DTO aceita tenant: o tenant é sempre o do contexto confiável criado pelo host.</summary>
public sealed record CreateChannelAccountRequest(Guid OrganizationId, ChannelProvider Provider, string ProviderAccountId, string Name);

/// <summary>
/// O valor da credencial. Só entra, nunca sai: o <c>ToString</c> é sobrescrito para que um log do objeto, uma mensagem de erro
/// ou um depurador não o exponham.
/// </summary>
public sealed record SetChannelSecretRequest(string Value)
{
    public override string ToString() => "SetChannelSecretRequest { Value = [oculto] }";
}

public sealed record RegisterChannelInstanceRequest(Guid AccountId, string Name, string PhoneNumberId, string? DisplayPhoneNumber, Locale? DefaultLocale);

public sealed record SetChannelDefaultsRequest(Guid? DefaultBusinessUnitId, Guid? DefaultDepartmentId);

public sealed record SetChannelDefaultLocaleRequest(Locale? DefaultLocale);
