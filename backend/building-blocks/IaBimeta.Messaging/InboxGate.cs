using IaBimeta.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IaBimeta.Messaging;

/// <summary>
/// A idempotência por <c>EventId</c> de um consumidor (BOUNDARIES.md §12.5, "Idempotência em camadas"): o registro de que o
/// evento foi tratado e o efeito do handler acontecem na MESMA transação do DbContext do módulo consumidor. Entrega repetida
/// (o relay entrega pelo menos uma vez) encontra o registro e não repete o efeito; falha desfaz as duas coisas juntas, e a nova
/// tentativa refaz tudo. O handler usa serviços do mesmo escopo, então as gravações dele entram na transação aberta aqui.
/// </summary>
public sealed class InboxGate<TContext>
    where TContext : DbContext
{
    private readonly TContext _db;
    private readonly TimeProvider _clock;

    public InboxGate(TContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>
    /// Roda <paramref name="action"/> uma única vez para este evento e este consumidor. Devolve <see cref="HandlerResult.Success"/>
    /// sem rodar nada se o evento já foi tratado. O registro só fica se a ação devolver sucesso e confirmar.
    /// </summary>
    /// <param name="consumer">O nome estável do consumidor dentro do módulo.</param>
    public async Task<HandlerResult> RunOnceAsync(
        TenantId tenantId, string consumer, Guid eventId, Func<CancellationToken, Task<HandlerResult>> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(consumer);
        ArgumentNullException.ThrowIfNull(action);

        var tipo = _db.Model.FindEntityType(typeof(InboxMessage))
            ?? throw new InvalidOperationException("O DbContext do consumidor não mapeou o inbox (ConfigureInbox).");
        var tabela = tipo.GetTableName();
        var nomeDaTabela = tipo.GetSchema() is { } esquema ? $"\"{esquema}\".\"{tabela}\"" : $"\"{tabela}\"";

        await using var transacao = await _db.Database.BeginTransactionAsync(cancellationToken);

        // O INSERT ... ON CONFLICT DO NOTHING é a barreira: de duas entregas simultâneas do mesmo evento, só uma o grava. O nome da
        // tabela vem do modelo do EF (nunca de dado externo) e os valores vão como parâmetros.
#pragma warning disable EF1002
        var gravadas = await _db.Database.ExecuteSqlRawAsync(
            $"INSERT INTO {nomeDaTabela} (tenant_id, consumer, event_id, processed_at) VALUES ({{0}}, {{1}}, {{2}}, {{3}}) ON CONFLICT DO NOTHING",
            new object[] { tenantId.Value, consumer, eventId, _clock.GetUtcNow() },
            cancellationToken);
#pragma warning restore EF1002
        if (gravadas == 0)
        {
            await transacao.RollbackAsync(cancellationToken);
            return HandlerResult.Success;
        }

        HandlerResult resultado;
        try
        {
            resultado = await action(cancellationToken);
        }
        catch
        {
            await transacao.RollbackAsync(CancellationToken.None);
            throw;
        }

        if (resultado.Outcome != HandlerOutcome.Success)
        {
            await transacao.RollbackAsync(cancellationToken);
            return resultado;
        }

        await transacao.CommitAsync(cancellationToken);
        return resultado;
    }
}

public static class MessagingServiceCollectionExtensions
{
    /// <summary>
    /// Registra o Messaging: as opções (validadas na partida), o escritor do outbox e o relógio. O host registra o
    /// <see cref="IMessagingScopeInitializer"/> (só ele cria o contexto do tenant, TEN-1).
    /// </summary>
    public static IServiceCollection AddMessaging(this IServiceCollection services, Action<MessagingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var opcoes = new MessagingOptions();
        configure?.Invoke(opcoes);
        opcoes.Validate();

        services.TryAddSingleton(opcoes);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IOutboxWriter, OutboxWriter>();
        return services;
    }

    /// <summary>Registra o relay do outbox do DbContext de um módulo emissor.</summary>
    public static IServiceCollection AddOutboxRelay<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        services.TryAddSingleton<OutboxRelay<TContext>>();
        services.AddSingleton<IOutboxRelay>(provider => provider.GetRequiredService<OutboxRelay<TContext>>());
        return services;
    }

    /// <summary>Registra o handler de um evento (um escopo por entrega). O módulo consumidor chama isto na própria Infrastructure.</summary>
    public static IServiceCollection AddEventHandler<THandler>(this IServiceCollection services)
        where THandler : class, IEventHandler
    {
        services.AddScoped<IEventHandler, THandler>();
        return services;
    }

    /// <summary>Registra a porta de idempotência do consumidor (inbox) sobre o DbContext do módulo.</summary>
    public static IServiceCollection AddInboxGate<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<InboxGate<TContext>>();
        return services;
    }
}
