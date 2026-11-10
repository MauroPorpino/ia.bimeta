using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Commands;
using IaBimeta.Conversations.Contracts.Queries;
using IaBimeta.Conversations.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Conversations.Application;

/// <summary>
/// O ciclo e a abertura do arquivo de uma mensagem de mídia (ADR 0028). O estado do arquivo é do Conversations: o Channels só busca no provedor e entrega os bytes (<see cref="IAttachmentCommands"/>, contexto de
/// SISTEMA); o conteúdo é validado pela <see cref="AttachmentPolicy"/> ANTES de ser guardado, e só então a linha passa a <c>Available</c> (o objeto no armazenamento tem chave por arquivo, então repetir
/// regrava o mesmo objeto). A abertura (<see cref="IAttachmentQueries"/>) respeita a permissão de leitura da sessão e só devolve arquivo disponível DA sessão informada.
/// </summary>
public sealed class AttachmentService : IAttachmentCommands, IAttachmentQueries, IOutboundAttachmentReader
{
    private readonly IConversationStore _store;
    private readonly IAttachmentContentStore _content;
    private readonly IAttachmentScanner _scanner;
    private readonly AttachmentOptions _options;
    private readonly IRequestContextAccessor _context;
    private readonly TimeProvider _clock;

    public AttachmentService(
        IConversationStore store, IAttachmentContentStore content, IAttachmentScanner scanner, AttachmentOptions options, IRequestContextAccessor context, TimeProvider clock)
    {
        _store = store;
        _content = content;
        _scanner = scanner;
        _options = options;
        _context = context;
        _clock = clock;
    }

    public async Task<AttachmentFetchPlan?> GetFetchPlanAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        _context.GetRequired().Require(Permission.SystemOperation);

