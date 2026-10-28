namespace IaBimeta.Tenancy.Presentation;

/// <summary>Corpo da troca do idioma padrão do tenant. Sem campo de tenant: o tenant vem do contexto confiável.</summary>
public sealed record SetDefaultLocaleRequest(string? Locale);

public sealed record TenantSettingsResponse(string DefaultLocale);
