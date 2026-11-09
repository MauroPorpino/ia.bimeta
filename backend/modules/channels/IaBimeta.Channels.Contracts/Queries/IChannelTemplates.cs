using IaBimeta.SharedKernel;

namespace IaBimeta.Channels.Contracts.Queries;

/// <summary>
/// O que o atendimento precisa do Channels para responder com um MODELO de mensagem aprovado (ADR 0027): a regra de janela do provedor e os modelos que a conta do número pode enviar, e a preparação de um envio
/// (validação dos parâmetros e o texto que o cliente vai ler). Consulta de módulo para módulo, a código confiável do host: o tenant é o do <see cref="RequestContext"/> (instância de outro tenant responde como
/// inexistente) e NÃO aplica permissões de usuário; quem decide se o atendente pode responder a esta conversa é o ContactCenter.
/// </summary>
public interface IChannelTemplates
{
    /// <summary>A janela de resposta livre do provedor e os modelos APROVADOS da conta do número, ou <c>null</c> se a instância não existe no tenant do contexto.</summary>
    /// <exception cref="TenantContextRequiredException">Não há contexto de tenant confiável.</exception>
    Task<ChannelReplyOptions?> GetReplyOptionsAsync(ChannelInstanceId channelInstanceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Valida os parâmetros contra a estrutura do modelo e monta o que será gravado na mensagem. Modelo inexistente, de outra conta ou que não está aprovado: <see cref="TemplatePreparation.FailureCode"/> preenchido.
    /// Parâmetro faltando, a mais, longo demais ou com caractere que a Meta recusa: <see cref="TemplatePreparation.Errors"/>. Nada é enviado nem gravado.
    /// </summary>
    /// <param name="parameters">Os valores por chave de parâmetro (<c>header.1</c>, <c>body.1</c>, <c>body.nome</c>, <c>button.0</c>).</param>
    Task<TemplatePreparation> PrepareAsync(ChannelInstanceId channelInstanceId, Guid templateId, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default);
}

/// <param name="ReplyWindow">Por quanto tempo depois da última mensagem do cliente o provedor aceita resposta LIVRE (no WhatsApp, 24 horas). Nulo: o provedor não tem janela (qualquer momento serve).</param>
/// <param name="Templates">Os modelos aprovados da conta, por nome e idioma.</param>
public sealed record ChannelReplyOptions(ChannelInstanceId ChannelInstanceId, TimeSpan? ReplyWindow, IReadOnlyList<ReplyTemplateView> Templates);

/// <param name="BodyText">O corpo como a Meta o aprovou, com os marcadores (<c>{{1}}</c> ou <c>{{nome}}</c>).</param>
public sealed record ReplyTemplateView(
    Guid Id,
    string Name,
    string Language,
    TemplateCategory Category,
    string? HeaderText,
    string BodyText,
    string? FooterText,
    IReadOnlyList<ReplyTemplateParameter> Parameters);

/// <param name="Key">A chave do parâmetro: componente, ponto e posição ou nome (<c>body.1</c>, <c>body.nome</c>, <c>header.1</c>, <c>button.0</c>).</param>
/// <param name="Component"><c>header</c>, <c>body</c> ou <c>button</c>.</param>
/// <param name="Label">Um rótulo legível para o formulário (o nome do parâmetro, ou "Variável 1").</param>
/// <param name="Example">O exemplo que o administrador cadastrou na Meta, se houver.</param>
/// <param name="MaxLength">O tamanho máximo aceito para o valor.</param>
public sealed record ReplyTemplateParameter(string Key, string Component, string Label, string? Example, int MaxLength);

/// <summary>O resultado da preparação de um envio de modelo.</summary>
/// <param name="ChannelData">Os pares que o Conversations guarda com a mensagem (a versão do esquema e o dicionário), prontos para o Sender reconstruir o envio. Nulo se não está pronto.</param>
/// <param name="PreviewText">O texto que o cliente lerá (cabeçalho, corpo e rodapé com os valores): é o que o histórico mostra.</param>
/// <param name="FailureCode"><c>template-not-found</c> ou <c>template-not-sendable</c> (não aprovado); nulo se o modelo é enviável.</param>
public sealed record TemplatePreparation(
    int ChannelDataVersion,
    IReadOnlyDictionary<string, string>? ChannelData,
    string? PreviewText,
    string? FailureCode,
    IReadOnlyList<TemplateParameterError> Errors)
{
    public bool IsReady => FailureCode is null && Errors.Count == 0 && ChannelData is not null;
}

/// <param name="Key">A chave do parâmetro com o problema (<c>*</c> quando é do conjunto).</param>
/// <param name="Code"><c>required</c>, <c>unknown</c>, <c>too-long</c>, <c>invalid-characters</c> ou <c>rendered-too-long</c>.</param>
public sealed record TemplateParameterError(string Key, string Code, string Message);
