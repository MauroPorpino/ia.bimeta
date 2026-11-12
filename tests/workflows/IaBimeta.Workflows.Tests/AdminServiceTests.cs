using IaBimeta.Audit.Contracts;
using IaBimeta.Audit.Contracts.Commands;
using IaBimeta.Capabilities.Contracts;
using IaBimeta.SharedKernel;
using IaBimeta.Workflows.Core.Application;
using IaBimeta.Workflows.Core.Domain;
using static IaBimeta.Workflows.Tests.Samples;

namespace IaBimeta.Workflows.Tests;

internal static class Ctx
{
    public static readonly TenantId TenantA = new(Guid.Parse("a0000000-0000-0000-0000-00000000000a"));
    public static readonly TenantId TenantB = new(Guid.Parse("b0000000-0000-0000-0000-00000000000b"));
    public static readonly Guid OrgA = Guid.Parse("0a000000-0000-0000-0000-00000000000a");
    public static readonly Guid OrgB = Guid.Parse("0b000000-0000-0000-0000-00000000000b");

    public static RequestContextHolder User(TenantId tenant, params PermissionGrant[] grants)
    {
        var holder = new RequestContextHolder();
        holder.Set(new RequestContext(tenant, "teste", new ActorPrincipal(Guid.NewGuid(), Guid.NewGuid(), grants)));
        return holder;
    }

    public static RequestContextHolder Admin(TenantId tenant, Guid org) =>
        User(tenant, new[] { Permission.WorkflowRead, Permission.WorkflowManage, Permission.WorkflowPublish, Permission.WorkflowTest, Permission.IntegrationExecute }.Select(p => new PermissionGrant(p, org)).ToArray());

    public static RequestContextHolder System(TenantId tenant)
    {
        var holder = new RequestContextHolder();
        holder.Set(new RequestContext(tenant, "teste"));
        return holder;
    }
}

internal sealed class FakeAudit : IAuditWriter
{
    public List<AuditEntry> Entries { get; } = [];

    public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        lock (Entries)
        {
            Entries.Add(entry);
        }

        return Task.CompletedTask;
    }

    public Task WriteForAuthenticationAsync(TenantId tenantId, Guid? actorUserId, AuditEntry entry, CancellationToken cancellationToken = default) => WriteAsync(entry, cancellationToken);
}

internal sealed class FakeClock : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
}

/// <summary>Um armazém em memória que respeita o tenant e os índices únicos, como o banco.</summary>
internal sealed class InMemoryStore : IWorkflowStore
{
    private readonly IRequestContextAccessor _context;

    public InMemoryStore(IRequestContextAccessor context, List<WorkflowVersion>? rows = null)
    {
        _context = context;
        Rows = rows ?? [];
    }

    public List<WorkflowVersion> Rows { get; }

    private IEnumerable<WorkflowVersion> Mine => Rows.Where(v => v.TenantId == _context.GetRequired().TenantId);

    public Task<WorkflowVersion?> FindAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Mine.FirstOrDefault(v => v.Id == id));

    public Task<WorkflowVersion?> FindPublishedAsync(string key, CancellationToken cancellationToken) => Task.FromResult(Mine.FirstOrDefault(v => v.Key == key && v.Status == VersionStatus.Published));

    public Task<WorkflowVersion?> FindDraftAsync(string key, CancellationToken cancellationToken) => Task.FromResult(Mine.FirstOrDefault(v => v.Key == key && v.Status == VersionStatus.Draft));

    public Task<IReadOnlyList<WorkflowVersion>> ListVersionsAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<WorkflowVersion>>(Mine.Where(v => v.Key == key).OrderByDescending(v => v.Version).ToList());

    public Task<IReadOnlyList<WorkflowVersion>> ListLatestAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<WorkflowVersion>>(Mine.GroupBy(v => v.Key).Select(g => g.OrderByDescending(v => v.Version).First()).ToList());

    public Task<int> CountKeysAsync(CancellationToken cancellationToken) => Task.FromResult(Mine.Select(v => v.Key).Distinct().Count());

    public void Add(WorkflowVersion version) => Rows.Add(version);

    public Task SaveAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task InTransactionAsync(Func<Task> work, CancellationToken cancellationToken) => await work();
}

internal sealed class FakeCatalog : ICapabilityCatalog
{
    public List<CapabilityDescriptor> Items { get; } = [];

    public bool Denied { get; set; }

    public Guid? LastScope { get; private set; }

