using System.Text.RegularExpressions;
using IaBimeta.Audit.Contracts;
using IaBimeta.Capabilities.Contracts;
using IaBimeta.Capabilities.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.Capabilities.Application;

/// <summary>
/// O catálogo de operações do tenant, versionado (ADR 0035, §1): salvar o rascunho, publicar, aposentar, ligar e desligar, e listar. A operação é separada da conexão (só a referencia). Publicar aposenta a versão
/// anterior na MESMA transação; só a versão publicada executa e nunca muda. Cada passo confere a permissão sobre a organização da conexão e é auditado.
/// </summary>
public sealed partial class DefinitionService
{
    private readonly CapabilitiesAuthorizer _auth;
    private readonly IDefinitionStore _definitions;
    private readonly IConnectionStore _connections;
    private readonly CapabilitiesOptions _options;
    private readonly TimeProvider _clock;

    internal DefinitionService(CapabilitiesAuthorizer auth, IDefinitionStore definitions, IConnectionStore connections, CapabilitiesOptions options, TimeProvider clock)
    {
        _auth = auth;
        _definitions = definitions;
        _connections = connections;
        _options = options;
        _clock = clock;
    }

    [GeneratedRegex(@"^[a-z][a-z0-9_.-]{1,79}$")]
    internal static partial Regex KeyRegex();

    public async Task<IReadOnlyList<DefinitionView>> ListAsync(Guid? connectionId, CancellationToken cancellationToken = default)
    {
        var visiveis = _auth.Context.OrganizationsWithPermission(Permission.IntegrationRead);
        var conexoes = (await _connections.ListAsync(cancellationToken)).Where(c => visiveis is null || (c.OrganizationId is { } o && visiveis.Contains(o))).Select(c => c.Id).ToHashSet();
        var todas = await _definitions.ListLatestAsync(connectionId, cancellationToken);
        return todas.Where(d => conexoes.Contains(d.ConnectionId)).OrderBy(d => d.Key, StringComparer.Ordinal).Select(DefinitionView.From).ToList();
    }

