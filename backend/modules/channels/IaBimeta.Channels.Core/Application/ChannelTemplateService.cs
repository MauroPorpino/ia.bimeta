using System.Text.Json;
using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.Channels.Contracts;
using IaBimeta.Channels.Contracts.Queries;
using IaBimeta.Channels.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Channels.Application;

/// <summary>Um modelo de mensagem associado a uma conta, como a administração o enxerga.</summary>
public sealed record ChannelTemplateView(
    Guid Id,
    Guid AccountId,
    string Name,
    string Language,
    TemplateCategory Category,
    TemplateStatus Status,
    string? MetaTemplateId,
    string? HeaderText,
    string BodyText,
    string? FooterText,
    IReadOnlyList<ReplyTemplateParameter> Parameters,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    internal static ChannelTemplateView From(ChannelTemplate t) =>
        new(t.Id, t.AccountId, t.Name, t.Language, t.Category, t.Status, t.MetaTemplateId, t.HeaderText, t.BodyText, t.FooterText,
            t.Parameters.Select(p => new ReplyTemplateParameter(p.Key, p.Component, p.Label, p.Example, p.MaxLength)).ToList(), t.CreatedAt, t.UpdatedAt);
}

/// <summary>O que o cadastro fez com cada modelo recebido.</summary>
/// <param name="Rejected">Os modelos que o MVP não sabe enviar (cabeçalho de mídia, autenticação...), com o motivo.</param>
public sealed record ChannelTemplateRegistration(
    IReadOnlyList<ChannelTemplateView> Created,
    IReadOnlyList<ChannelTemplateView> Updated,
    IReadOnlyList<ChannelTemplateView> Unchanged,
    IReadOnlyList<string> Rejected);

/// <summary>
/// Cadastro dos modelos de mensagem (templates) JÁ APROVADOS na Meta e associados à conta WhatsApp correta (ADR 0027). Não cria, edita nem aprova modelo: recebe a definição como a Meta a devolve (um objeto, uma lista ou
/// a página <c>{"data":[...]}</c> da Graph API), extrai nome, idioma, categoria, situação e a estrutura de parâmetros e grava por conta. Reenviar o mesmo modelo atualiza o que a Meta mudou. Mesmas regras do resto do
/// Channels: tenant e permissões do contexto confiável, a conta de outro tenant ou de organização sem leitura responde como inexistente, gerir exige <see cref="Permission.ChannelManage"/> e tudo entra na trilha de auditoria.
/// </summary>
public sealed class ChannelTemplateService
{
    private const string TemplateResource = "channel-template";

    /// <summary>O teto de modelos por chamada: uma página da Graph API tem até 100; o dobro cobre uma conta com muitos idiomas.</summary>
    public const int MaxTemplatesPerRegistration = 250;

    private readonly IChannelRepository _repository;
    private readonly IRequestContextAccessor _context;
    private readonly TimeProvider _clock;
    private readonly IAuditWriter _audit;

    public ChannelTemplateService(IChannelRepository repository, IRequestContextAccessor context, TimeProvider clock, IAuditWriter audit)
    {
        _repository = repository;
        _context = context;
        _clock = clock;
        _audit = audit;
    }

    /// <exception cref="ChannelsResourceNotFoundException">A conta não existe ou não é visível ao usuário.</exception>
    /// <exception cref="AccessDeniedException">Falta a permissão de gerir canais da organização.</exception>
    /// <exception cref="ChannelsValidationException">Nenhum modelo da definição pôde ser aceito (a mensagem traz o motivo).</exception>
    public async Task<ChannelTemplateRegistration> RegisterAsync(Guid accountId, JsonElement definition, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        var conta = await _repository.FindAccountAsync(accountId, cancellationToken);
        if (conta is null || !contexto.IsAllowed(Permission.ChannelRead, conta.OrganizationId))
        {
            throw new ChannelsResourceNotFoundException("Conta");
        }

        await AuditedAccess.RequireAsync(
            _audit, contexto, Permission.ChannelManage, conta.OrganizationId,
            "channels.template.register", TemplateResource, conta.Id.ToString(), cancellationToken);

        var itens = Items(definition);
        var criados = new List<ChannelTemplateView>();
        var atualizados = new List<ChannelTemplateView>();
        var iguais = new List<ChannelTemplateView>();
        var recusados = new List<string>();
        var agora = _clock.GetUtcNow();

        foreach (var item in itens)
        {
            ParsedTemplate lido;
            try
            {
                lido = TemplateDefinitionParser.Parse(item);
            }
            catch (ChannelsValidationException ex)
            {
                recusados.Add(ex.Message);
                continue;
            }

            var existente = await _repository.FindTemplateAsync(conta.Id, lido.Name, lido.Language, cancellationToken);
            if (existente is null)
            {
                var novo = ChannelTemplate.Create(
                    contexto.TenantId, conta.Id, lido.Name, lido.Language, lido.Category, lido.Status, lido.MetaTemplateId, lido.HeaderText, lido.BodyText, lido.FooterText, lido.Parameters, agora);
                await _repository.AddTemplateAsync(novo, cancellationToken);
                criados.Add(ChannelTemplateView.From(novo));
                continue;
            }

            if (Same(existente, lido))
            {
                iguais.Add(ChannelTemplateView.From(existente));
                continue;
            }

            existente.Refresh(lido.Category, lido.Status, lido.MetaTemplateId, lido.HeaderText, lido.BodyText, lido.FooterText, lido.Parameters, agora);
            await _repository.SaveChangesAsync(cancellationToken);
            atualizados.Add(ChannelTemplateView.From(existente));
        }

        if (criados.Count + atualizados.Count + iguais.Count == 0)
        {
            await _audit.WriteAsync(new AuditEntry("channels.template.register", TemplateResource, conta.Id.ToString(), AuditOutcome.Failed), cancellationToken);
            throw new ChannelsValidationException(recusados.Count == 1 ? recusados[0] : $"Nenhum modelo pôde ser aceito. {string.Join(" ", recusados.Take(3))}");
        }

        await _audit.WriteAsync(new AuditEntry("channels.template.register", TemplateResource, conta.Id.ToString(), AuditOutcome.Success), cancellationToken);
        return new ChannelTemplateRegistration(criados, atualizados, iguais, recusados);
    }