    public Task<IReadOnlyList<CapabilityDescriptor>> ListAsync(CapabilityCaller caller, CancellationToken cancellationToken = default, Guid? scopeOrganizationId = null)
    {
        LastScope = scopeOrganizationId;
        if (Denied)
        {
            throw new AccessDeniedException(Permission.IntegrationExecute);
        }

        return Task.FromResult<IReadOnlyList<CapabilityDescriptor>>(Items.ToList());
    }

    public async Task<CapabilityDescriptor?> GetAsync(string key, CapabilityCaller caller, CancellationToken cancellationToken = default, Guid? scopeOrganizationId = null) =>
        (await ListAsync(caller, cancellationToken, scopeOrganizationId)).FirstOrDefault(d => d.Key == key);
}

public sealed class AdminServiceTests
{
    private sealed class Harness
    {
        public required RequestContextHolder Holder { get; init; }

        public InMemoryStore Store { get; init; } = null!;

        public FakeAudit Audit { get; } = new();

        public FakeCatalog Catalog { get; } = new();

        public WorkflowsOptions Options { get; } = new();

        public WorkflowDefinitionService Service => new(new WorkflowsAuthorizer(Holder, Audit), Store, Catalog, Options, new SimulationThrottle(new FakeClock()), new FakeClock());

        public WorkflowDefinitionService As(RequestContextHolder holder) => new(new WorkflowsAuthorizer(holder, Audit), new InMemoryStore(holder, Store.Rows), Catalog, Options, new SimulationThrottle(new FakeClock()), new FakeClock());

        public static Harness Create(RequestContextHolder? holder = null)
        {
            holder ??= Ctx.Admin(Ctx.TenantA, Ctx.OrgA);
            return new Harness { Holder = holder, Store = new InMemoryStore(holder) };
        }
    }

    private static string Json(FlowGraph g) => FlowGraphSerializer.Serialize(g);

    private static FlowGraph Simple() => Graph("a", [Msg("a", "Olá"), new("f", NodeType.End)], [E("a", "next", "f")]);

    private static SaveWorkflowRequest Req(string key = "boas-vindas", int? rev = null, string? json = null, Guid? org = null) =>
        new(org ?? Ctx.OrgA, key, "Boas-vindas", null, json ?? Json(Simple()), rev);

    [Fact]
    public async Task Draft_can_be_incomplete_but_publishing_needs_zero_problems()
    {
        var h = Harness.Create();
        var incomplete = Graph("a", [Msg("a", "Olá")], []);
        var saved = await h.Service.SaveDraftAsync(Req(json: Json(incomplete)));

        Assert.Equal(VersionStatus.Draft, saved.Workflow.Status);
        Assert.Contains(saved.Problems, p => p.Code == "port-unconnected");
        await Assert.ThrowsAsync<WorkflowsValidationException>(() => h.Service.PublishAsync(saved.Workflow.Id));
        Assert.DoesNotContain(h.Audit.Entries, e => e.Operation == "workflows.definition.publish");
    }

