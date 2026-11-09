using IaBimeta.Conversations.Contracts;
using IaBimeta.SharedKernel;

namespace IaBimeta.ContactCenter.Application;

/// <summary>
/// O que o atendimento precisa saber do CANAL para responder (ADR 0027): por quanto tempo o provedor aceita resposta livre depois da última mensagem do cliente e quais modelos de mensagem aprovados o número pode
/// enviar fora dela, além de validar os parâmetros de um modelo. É uma porta DO ContactCenter, implementada pelo host com a consulta do Channels: a matriz de dependências não liga o ContactCenter ao Channels e
/// nenhuma aresta nova nasce (mesmo caminho do ADR 0008). Código confiável: o tenant é o do contexto e a permissão do atendente já foi decidida pelo ContactCenter.
/// </summary>
public interface IChannelReplyPolicy
{
    /// <summary>A janela e os modelos do número, ou <c>null</c> se o canal não os conhece (instância inexistente, de outro tenant ou sem a consulta): então o provedor decide.</summary>
    Task<ReplyOptions?> GetOptionsAsync(ChannelInstanceId channelInstanceId, CancellationToken cancellationToken);

    /// <summary>Valida os parâmetros e monta o texto que o cliente lerá e o bloco de canal que acompanha a mensagem. Nada é gravado nem enviado.</summary>
    Task<ReplyTemplatePreparation> PrepareTemplateAsync(
        ChannelInstanceId channelInstanceId, Guid templateId, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken);
}

/// <param name="Window">Quanto tempo depois da última mensagem do cliente o provedor aceita resposta livre (24 horas no WhatsApp). Nulo: não há janela.</param>
public sealed record ReplyOptions(TimeSpan? Window, IReadOnlyList<ReplyTemplateOption> Templates);

/// <summary>Um modelo aprovado que o atendente pode enviar. <c>BodyText</c> traz os marcadores como a Meta o aprovou.</summary>
public sealed record ReplyTemplateOption(
    Guid Id,
    string Name,
    string Language,
    string Category,
    string? HeaderText,
    string BodyText,
    string? FooterText,
    IReadOnlyList<ReplyTemplateParameterOption> Parameters);

/// <summary>Um parâmetro do modelo, para o formulário: a chave que volta no envio, o rótulo, o exemplo da aprovação e o tamanho máximo.</summary>
public sealed record ReplyTemplateParameterOption(string Key, string Component, string Label, string? Example, int MaxLength);

/// <param name="ChannelData">O bloco que o Conversations grava com a mensagem (o modelo e os parâmetros); nulo se não está pronto.</param>
/// <param name="PreviewText">O texto que o cliente lerá, já com os parâmetros: é o texto da mensagem no histórico.</param>
/// <param name="FailureCode"><c>template-not-found</c> (inexistente ou de outra conta) ou <c>template-not-sendable</c> (não aprovado); nulo se o modelo é enviável.</param>
public sealed record ReplyTemplatePreparation(ChannelData? ChannelData, string? PreviewText, string? FailureCode, IReadOnlyList<ReplyParameterError> Errors)
{
    public bool IsReady => FailureCode is null && Errors.Count == 0 && ChannelData is not null && PreviewText is not null;
}

/// <param name="Key">A chave do parâmetro com o problema (<c>*</c> quando é do conjunto).</param>
/// <param name="Code"><c>required</c>, <c>unknown</c>, <c>too-long</c>, <c>invalid-characters</c> ou <c>rendered-too-long</c>.</param>
public sealed record ReplyParameterError(string Key, string Code, string Message);

/// <summary>
/// O que o atendente vê ao abrir a resposta: se a janela de resposta livre está aberta, até quando, e os modelos aprovados do número. Sem janela no provedor (<see cref="WindowHours"/> nulo), o texto livre vale
/// sempre; com a janela fechada, só modelo.
/// </summary>
public sealed record ReplyOptionsView(
    bool FreeTextAllowed,
    bool WindowOpen,
    double? WindowHours,
    DateTimeOffset? LastInboundAt,
    DateTimeOffset? WindowClosesAt,
    IReadOnlyList<ReplyTemplateOption> Templates);

/// <summary>A implementação de quem não tem o Channels (testes e hosts sem o canal): nenhuma janela conhecida e nenhum modelo. O provedor continua sendo a defesa final contra texto livre fora da janela.</summary>
public sealed class NoChannelReplyPolicy : IChannelReplyPolicy
{
    public Task<ReplyOptions?> GetOptionsAsync(ChannelInstanceId channelInstanceId, CancellationToken cancellationToken) => Task.FromResult<ReplyOptions?>(null);

    public Task<ReplyTemplatePreparation> PrepareTemplateAsync(
        ChannelInstanceId channelInstanceId, Guid templateId, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken) =>
        Task.FromResult(new ReplyTemplatePreparation(null, null, "template-not-found", Array.Empty<ReplyParameterError>()));
}
