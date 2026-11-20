using System.Reflection;
using IaBimeta.Conversations.Application;
using IaBimeta.Conversations.Contracts.Queries;
using IaBimeta.Conversations.Domain;
using IaBimeta.Identity.Contracts.Queries;
using IaBimeta.SharedKernel;

namespace IaBimeta.Conversations.Tests;

/// <summary>E2.3: as consultas do vínculo falham FECHADAS (ADR 0041, seção 3). Sem banco: o leitor e o Identity são dublês.</summary>
public sealed class ContactLinkQueryServiceTests
{
    private static readonly TenantId Tenant = new TenantId(Guid.NewGuid());
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid Conexao = Guid.NewGuid();

    private sealed class Leitor : IContactLinkReader
    {
        public int Chamadas { get; private set; }

        public List<LinkedSessionRow> Linhas { get; } = new();

        public Task<(bool Resolved, bool Shared)> GetLinkStateAsync(Guid connectionId, Guid organizationId, string customerRef, CancellationToken cancellationToken)
        {
            Chamadas++;
            return Task.FromResult((true, false));
        }

        public Task<IReadOnlyList<LinkedSessionRow>> ListSessionsAsync(Guid connectionId, Guid organizationId, IReadOnlyCollection<string> customerRefs, SessionCursor? after, int take, CancellationToken cancellationToken)
        {
            Chamadas++;
            return Task.FromResult<IReadOnlyList<LinkedSessionRow>>(Linhas.Take(take + 1).ToList());
        }

        public Task<bool> IsSessionLinkedAsync(Guid sessionId, Guid connectionId, Guid organizationId, IReadOnlyCollection<string> customerRefs, CancellationToken cancellationToken)
        {
            Chamadas++;
            return Task.FromResult(true);
        }
    }

    private sealed class Embutida(EmbeddedSessionInfo? info) : IEmbeddedContextQuery
    {
        public Task<EmbeddedSessionInfo?> GetCurrentAsync(CancellationToken cancellationToken = default) => Task.FromResult(info);

        public Task<bool> IsCustomerGrantedAsync(string customerRef, CancellationToken cancellationToken = default) => Task.FromResult(info?.CustomerRefs.Contains(customerRef) == true);
    }

    private sealed class Acessor(RequestContext? contexto) : IRequestContextAccessor
    {
        public RequestContext? Current => contexto;
    }

    private static RequestContext Contexto(EmbeddedScope? escopo, bool comUsuario = true)
    {
        var principal = comUsuario ? new ActorPrincipal(Guid.NewGuid(), Guid.NewGuid(), Array.Empty<PermissionGrant>(), escopo) : null;
        var ctor = typeof(RequestContext).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, new[] { typeof(TenantId), typeof(string), typeof(ActorPrincipal) })!;
        return (RequestContext)ctor.Invoke(new object?[] { Tenant, "teste", principal });
    }

    private static EmbeddedSessionInfo Info(Guid? org = null, params string[] clientes) =>
        new(Guid.NewGuid(), Conexao, org ?? Org, EmbeddedMode.CustomerOnly, "u", clientes);

    private static ContactLinkQueryService Servico(Leitor leitor, RequestContext? contexto, EmbeddedSessionInfo? info) =>
        new(leitor, new Acessor(contexto), new Embutida(info));

    [Fact]
    public async Task Sem_sessao_embutida_ou_com_contexto_de_sistema_ou_de_usuario_interativo_tudo_e_negado_sem_consultar_o_banco()
    {
        var leitor = new Leitor();
        leitor.Linhas.Add(new LinkedSessionRow(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null));
        var info = Info(null, "CLI-1");

        foreach (var contexto in new[] { Contexto(null), Contexto(null, comUsuario: false) })
        {
            var s = Servico(leitor, contexto, info); // o Identity até responderia, mas o contexto não é de sessão embutida
            Assert.Null(await s.ListCustomerSessionsAsync("CLI-1", 10, null));
            Assert.False(await s.IsSessionOfGrantedCustomerAsync(Guid.NewGuid()));
            Assert.Empty(await s.ListSessionIdsOfGrantedCustomersAsync(10));
        }

        Assert.Equal(0, leitor.Chamadas);
    }

    [Fact]
    public async Task Cliente_nao_autorizado_organizacao_divergente_e_lista_de_clientes_vazia_negam()
    {
        var leitor = new Leitor();
        leitor.Linhas.Add(new LinkedSessionRow(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null));
        var contexto = Contexto(new EmbeddedScope(Org, CustomerOnly: true));

        Assert.Null(await Servico(leitor, contexto, Info(null, "CLI-1")).ListCustomerSessionsAsync("CLI-2", 10, null)); // não autorizado na sessão
        Assert.Null(await Servico(leitor, contexto, Info(null, "CLI-1")).ListCustomerSessionsAsync("cli-1", 10, null)); // a comparação é exata
        Assert.Null(await Servico(leitor, contexto, Info(null, "CLI-1")).ListCustomerSessionsAsync("  ", 10, null));
        Assert.Null(await Servico(leitor, contexto, Info(null, "CLI-1")).ListCustomerSessionsAsync(new string('x', 129), 10, null));

        // A organização da conexão (Identity) diverge do limite do contexto: nega tudo.
        var divergente = Servico(leitor, contexto, Info(Guid.NewGuid(), "CLI-1"));
        Assert.Null(await divergente.ListCustomerSessionsAsync("CLI-1", 10, null));
        Assert.False(await divergente.IsSessionOfGrantedCustomerAsync(Guid.NewGuid()));

        // Sem nenhum cliente em foco: nada a autorizar.
        var semClientes = Servico(leitor, contexto, Info());
        Assert.False(await semClientes.IsSessionOfGrantedCustomerAsync(Guid.NewGuid()));
        Assert.Empty(await semClientes.ListSessionIdsOfGrantedCustomersAsync(10));
        Assert.Equal(0, leitor.Chamadas);
    }

    [Fact]
    public async Task Autorizado_pagina_com_limite_e_cursor_e_recusa_limite_e_cursor_invalidos()
    {
        var leitor = new Leitor();
        for (var i = 0; i < 5; i++)
        {
            leitor.Linhas.Add(new LinkedSessionRow(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(-i), DateTimeOffset.UtcNow.AddMinutes(-i), null));
        }

        var s = Servico(leitor, Contexto(new EmbeddedScope(Org, CustomerOnly: false)), Info(null, "CLI-1"));

        var pagina = await s.ListCustomerSessionsAsync("CLI-1", 2, null);
        Assert.NotNull(pagina);
        Assert.Equal(2, pagina!.Items.Count);
        Assert.NotNull(pagina.NextCursor); // havia mais
        Assert.True(pagina.Resolved);

        var tudo = await s.ListCustomerSessionsAsync("CLI-1", 10, null);
        Assert.Equal(5, tudo!.Items.Count);
        Assert.Null(tudo.NextCursor);

        await Assert.ThrowsAsync<ConversationsValidationException>(() => s.ListCustomerSessionsAsync("CLI-1", 0, null));
        await Assert.ThrowsAsync<ConversationsValidationException>(() => s.ListCustomerSessionsAsync("CLI-1", ConversationLimits.MaxPageSize + 1, null));
        await Assert.ThrowsAsync<InvalidPageCursorException>(() => s.ListCustomerSessionsAsync("CLI-1", 2, "adulterado"));
        Assert.Empty(await s.ListSessionIdsOfGrantedCustomersAsync(0)); // limite inválido: vazio, nunca ilimitado
    }
}