    /// <summary>Muda a situação de um modelo (por exemplo, a Meta o pausou). Um modelo que não está aprovado deixa de ser oferecido ao atendente e de ser enviado.</summary>
    /// <exception cref="ChannelsResourceNotFoundException">O modelo não existe ou não é visível ao usuário.</exception>
    /// <exception cref="AccessDeniedException">Falta a permissão de gerir canais da organização.</exception>
    public async Task<ChannelTemplateView> SetStatusAsync(Guid accountId, Guid templateId, TemplateStatus status, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        var modelo = await _repository.FindTemplateAsync(templateId, cancellationToken);
        var conta = modelo is null || modelo.AccountId != accountId ? null : await _repository.FindAccountAsync(modelo.AccountId, cancellationToken);
        if (modelo is null || conta is null || !contexto.IsAllowed(Permission.ChannelRead, conta.OrganizationId))
        {
            throw new ChannelsResourceNotFoundException("Modelo");
        }

        await AuditedAccess.RequireAsync(
            _audit, contexto, Permission.ChannelManage, conta.OrganizationId,
            "channels.template.status", TemplateResource, modelo.Id.ToString(), cancellationToken);

        if (modelo.SetStatus(status, _clock.GetUtcNow()))
        {
            await _repository.SaveChangesAsync(cancellationToken);
        }

        await _audit.WriteAsync(new AuditEntry("channels.template.status", TemplateResource, modelo.Id.ToString(), AuditOutcome.Success), cancellationToken);
        return ChannelTemplateView.From(modelo);
    }

    /// <summary>Os modelos da conta, de qualquer situação. Conta de outro tenant ou sem permissão de leitura: <c>null</c> (TEN-2).</summary>
    public async Task<IReadOnlyList<ChannelTemplateView>?> ListAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var contexto = _context.GetRequired();
        var conta = await _repository.FindAccountAsync(accountId, cancellationToken);
        if (conta is null || !contexto.IsAllowed(Permission.ChannelRead, conta.OrganizationId))
        {
            return null;
        }

        var modelos = await _repository.ListTemplatesAsync(conta.Id, onlyApproved: false, cancellationToken);
        return modelos.Select(ChannelTemplateView.From).ToList();
    }

    /// <summary>Uma definição pode ser um objeto, uma lista ou a página da Graph API ({"data":[...]}).</summary>
    private static List<JsonElement> Items(JsonElement definicao)
    {
        List<JsonElement> itens;
        switch (definicao.ValueKind)
        {
            case JsonValueKind.Array:
                itens = definicao.EnumerateArray().ToList();
                break;

            case JsonValueKind.Object when definicao.TryGetProperty("data", out var dados) && dados.ValueKind == JsonValueKind.Array:
                itens = dados.EnumerateArray().ToList();
                break;

            case JsonValueKind.Object:
                itens = [definicao];
                break;

            default:
                throw new ChannelsValidationException("A definição deve ser um modelo, uma lista de modelos ou a página \"data\" da Graph API.");
        }

        if (itens.Count == 0)
        {
            throw new ChannelsValidationException("A definição não traz modelo algum.");
        }

        if (itens.Count > MaxTemplatesPerRegistration)
        {
            throw new ChannelsValidationException($"Cadastre no máximo {MaxTemplatesPerRegistration} modelos por vez.");
        }

        return itens;
    }

    private static bool Same(ChannelTemplate atual, ParsedTemplate novo) =>
        atual.Category == novo.Category
        && atual.Status == novo.Status
        && string.Equals(atual.MetaTemplateId, novo.MetaTemplateId, StringComparison.Ordinal)
        && string.Equals(atual.HeaderText, novo.HeaderText, StringComparison.Ordinal)
        && string.Equals(atual.BodyText, novo.BodyText, StringComparison.Ordinal)
        && string.Equals(atual.FooterText, novo.FooterText, StringComparison.Ordinal)
        && atual.Parameters.SequenceEqual(novo.Parameters);
}