        // Só o arquivo do CLIENTE tem o que buscar no provedor: o que o atendente enviou (ADR 0031) já nasce guardado e não tem plano de busca.
        var anexo = await _store.FindAttachmentAsync(messageId, cancellationToken);
        return anexo is not { Direction: MessageDirection.Inbound, ProviderMediaId: { } identificador }
            ? null
            : new AttachmentFetchPlan(
                anexo.MessageId, anexo.SessionId, anexo.Status, anexo.Kind, identificador, anexo.DeclaredMimeType, _options.MaxBytesFor(anexo.Kind), _options.AllowedTypesFor(anexo.Kind));
    }

    public async Task<Stream?> OpenAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        contexto.Require(Permission.SystemOperation);

        // Só o arquivo de uma mensagem de SAÍDA do tenant do contexto, ainda disponível: o Sender nunca lê o arquivo que o cliente mandou.
        if (await _store.FindAttachmentAsync(messageId, cancellationToken) is not { Direction: MessageDirection.Outbound, Status: AttachmentStatus.Available, StorageKey: { } chave } anexo)
        {
            return null;
        }

        return await ReadVerifiedAsync(contexto.TenantId, anexo, chave, cancellationToken);
    }

    public async Task<AttachmentChangeResult> CompleteAsync(CompleteAttachmentCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var contexto = _context.GetRequired();
        contexto.Require(Permission.SystemOperation);
        ArgumentNullException.ThrowIfNull(command.Content);

        var anexo = await _store.FindAttachmentAsync(command.MessageId, cancellationToken);
        if (anexo is null)
        {
            return new AttachmentChangeResult(AttachmentChangeOutcome.NotFound, null, null);
        }

        if (anexo.Status != AttachmentStatus.Pending)
        {
            return new AttachmentChangeResult(AttachmentChangeOutcome.AlreadyResolved, anexo.Status, anexo.FailureCode);
        }

        // A política antes de guardar qualquer coisa: o conteúdo que não passa nunca chega ao armazenamento.
        var veredito = AttachmentPolicy.Evaluate(_options, anexo.Kind, command.Content, command.ProviderMimeType, anexo.DeclaredMimeType, command.ProviderSha256, anexo.FileName);
        if (!veredito.Accepted)
        {
            return await RejectAsync(command.MessageId, veredito.FailureCode!, cancellationToken);
        }

        // A varredura (ADR 0032): infectado é recusado; o verificador que não respondeu NUNCA vale como limpo (com a varredura exigida o arquivo não é guardado e o chamador tenta de novo; sem ela, passa como não verificado).
        var varredura = await _scanner.ScanAsync(anexo.Kind, veredito.MimeType!, command.Content, cancellationToken);
        if (varredura == ScanVerdict.Infected)
        {
            return await RejectAsync(command.MessageId, "malware", cancellationToken);
        }

        if (_options.Scanner.Required && varredura != ScanVerdict.Clean)
        {
            return new AttachmentChangeResult(AttachmentChangeOutcome.ScanUnavailable, AttachmentStatus.Pending, null);
        }

        // O objeto tem chave por arquivo: se este processo cair aqui, quem repetir regrava o mesmo objeto e conclui.
        var chave = await _content.WriteAsync(contexto.TenantId, anexo.Id, command.Content, cancellationToken);

        await using (var trabalho = await _store.BeginAsync(cancellationToken))
        {
            if (await trabalho.LockMessageAsync(command.MessageId, cancellationToken) is null || await trabalho.FindAttachmentForUpdateAsync(command.MessageId, cancellationToken) is not { } travado)
            {
                return new AttachmentChangeResult(AttachmentChangeOutcome.NotFound, null, null);
            }

            // A cota da EMPRESA (ADR 0032), sob a trava dela: o que passa do limite não fica guardado e o arquivo vira indisponível com o motivo, sem afetar nenhum outro.
            var cabe = _options.TenantQuotaBytes <= 0 || await trabalho.LockAttachmentQuotaAsync(cancellationToken) + command.Content.Length <= _options.TenantQuotaBytes;
            if (cabe)
            {
                if (!travado.MarkAvailable(
                        veredito.MimeType!, veredito.FileName!, command.Content.Length, veredito.Sha256!, chave, _clock.GetUtcNow(), varredura == ScanVerdict.Clean ? AttachmentScanStatus.Clean : AttachmentScanStatus.NotScanned))
                {
                    return new AttachmentChangeResult(AttachmentChangeOutcome.AlreadyResolved, travado.Status, travado.FailureCode);
                }

                await trabalho.CommitAsync(cancellationToken);
                return new AttachmentChangeResult(AttachmentChangeOutcome.Changed, AttachmentStatus.Available, null);
            }
        }

        // Sem cota: a transação foi desfeita (a trava da empresa solta) e o objeto recém-gravado não fica no armazenamento.
        await _content.DeleteAsync(contexto.TenantId, chave, cancellationToken);
        return await MarkFailedAsync(command.MessageId, AttachmentStatus.Unavailable, QuotaExceededCode, cancellationToken);
    }

    /// <summary>O motivo gravado quando o arquivo recebido não coube na cota de armazenamento da empresa.</summary>
    public const string QuotaExceededCode = "quota-exceeded";

    public async Task<AttachmentChangeResult> FailAsync(FailAttachmentCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        _context.GetRequired().Require(Permission.SystemOperation);
        if (command.Status is not (AttachmentStatus.Unavailable or AttachmentStatus.Rejected))
        {
            throw new ConversationsValidationException("Um arquivo que falha fica indisponível ou recusado.");
        }

        return await MarkFailedAsync(command.MessageId, command.Status, command.Code, cancellationToken);
    }

    public async Task<AttachmentContent?> OpenAsync(Guid sessionId, Guid messageId, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();

        var linha = await _store.FindSessionAsync(sessionId, cancellationToken);
        if (linha is null || !contexto.IsAllowed(Permission.ConversationRead, linha.Session.OrganizationId))
        {
            return null;
        }

        // A mensagem precisa ser DESTA sessão: o identificador de uma mensagem de outra sessão (ou de outro tenant) é inexistente.
        if (await _store.FindMessageAsync(sessionId, messageId, cancellationToken) is null
            || await _store.FindAttachmentAsync(messageId, cancellationToken) is not { Status: AttachmentStatus.Available, StorageKey: { } chave, MimeType: { } tipo, FileName: { } nome } anexo)
        {
            return null;
        }

        var fluxo = await ReadVerifiedAsync(contexto.TenantId, anexo, chave, cancellationToken);
        return fluxo is null ? null : new AttachmentContent(anexo.ToView(), fluxo, tipo, nome);
    }

    /// <summary>
    /// O conteúdo do arquivo, SÓ se for confiável (ADR 0032): com a varredura exigida o arquivo precisa ter sido verificado como limpo; o objeto precisa existir, decifrar (chave certa, sem adulteração) e ter o resumo
    /// SHA-256 gravado na linha do arquivo. Qualquer falha devolve nulo: nunca entrega conteúdo não verificado, cifrado, adulterado ou diferente do que foi guardado.
    /// </summary>
    private async Task<Stream?> ReadVerifiedAsync(TenantId tenant, MessageAttachment anexo, string chave, CancellationToken cancellationToken)
    {
        if (_options.Scanner.Required && anexo.ScanStatus != AttachmentScanStatus.Clean)
        {
            return null;
        }

        Stream? fluxo;
        try
        {
            fluxo = await _content.OpenReadAsync(tenant, chave, cancellationToken);
        }
        catch (AttachmentContentUnreadableException)
        {
            return null;
        }

        if (fluxo is null)
        {
            return null;
        }

        await using (fluxo)
        {
            using var memoria = new MemoryStream();
            await fluxo.CopyToAsync(memoria, cancellationToken);
            var bytes = memoria.ToArray();
            if (bytes.Length != anexo.SizeBytes || !string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), anexo.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return new MemoryStream(bytes, writable: false);
        }
    }

    private Task<AttachmentChangeResult> RejectAsync(Guid messageId, string code, CancellationToken cancellationToken) =>
        MarkFailedAsync(messageId, AttachmentStatus.Rejected, code, cancellationToken, rejected: true);

    private async Task<AttachmentChangeResult> MarkFailedAsync(Guid messageId, AttachmentStatus status, string code, CancellationToken cancellationToken, bool rejected = false)
    {
        await using var trabalho = await _store.BeginAsync(cancellationToken);
        if (await trabalho.LockMessageAsync(messageId, cancellationToken) is null || await trabalho.FindAttachmentForUpdateAsync(messageId, cancellationToken) is not { } anexo)
        {
            return new AttachmentChangeResult(AttachmentChangeOutcome.NotFound, null, null);
        }

        if (!anexo.MarkFailed(status, code, _clock.GetUtcNow()))
        {
            return new AttachmentChangeResult(AttachmentChangeOutcome.AlreadyResolved, anexo.Status, anexo.FailureCode);
        }

        await trabalho.CommitAsync(cancellationToken);
        return new AttachmentChangeResult(rejected ? AttachmentChangeOutcome.Rejected : AttachmentChangeOutcome.Changed, status, code);
    }
}
