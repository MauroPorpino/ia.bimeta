using IaBimeta.SharedKernel;

namespace IaBimeta.Workflows.Core.Domain;

public enum VersionStatus
{
    Draft = 1,
    Published = 2,
    Retired = 3,
}

/// <summary>
/// Uma versão de um fluxo (ADR 0037 §6): rascunho, publicada ou aposentada. Uma versão publicada NUNCA muda (a mudança vira uma versão nova; um gatilho do banco também a protege) e uma conversa em
/// andamento termina na versão em que começou. O fluxo pertence a UMA organização (o escopo das permissões e das operações de integração).
/// </summary>
public sealed class WorkflowVersion : ITenantOwned
{
    private WorkflowVersion()
    {
        Key = string.Empty;
        Name = string.Empty;
        GraphJson = "{}";
        GraphHash = string.Empty;
    }

    public TenantId TenantId { get; private set; } = null!;

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public string Key { get; private set; }

    public int Version { get; private set; }

    public VersionStatus Status { get; private set; }

    public string Name { get; private set; }

    public string? Description { get; private set; }

    public string GraphJson { get; private set; }

    /// <summary>SHA-256 do significado do grafo (sem posições nem rótulos): identifica a versão publicada.</summary>
    public string GraphHash { get; private set; }

    public Guid? CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? PublishedAt { get; private set; }

    public Guid? PublishedBy { get; private set; }

    public DateTimeOffset? RetiredAt { get; private set; }

    public int Revision { get; private set; } = 1;

    public static WorkflowVersion CreateDraft(TenantId tenantId, Guid organizationId, string key, int version, string name, string? description, string graphJson, string graphHash, Guid? createdBy, DateTimeOffset now) =>
        new()
        {
            TenantId = tenantId,
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            Key = key,
            Version = version,
            Status = VersionStatus.Draft,
            Name = name,
            Description = description,
            GraphJson = graphJson,
            GraphHash = graphHash,
            CreatedBy = createdBy,
            CreatedAt = now,
            UpdatedAt = now,
        };

    /// <summary>Edita o RASCUNHO. Uma versão publicada nunca é alterada.</summary>
    public void UpdateDraft(string name, string? description, string graphJson, string graphHash, DateTimeOffset now)
    {
        if (Status != VersionStatus.Draft)
        {
            throw new WorkflowsConflictException("not-draft", "Só o rascunho pode ser editado: uma versão publicada nunca muda.");
        }

        Name = name;
        Description = description;
        GraphJson = graphJson;
        GraphHash = graphHash;
        UpdatedAt = now;
        Revision++;
    }

    public void Publish(Guid? by, DateTimeOffset now)
    {
        if (Status != VersionStatus.Draft)
        {
            throw new WorkflowsConflictException("not-draft", "Só o rascunho pode ser publicado.");
        }

        Status = VersionStatus.Published;
        PublishedAt = now;
        PublishedBy = by;
        UpdatedAt = now;
        Revision++;
    }

    public void Retire(DateTimeOffset now)
    {
        if (Status == VersionStatus.Retired)
        {
            return;
        }

        if (Status != VersionStatus.Published)
        {
            throw new WorkflowsConflictException("not-published", "Só uma versão publicada é aposentada; um rascunho se descarta editando.");
        }

        Status = VersionStatus.Retired;
        RetiredAt = now;
        UpdatedAt = now;
        Revision++;
    }
}
