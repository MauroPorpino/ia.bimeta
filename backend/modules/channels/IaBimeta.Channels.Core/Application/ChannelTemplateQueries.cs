using IaBimeta.Channels.Contracts;
using IaBimeta.Channels.Contracts.Queries;
using IaBimeta.SharedKernel;

namespace IaBimeta.Channels.Application;

/// <summary>
/// A consulta entre módulos dos modelos de mensagem (<see cref="IChannelTemplates"/>). Só enxerga o tenant do contexto (instância ou modelo de outro tenant responde como inexistente) e não aplica permissão de usuário:
/// é para o código confiável do host, que a oferece ao ContactCenter por uma porta própria. Falha fechada: sem contexto lança. Só modelos APROVADOS e DA CONTA DO NÚMERO chegam ao atendente.
/// </summary>
public sealed class ChannelTemplateQueries : IChannelTemplates
{
    public const string TemplateNotFound = "template-not-found";
    public const string TemplateNotSendable = "template-not-sendable";

    private readonly IChannelRepository _repository;
    private readonly IRequestContextAccessor _context;
    private readonly ChannelProviderAdapters _adapters;

    public ChannelTemplateQueries(IChannelRepository repository, IRequestContextAccessor context, ChannelProviderAdapters adapters)
    {
        _repository = repository;
        _context = context;
        _adapters = adapters;
    }

    public async Task<ChannelReplyOptions?> GetReplyOptionsAsync(ChannelInstanceId channelInstanceId, CancellationToken cancellationToken = default)
    {
        _ = _context.GetRequired();
        if (channelInstanceId.IsEmpty)
        {
            return null;
        }

        var instancia = await _repository.FindInstanceReadOnlyAsync(channelInstanceId, cancellationToken);
        if (instancia is null)
        {
            return null;
        }

        var janela = _adapters.Find(instancia.Provider)?.ReplyWindow;
        var modelos = await _repository.ListTemplatesAsync(instancia.AccountId, onlyApproved: true, cancellationToken);
        return new ChannelReplyOptions(
            instancia.Id,
            janela,
            modelos.Select(m => new ReplyTemplateView(
                m.Id, m.Name, m.Language, m.Category, m.HeaderText, m.BodyText, m.FooterText,
                m.Parameters.Select(p => new ReplyTemplateParameter(p.Key, p.Component, p.Label, p.Example, p.MaxLength)).ToList())).ToList());
    }

    public async Task<TemplatePreparation> PrepareAsync(ChannelInstanceId channelInstanceId, Guid templateId, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        _ = _context.GetRequired();
        ArgumentNullException.ThrowIfNull(parameters);

        var instancia = channelInstanceId.IsEmpty ? null : await _repository.FindInstanceReadOnlyAsync(channelInstanceId, cancellationToken);
        var modelo = instancia is null || templateId == Guid.Empty ? null : await _repository.FindTemplateReadOnlyAsync(templateId, cancellationToken);

        // O modelo de outra conta (mesmo do mesmo tenant) responde como inexistente: cada número envia só os modelos da sua conta.
        if (instancia is null || modelo is null || modelo.AccountId != instancia.AccountId)
        {
            return new TemplatePreparation(0, null, null, TemplateNotFound, Array.Empty<TemplateParameterError>());
        }

        if (!modelo.IsSendable)
        {
            return new TemplatePreparation(0, null, null, TemplateNotSendable, Array.Empty<TemplateParameterError>());
        }

        return TemplateRenderer.Prepare(modelo, parameters);
    }
}
