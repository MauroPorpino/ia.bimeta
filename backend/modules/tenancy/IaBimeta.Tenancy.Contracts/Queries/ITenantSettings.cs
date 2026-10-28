using IaBimeta.SharedKernel;

namespace IaBimeta.Tenancy.Contracts.Queries;

/// <summary>
/// Configurações do tenant do contexto da operação. O tenant vem sempre do <see cref="RequestContext"/> confiável, nunca de
/// parâmetro: um módulo só enxerga as configurações do tenant em que está operando.
/// </summary>
public interface ITenantSettings
{
    /// <summary>
    /// O idioma padrão do tenant (BCP 47). É o último recurso para escolher o idioma de uma conversa e nunca é o idioma
    /// presumido do cliente: quem o usa registra que a escolha foi uma suposição.
    /// </summary>
    Task<Locale> GetDefaultLocaleAsync(CancellationToken cancellationToken = default);
}
