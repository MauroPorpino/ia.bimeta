namespace IaBimeta.Conversations.Contracts.Commands;

/// <summary>
/// O ciclo do arquivo de uma mensagem de mídia (ADR 0028), pelo contexto de SISTEMA do tenant (o job do Channels, no Worker). O Channels busca o arquivo no provedor e o entrega aqui; quem valida o conteúdo
/// (tamanho, tipo permitido, assinatura do arquivo, integridade) e o guarda em armazenamento privado é o Conversations, dono da mensagem. Tudo é idempotente: repetir não duplica arquivo nem muda o desfecho.
/// </summary>
public interface IAttachmentCommands
{
    /// <summary>O que o Channels precisa para buscar o arquivo: o identificador no provedor e os limites desta mídia. Nulo se a mensagem não tem arquivo neste tenant.</summary>
    Task<AttachmentFetchPlan?> GetFetchPlanAsync(Guid messageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Entrega o arquivo baixado: valida e guarda. Já resolvido (disponível, indisponível ou recusado), nada muda. Conteúdo fora da política resulta em <see cref="AttachmentChangeOutcome.Rejected"/>, com o
    /// motivo gravado, e nada é guardado.
    /// </summary>
    Task<AttachmentChangeResult> CompleteAsync(CompleteAttachmentCommand command, CancellationToken cancellationToken = default);

    /// <summary>Registra que o arquivo não pôde ser obtido (<see cref="AttachmentStatus.Unavailable"/>) ou foi recusado antes do download (<see cref="AttachmentStatus.Rejected"/>). Só sai de <c>Pending</c>.</summary>
    Task<AttachmentChangeResult> FailAsync(FailAttachmentCommand command, CancellationToken cancellationToken = default);
}

/// <param name="ProviderMediaId">O identificador da mídia no provedor.</param>
/// <param name="MaxBytes">O maior tamanho aceito para este tipo de mídia (configurável): o download passa dele é recusado.</param>
/// <param name="AllowedMimeTypes">Os tipos aceitos para este tipo de mídia (configurável).</param>
public sealed record AttachmentFetchPlan(
    Guid MessageId,
    Guid SessionId,
    AttachmentStatus Status,
    MessageKind Kind,
    string ProviderMediaId,
    string? DeclaredMimeType,
    long MaxBytes,
    IReadOnlyList<string> AllowedMimeTypes);

/// <param name="Content">O arquivo baixado (já limitado ao tamanho do plano).</param>
/// <param name="ProviderMimeType">O tipo que o provedor informou ao entregar o arquivo (vale sobre o declarado na mensagem).</param>
/// <param name="ProviderSha256">O resumo SHA-256 que o provedor informou (hexadecimal ou Base64), se houver: confere a integridade do que foi baixado.</param>
public sealed record CompleteAttachmentCommand(Guid MessageId, byte[] Content, string? ProviderMimeType, string? ProviderSha256);

/// <param name="Status"><see cref="AttachmentStatus.Unavailable"/> ou <see cref="AttachmentStatus.Rejected"/>.</param>
/// <param name="Code">O motivo (código curto, sem texto do provedor).</param>
public sealed record FailAttachmentCommand(Guid MessageId, AttachmentStatus Status, string Code);

public enum AttachmentChangeOutcome
{
    /// <summary>O arquivo foi guardado (ou o estado foi registrado) agora.</summary>
    Changed = 1,

    /// <summary>O arquivo já estava resolvido: nada mudou.</summary>
    AlreadyResolved = 2,

    NotFound = 3,

    /// <summary>O conteúdo violou a política e o arquivo ficou <see cref="AttachmentStatus.Rejected"/> (o motivo está em <see cref="AttachmentChangeResult.FailureCode"/>).</summary>
    Rejected = 4,
}

public sealed record AttachmentChangeResult(AttachmentChangeOutcome Outcome, AttachmentStatus? Status, string? FailureCode);