    public async Task<DefinitionView?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var definicao = await _definitions.FindAsync(id, cancellationToken);
        return definicao is not null && await CanReadAsync(definicao, cancellationToken) ? DefinitionView.From(definicao) : null;
    }

    public async Task<IReadOnlyList<DefinitionView>> VersionsAsync(string key, CancellationToken cancellationToken = default)
    {
        var versoes = await _definitions.ListVersionsAsync(key, cancellationToken);
        var lidas = new List<DefinitionView>();
        foreach (var versao in versoes)
        {
            if (await CanReadAsync(versao, cancellationToken))
            {
                lidas.Add(DefinitionView.From(versao));
            }
        }

        return lidas;
    }

    /// <summary>Cria o rascunho (uma versão nova) ou atualiza o rascunho existente da chave. A conexão de uma chave não muda depois da primeira versão.</summary>
    public async Task<DefinitionView> SaveDraftAsync(DefinitionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var conexao = await _connections.FindAsync(request.ConnectionId, cancellationToken)
            ?? throw new CapabilitiesValidationException("connectionId: conexão não encontrada.");
        await _auth.RequireAsync(Permission.IntegrationManage, conexao.OrganizationId, "capabilities.definition.save", CapabilitiesAuthorizer.DefinitionResource, null, cancellationToken);

        var problemas = new List<string>();
        if (!KeyRegex().IsMatch(request.Key ?? string.Empty))
        {
            problemas.Add("key: minúsculas, números e . _ -, começando por letra (2 a 80).");
        }

        var nome = (request.Name ?? string.Empty).Trim();
        if (nome.Length is < 1 or > 100 || nome.Any(char.IsControl))
        {
            problemas.Add("name: de 1 a 100 caracteres.");
        }

        var descricao = request.Description?.Trim();
        if (descricao is { Length: > 500 } || (descricao?.Any(c => char.IsControl(c) && c is not ('\n' or '\t')) ?? false))
        {
            problemas.Add("description: até 500 caracteres.");
        }

        if (!Enum.IsDefined(request.Effect))
        {
            problemas.Add("effect: Read ou Write.");
        }

        var chamadores = CapabilityCaller.None;
        foreach (var origem in request.AllowedCallers ?? [])
        {
            if (!Enum.IsDefined(origem) || origem == CapabilityCaller.None || (int)origem is var v && (v & (v - 1)) != 0)
            {
                problemas.Add("allowedCallers: valor inválido.");
                continue;
            }

            chamadores |= origem;
        }

        if (chamadores == CapabilityCaller.None)
        {
            // Padrão seguro: só uma pessoa, pela administração. Uma operação só é usada pelo agente, chatbot ou fluxo se o administrador a liberar para eles.
            chamadores = CapabilityCaller.Operator;
        }

        var confirmacao = request.Effect == CapabilityEffect.Write ? request.RequiresConfirmation ?? true : false;
        if (request.Spec is null)
        {
            problemas.Add("spec: obrigatório.");
        }
        else
        {
            problemas.AddRange(OperationSpecRules.Validate(request.Spec, request.Effect, confirmacao, conexao.AllowedHeaders));
            if (CapabilitySpecJson.Serialize(request.Spec).Length > CapabilitySpecJson.MaxLength)
            {
                problemas.Add("spec: grande demais.");
            }
        }

        if (problemas.Count > 0)
        {
            throw new CapabilitiesValidationException(problemas);
        }

        var agora = _clock.GetUtcNow();
        var existente = await _definitions.FindDraftAsync(request.Key!, cancellationToken);
        CapabilityDefinition rascunho;
        if (existente is not null)
        {
            if (existente.ConnectionId != conexao.Id)
            {
                throw new CapabilitiesValidationException("connectionId: a conexão de uma operação não muda depois da primeira versão.");
            }

            if (request.ExpectedRevision is not { } esperada || esperada != existente.Revision)
            {
                throw new CapabilitiesConflictException("changed", "O rascunho foi alterado por outra operação. Releia e tente de novo.");
            }

            existente.UpdateDraft(nome, descricao, request.Effect, confirmacao, chamadores, request.Spec!, agora);
            rascunho = existente;
        }
        else
        {
            var versoes = await _definitions.ListVersionsAsync(request.Key!, cancellationToken);
            if (versoes.Count > 0 && versoes[0].ConnectionId != conexao.Id)
            {
                throw new CapabilitiesValidationException("connectionId: a conexão de uma operação não muda depois da primeira versão.");
            }

            if (versoes.Count == 0 && await _definitions.CountKeysAsync(cancellationToken) >= _options.MaxDefinitionKeysPerTenant)
            {
                throw new CapabilitiesConflictException("limit-reached", "O limite de operações desta empresa foi atingido.");
            }

            var proxima = (versoes.Count == 0 ? 0 : versoes.Max(v => v.Version)) + 1;
            rascunho = CapabilityDefinition.CreateDraft(_auth.Context.TenantId, conexao.Id, request.Key!, proxima, nome, descricao, request.Effect, confirmacao, chamadores, request.Spec!, _auth.Context.Principal?.UserId, agora);
            _definitions.Add(rascunho);
        }

        await _definitions.SaveAsync(cancellationToken);
        await _auth.WriteAsync("capabilities.definition.save", CapabilitiesAuthorizer.DefinitionResource, rascunho.Id, AuditOutcome.Success, cancellationToken);
        return DefinitionView.From(rascunho);
    }

    public async Task<DefinitionView?> PublishAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var rascunho = await _definitions.FindAsync(id, cancellationToken);
        if (rascunho is null)
        {
            return null;
        }

        var conexao = await _connections.FindAsync(rascunho.ConnectionId, cancellationToken);
        await _auth.RequireAsync(Permission.IntegrationManage, conexao?.OrganizationId, "capabilities.definition.publish", CapabilitiesAuthorizer.DefinitionResource, id, cancellationToken);
        if (conexao is null)
        {
            throw new CapabilitiesValidationException("connectionId: a conexão da operação não existe mais.");
        }

        // O molde é conferido DE NOVO na publicação: o que está guardado não é confiável por ter sido aceito antes (a conexão pode ter mudado os cabeçalhos permitidos).
        var problemas = OperationSpecRules.Validate(rascunho.Spec, rascunho.Effect, rascunho.RequiresConfirmation, conexao.AllowedHeaders);
        if (problemas.Count > 0)
        {
            throw new CapabilitiesValidationException(problemas);
        }

        var agora = _clock.GetUtcNow();
        var anterior = await _definitions.FindPublishedAsync(rascunho.Key, cancellationToken);

        // Uma transação só: a anterior sai de cena (primeiro, por causa do índice único parcial) e a nova entra. O índice garante uma publicada por chave mesmo sob disputa.
        await _definitions.InTransactionAsync(
            async () =>
            {
                if (anterior is not null)
                {
                    anterior.Retire(agora);
                    await _definitions.SaveAsync(cancellationToken);
                }

                rascunho.Publish(_auth.Context.Principal?.UserId, agora);
                await _definitions.SaveAsync(cancellationToken);
            },
            cancellationToken);
        await _auth.WriteAsync("capabilities.definition.publish", CapabilitiesAuthorizer.DefinitionResource, id, AuditOutcome.Success, cancellationToken);
        return DefinitionView.From(rascunho);
    }

    public async Task<DefinitionView?> RetireAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var definicao = await _definitions.FindAsync(id, cancellationToken);
        if (definicao is null)
        {
            return null;
        }

        await RequireManageAsync(definicao, "capabilities.definition.retire", cancellationToken);
        definicao.Retire(_clock.GetUtcNow());
        await _definitions.SaveAsync(cancellationToken);
        await _auth.WriteAsync("capabilities.definition.retire", CapabilitiesAuthorizer.DefinitionResource, id, AuditOutcome.Success, cancellationToken);
        return DefinitionView.From(definicao);
    }

    /// <summary>Liga ou desliga a versão publicada (vale já na próxima execução). Não cria versão: é um interruptor operacional, auditado.</summary>
    public async Task<DefinitionView?> SetEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken = default)
    {
        var definicao = await _definitions.FindAsync(id, cancellationToken);
        if (definicao is null)
        {
            return null;
        }

        var operacao = enabled ? "capabilities.definition.enable" : "capabilities.definition.disable";
        await RequireManageAsync(definicao, operacao, cancellationToken);
        if (definicao.Status != DefinitionStatus.Published)
        {
            throw new CapabilitiesConflictException("changed", "Só a versão publicada é ligada ou desligada.");
        }

        definicao.SetEnabled(enabled, _clock.GetUtcNow());
        await _definitions.SaveAsync(cancellationToken);
        await _auth.WriteAsync(operacao, CapabilitiesAuthorizer.DefinitionResource, id, AuditOutcome.Success, cancellationToken);
        return DefinitionView.From(definicao);
    }

    private async Task RequireManageAsync(CapabilityDefinition definicao, string operacao, CancellationToken cancellationToken)
    {
        var conexao = await _connections.FindAsync(definicao.ConnectionId, cancellationToken);
        await _auth.RequireAsync(Permission.IntegrationManage, conexao?.OrganizationId, operacao, CapabilitiesAuthorizer.DefinitionResource, definicao.Id, cancellationToken);
    }

    private async Task<bool> CanReadAsync(CapabilityDefinition definicao, CancellationToken cancellationToken)
    {
        var conexao = await _connections.FindAsync(definicao.ConnectionId, cancellationToken);
        return conexao is not null && _auth.Context.IsAllowed(Permission.IntegrationRead, conexao.OrganizationId);
    }
}
