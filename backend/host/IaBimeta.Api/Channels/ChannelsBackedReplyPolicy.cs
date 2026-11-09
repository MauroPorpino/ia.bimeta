using IaBimeta.ContactCenter.Application;
using IaBimeta.Channels.Contracts.Queries;
using IaBimeta.Conversations.Contracts;
using IaBimeta.SharedKernel;

namespace IaBimeta.Api.Channels;

/// <summary>
/// A integração mediada pelo host entre o ContactCenter e o Channels para responder com um MODELO de mensagem (ADR 0027). O ContactCenter pergunta, por uma porta dele, qual é a janela de resposta livre do número
/// e quais modelos aprovados ele pode enviar, e pede a validação dos parâmetros; quem responde é o Channels, fonte de verdade dos modelos e da regra do provedor. Nenhuma aresta nova na matriz de
/// dependências (mesmo caminho do ADR 0008): só o host conhece os dois módulos. Cada pergunta consulta o Channels na hora (nenhuma cópia) e só enxerga o tenant do contexto. Uma falha da consulta sobe como exceção:
/// nunca vira "janela aberta" nem "modelo válido".
/// </summary>
internal sealed class ChannelsBackedReplyPolicy : IChannelReplyPolicy
{
    private readonly IChannelTemplates _templates;

    public ChannelsBackedReplyPolicy(IChannelTemplates templates)
    {
        _templates = templates;
    }

    public async Task<ReplyOptions?> GetOptionsAsync(ChannelInstanceId channelInstanceId, CancellationToken cancellationToken)
    {
        var opcoes = await _templates.GetReplyOptionsAsync(channelInstanceId, cancellationToken);
        if (opcoes is null)
        {
            return null;
        }

        return new ReplyOptions(
            opcoes.ReplyWindow,
            opcoes.Templates
                .Select(t => new ReplyTemplateOption(
                    t.Id, t.Name, t.Language, t.Category.ToString(), t.HeaderText, t.BodyText, t.FooterText,
                    t.Parameters.Select(p => new ReplyTemplateParameterOption(p.Key, p.Component, p.Label, p.Example, p.MaxLength)).ToList()))
                .ToList());
    }

    public async Task<ReplyTemplatePreparation> PrepareTemplateAsync(
        ChannelInstanceId channelInstanceId, Guid templateId, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        var preparo = await _templates.PrepareAsync(channelInstanceId, templateId, parameters, cancellationToken);
        return new ReplyTemplatePreparation(
            preparo.ChannelData is null ? null : new ChannelData(preparo.ChannelDataVersion, preparo.ChannelData),
            preparo.PreviewText,
            preparo.FailureCode,
            preparo.Errors.Select(e => new ReplyParameterError(e.Key, e.Code, e.Message)).ToList());
    }
}
