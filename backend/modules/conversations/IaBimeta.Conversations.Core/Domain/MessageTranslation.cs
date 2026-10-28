using IaBimeta.Conversations.Contracts;
using IaBimeta.SharedKernel;

namespace IaBimeta.Conversations.Domain;

/// <summary>
/// Tradução de uma mensagem, guardada À PARTE: o texto da <see cref="Message"/> original nunca é alterado nem sobrescrito, e
/// a tradução nunca substitui o original em nenhuma leitura. A chave é <c>(tenant_id, id)</c>, a referência à mensagem carrega
/// o tenant (o banco recusa tradução ligada à mensagem de outro tenant) e há no máximo uma tradução por mensagem e idioma de
/// destino. Imutável depois de criada. Nenhum serviço de tradução existe ainda: só a estrutura que ele vai usar.
/// </summary>
public sealed class MessageTranslation : ITenantOwned
{
    private MessageTranslation()
    {
        TenantId = null!;
        TargetLocale = null!;
        Text = string.Empty;
        Provenance = string.Empty;
    }

    public Guid Id { get; private set; }

    public TenantId TenantId { get; private set; }

    public Guid MessageId { get; private set; }

    public Locale TargetLocale { get; private set; }

    /// <summary>O idioma da mensagem original no momento da tradução, se se sabia.</summary>
    public Locale? SourceLocale { get; private set; }

    public string Text { get; private set; }

    /// <summary>Código curto de quem produziu a tradução (<c>human</c>, <c>machine.provider-x</c>).</summary>
    public string Provenance { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Só se traduz conteúdo pronto: o chamador confere <see cref="Message.ContentState"/> antes.</summary>
    public static MessageTranslation Create(TenantId tenantId, Message original, Locale targetLocale, string text, string provenance, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(targetLocale);

        Message.ValidateText(text, MessageKind.Text, required: true);
        var origem = ConversationLimits.ValidateProvenance(provenance);

        return new MessageTranslation
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            MessageId = original.Id,
            TargetLocale = targetLocale,
            SourceLocale = original.Locale,
            Text = text,
            Provenance = origem,
            CreatedAt = now,
        };
    }
}