    [Fact]
    public async Task Publish_freezes_the_version_and_retires_the_previous_one_and_edits_become_a_new_version()
    {
        var h = Harness.Create();
        var v1 = (await h.Service.SaveDraftAsync(Req())).Workflow;
        var pub1 = await h.Service.PublishAsync(v1.Id);
        Assert.Equal(VersionStatus.Published, pub1!.Status);

        // Editar depois de publicar cria o rascunho da versão 2; a 1 não muda.
        var v2 = (await h.Service.SaveDraftAsync(Req(json: Json(Graph("a", [Msg("a", "Outro texto"), new("f", NodeType.End)], [E("a", "next", "f")]))))).Workflow;
        Assert.Equal(2, v2.Version);
        var pub2 = await h.Service.PublishAsync(v2.Id);

        var versions = await h.Service.VersionsAsync("boas-vindas");
        Assert.Equal([VersionStatus.Published, VersionStatus.Retired], versions.Select(v => v.Status));
        Assert.Equal(pub1.GraphHash, versions[1].GraphHash);
        Assert.NotEqual(pub1.GraphHash, pub2!.GraphHash);
        Assert.DoesNotContain("Outro texto", (await h.Service.GetAsync(v1.Id))!.GraphJson);

        var published = h.Store.Rows.Single(r => r.Id == v1.Id);
        Assert.Throws<WorkflowsConflictException>(() => published.UpdateDraft("x", null, "{}", "h", DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Updating_a_draft_requires_the_expected_revision()
    {
        var h = Harness.Create();
        var first = (await h.Service.SaveDraftAsync(Req())).Workflow;

        var stale = await Assert.ThrowsAsync<WorkflowsConflictException>(() => h.Service.SaveDraftAsync(Req(rev: first.Revision + 5)));
        Assert.Equal("changed", stale.Code);
        await Assert.ThrowsAsync<WorkflowsConflictException>(() => h.Service.SaveDraftAsync(Req(rev: null)));

        var ok = await h.Service.SaveDraftAsync(Req(rev: first.Revision));
        Assert.Equal(first.Revision + 1, ok.Workflow.Revision);
    }

    [Fact]
    public async Task Strict_parsing_rejects_unknown_fields_oversized_documents_and_bad_metadata()
    {
        var h = Harness.Create();
        await Assert.ThrowsAsync<WorkflowsValidationException>(() => h.Service.SaveDraftAsync(Req(json: """{"schemaVersion":1,"start":"a","nodes":[],"edges":[],"script":"x"}""")));
        await Assert.ThrowsAsync<WorkflowsValidationException>(() => h.Service.SaveDraftAsync(Req(key: "Chave Inválida")));
        h.Options.Limits = new FlowLimits(MaxDocumentBytes: 1024);
        await Assert.ThrowsAsync<WorkflowsValidationException>(() => h.Service.SaveDraftAsync(Req(json: Json(Menu()))));
    }

    [Fact]
    public async Task Permissions_are_distinct_and_checked_on_the_workflow_organization()
    {
        var h = Harness.Create();
        var saved = (await h.Service.SaveDraftAsync(Req())).Workflow;

        // Quem só edita não publica; quem não tem nada é recusado antes de qualquer consulta; outra organização é negada.
        var editor = h.As(Ctx.User(Ctx.TenantA, new PermissionGrant(Permission.WorkflowRead, Ctx.OrgA), new PermissionGrant(Permission.WorkflowManage, Ctx.OrgA)));
        await Assert.ThrowsAsync<AccessDeniedException>(() => editor.PublishAsync(saved.Id));
        await editor.SaveDraftAsync(Req(rev: saved.Revision));

        var nobody = h.As(Ctx.User(Ctx.TenantA, new PermissionGrant(Permission.ConversationRead, Ctx.OrgA)));
        await Assert.ThrowsAsync<AccessDeniedException>(() => nobody.ListAsync());
        await Assert.ThrowsAsync<AccessDeniedException>(() => nobody.GetAsync(Guid.NewGuid()));

        var otherOrg = h.As(Ctx.Admin(Ctx.TenantA, Ctx.OrgB));
        await Assert.ThrowsAsync<AccessDeniedException>(() => otherOrg.SaveDraftAsync(Req(org: Ctx.OrgA, rev: saved.Revision)));
        Assert.Null(await otherOrg.GetAsync(saved.Id));
        Assert.Empty(await otherOrg.ListAsync());
        Assert.Contains(h.Audit.Entries, e => e.Outcome == AuditOutcome.Denied);

        // Um contexto de sistema (sem usuário) nunca administra fluxos.
        var system = h.As(Ctx.System(Ctx.TenantA));
        await Assert.ThrowsAsync<AccessDeniedException>(() => system.ListAsync());
    }

    [Fact]
    public async Task Tenants_are_isolated_and_a_workflow_organization_never_changes()
    {
        var h = Harness.Create();
        var saved = (await h.Service.SaveDraftAsync(Req())).Workflow;

        var tenantB = h.As(Ctx.Admin(Ctx.TenantB, Ctx.OrgA));
        Assert.Null(await tenantB.GetAsync(saved.Id));
        Assert.Null(await tenantB.PublishAsync(saved.Id));
        Assert.Empty(await tenantB.ListAsync());
        Assert.Empty(await tenantB.VersionsAsync("boas-vindas"));

        var both = h.As(Ctx.User(Ctx.TenantA, new[] { Permission.WorkflowRead, Permission.WorkflowManage }.SelectMany(p => new[] { new PermissionGrant(p, Ctx.OrgA), new PermissionGrant(p, Ctx.OrgB) }).ToArray()));
        await Assert.ThrowsAsync<WorkflowsValidationException>(() => both.SaveDraftAsync(Req(org: Ctx.OrgB, rev: saved.Revision)));
    }

    [Fact]
    public async Task Integration_nodes_are_checked_against_the_catalog_in_the_workflow_organization_and_only_public_reads_pass()
    {
        var h = Harness.Create();
        h.Catalog.Items.Add(new CapabilityDescriptor("exam.price", 1, "Preço", null, CapabilityEffect.Read, false, [new("code", CapabilityParameterType.String, true, null, null)], ["valor"], CapabilityDataClass.Public));
        h.Catalog.Items.Add(new CapabilityDescriptor("exam.book", 1, "Agendar", null, CapabilityEffect.Write, true, [], [], CapabilityDataClass.Public));

        FlowGraph G(string key, params string[] bound) => Graph(
            "i",
            [new("i", NodeType.Integration, new NodeConfig(OperationKey: key, ResultPrefix: "r", Parameters: bound.ToDictionary(b => b, _ => new ParamBinding(Literal: "X")))), new("f", NodeType.End)],
            [E("i", "success", "f"), E("i", "failure", "f")]);

        Assert.Empty((await h.Service.SaveDraftAsync(Req(json: Json(G("exam.price", "code"))))).Problems);
        Assert.Equal(Ctx.OrgA, h.Catalog.LastScope);
        var write = (await h.Service.SaveDraftAsync(Req("outro", json: Json(G("exam.book"))))).Problems;
        Assert.Contains(write, p => p.Code == "operation-not-eligible");
        Assert.Contains((await h.Service.ValidateAsync(Ctx.OrgA, Json(G("exam.price", "code", "extra")))), p => p.Code == "parameter-unknown");
        Assert.Contains((await h.Service.ValidateAsync(Ctx.OrgA, Json(G("exam.price")))), p => p.Code == "parameter-missing");
        Assert.Contains((await h.Service.ValidateAsync(Ctx.OrgA, Json(G("nao.existe")))), p => p.Code == "operation-missing");

        // Sem permissão de integração o catálogo é ilegível: a validação recusa o que não consegue conferir e a publicação não passa.
        h.Catalog.Denied = true;
        var draft = (await h.Service.SaveDraftAsync(Req("terceiro", json: Json(G("exam.price", "code"))))).Workflow;
        await Assert.ThrowsAsync<WorkflowsValidationException>(() => h.Service.PublishAsync(draft.Id));
    }

    [Fact]
    public async Task Simulation_needs_its_own_permission_validates_first_and_is_throttled()
    {
        var h = Harness.Create();
        var scenario = new SimulationScenario([new(SimInputKind.Say, "oi")]);

        var ok = await h.Service.SimulateAsync(new SimulateRequest(Ctx.OrgA, Json(Simple()), scenario));
        Assert.Empty(ok.Problems);
        Assert.Equal("completed", ok.Simulation!.Outcome);

        var invalid = await h.Service.SimulateAsync(new SimulateRequest(Ctx.OrgA, Json(Graph("a", [Msg("a", "x")], [])), scenario));
        Assert.NotEmpty(invalid.Problems);
        Assert.Null(invalid.Simulation);

        var reader = h.As(Ctx.User(Ctx.TenantA, new PermissionGrant(Permission.WorkflowRead, Ctx.OrgA)));
        await Assert.ThrowsAsync<AccessDeniedException>(() => reader.SimulateAsync(new SimulateRequest(Ctx.OrgA, Json(Simple()), scenario)));

        h.Options.SimulationsPerMinutePerTenant = 2;
        var limited = h.Service;
        await limited.SimulateAsync(new SimulateRequest(Ctx.OrgA, Json(Simple()), scenario));
        await limited.SimulateAsync(new SimulateRequest(Ctx.OrgA, Json(Simple()), scenario));
        var over = await Assert.ThrowsAsync<WorkflowsConflictException>(() => limited.SimulateAsync(new SimulateRequest(Ctx.OrgA, Json(Simple()), scenario)));
        Assert.Equal("rate-limited", over.Code);

        var huge = new SimulationScenario(Enumerable.Range(0, 500).Select(_ => new SimInput(SimInputKind.Say, "x")).ToList());
        await Assert.ThrowsAsync<WorkflowsValidationException>(() => h.As(Ctx.Admin(Ctx.TenantA, Ctx.OrgA)).SimulateAsync(new SimulateRequest(Ctx.OrgA, Json(Simple()), huge)));
    }

    [Fact]
    public async Task The_audit_trail_has_no_graph_content_and_the_key_limit_is_enforced()
    {
        var h = Harness.Create();
        h.Options.MaxKeysPerTenant = 1;
        var saved = (await h.Service.SaveDraftAsync(Req())).Workflow;
        await h.Service.PublishAsync(saved.Id);
        var conflict = await Assert.ThrowsAsync<WorkflowsConflictException>(() => h.Service.SaveDraftAsync(Req("segundo")));
        Assert.Equal("limit-reached", conflict.Code);
        Assert.All(h.Audit.Entries, e => Assert.DoesNotContain("Olá", e.Operation + e.ResourceType + e.ResourceId));
        Assert.Contains(h.Audit.Entries, e => e.Operation == "workflows.definition.save" && e.Outcome == AuditOutcome.Success);
        Assert.Contains(h.Audit.Entries, e => e.Operation == "workflows.definition.publish");
    }
}
