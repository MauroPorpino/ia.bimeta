using System.Text.RegularExpressions;

namespace IaBimeta.Conversations.Domain;

/// <summary>Limites e validações de formato do módulo. Valores fixos: não há configuração por tenant (CONVERSATION-LIFECYCLE.md §14).</summary>
public static class ConversationLimits
{
    public const int TextMaxLength = 4096;
    public const int ContactRefMaxLength = 128;
    public const int DisplayNameMaxLength = 120;
    public const int ProviderEventIdMaxLength = 200;
    public const int ReasonMaxLength = 100;

    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    public const int DefaultSnapshotMessages = 50;
    public const int MaxSnapshotMessages = 200;

    /// <summary>Motivos são códigos curtos (<c>manual</c>, <c>chatty-burst</c>), sem espaços: nada de texto livre que possa carregar segredo.</summary>
    private static readonly Regex ReasonPattern = new("^[a-z0-9][a-z0-9_.-]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <exception cref="ConversationsValidationException">O motivo está fora do formato.</exception>
    public static string ValidateReason(string? reason)
    {
        if (string.IsNullOrEmpty(reason) || reason.Length > ReasonMaxLength || !ReasonPattern.IsMatch(reason))
        {
            throw new ConversationsValidationException($"O motivo deve ter de 1 a {ReasonMaxLength} caracteres: letras minúsculas, números e '_.-'.");
        }

        return reason;
    }

    /// <summary>A origem de uma tradução é um código curto (<c>human</c>, <c>machine.provider-x</c>), no mesmo formato dos motivos: sem texto livre.</summary>
    /// <exception cref="ConversationsValidationException">A origem está fora do formato.</exception>
    public static string ValidateProvenance(string? provenance)
    {
        if (string.IsNullOrEmpty(provenance) || provenance.Length > ReasonMaxLength || !ReasonPattern.IsMatch(provenance))
        {
            throw new ConversationsValidationException($"A origem da tradução deve ter de 1 a {ReasonMaxLength} caracteres: letras minúsculas, números e '_.-'.");
        }

        return provenance;
    }
}
