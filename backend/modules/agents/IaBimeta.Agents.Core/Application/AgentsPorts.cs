using IaBimeta.Agents.Domain;

namespace IaBimeta.Agents.Application;

/// <summary>
/// Outra operação alterou o mesmo turno (ou criou o turno aberto da mesma sessão) primeiro. A mudança NÃO foi gravada e as entidades rastreadas foram descartadas: releia e decida de novo. Vem da
/// concorrência otimista (<see cref="AiTurn.Version"/>) e do índice único de um turno aberto por sessão.
/// </summary>
public sealed class AiTurnConflictException : Exception
{
    public AiTurnConflictException()
        : base("O turno foi alterado por outra operação. Releia e decida de novo.")
    {
    }
}

/// <summary>O armazenamento dos turnos do tenant do contexto (a Infrastructure o implementa, filtrando por tenant).</summary>
public interface IAiTurnStore
{
    /// <summary>
    /// O turno pelo identificador, lido do banco AGORA (um turno já rastreado neste escopo é descartado e relido, então o resultado nunca é um estado velho), ou nulo se não existe no tenant.
    /// Rastreado para alteração.
    /// </summary>
    Task<AiTurn?> FindAsync(Guid turnId, CancellationToken cancellationToken);

    /// <summary>O turno ABERTO (agendado, pronto ou em andamento) da sessão, lido agora, ou nulo. Rastreado. Há no máximo um.</summary>
    Task<AiTurn?> FindOpenBySessionAsync(Guid sessionId, CancellationToken cancellationToken);

    /// <summary>Marca um turno novo para gravação no próximo <see cref="SaveAsync"/>.</summary>
    void Add(AiTurn turn);

    /// <summary>Grava tudo numa transação. Conflito de versão ou de turno aberto da sessão: <see cref="AiTurnConflictException"/>, sem gravar nada.</summary>
    Task SaveAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reivindica, atomicamente e para o tenant do contexto, até <paramref name="max"/> turnos devidos (<see cref="AiTurn.IsDue"/>), respeitando o teto de turnos simultâneos do tenant
    /// (<see cref="AgentsOptions.MaxConcurrentTurnsPerTenant"/>) contado no banco, entre todos os processos. Cada turno reivindicado já volta em <see cref="AiTurnState.Running"/>, com o token de lane
    /// de uma sequência sempre crescente e o lease vigente. Dois executores nunca levam o mesmo turno.
    /// </summary>
    Task<IReadOnlyList<AiTurn>> ClaimDueAsync(string owner, int max, CancellationToken cancellationToken);
}

/// <summary>A leitura cruzada entre tenants que o Worker precisa para abrir um contexto de sistema por tenant. Só devolve identificadores de tenant, nunca dado de turno.</summary>
public interface IPendingTurnSource
{
    /// <summary>Os tenants com algum turno devido agora (agendado vencido, pronto vencido ou em andamento com o lease vencido), os que esperam há mais tempo primeiro.</summary>
    Task<IReadOnlyList<Guid>> ListTenantsWithDueTurnsAsync(DateTimeOffset now, int take, CancellationToken cancellationToken);

    /// <summary>Os tenants que já tiveram turnos de IA, os mais recentes primeiro: o reconciliador (CON-15) só olha quem usa a IA.</summary>
    Task<IReadOnlyList<Guid>> ListTenantsWithTurnsAsync(int take, CancellationToken cancellationToken);
}

/// <summary>A definição PUBLICADA do agente do tenant do contexto: a única que o turno usa (o rascunho nunca chega ao modelo).</summary>
public interface IAgentDefinitionReader
{
    /// <summary>O conteúdo publicado, ou nulo se o tenant ainda não publicou. Sem definição publicada a IA não responde (falha fechada: o turno transfere para o atendente).</summary>
    Task<AgentDefinitionContent?> GetPublishedAsync(CancellationToken cancellationToken);

    /// <summary>
    /// O conteúdo publicado JUNTO com o número da versão, numa só leitura (nunca um de cada vez: uma publicação no meio os desencontraria). A resposta direta por similaridade (ADR 0034) só vale contra o índice
    /// da MESMA versão. Nulo se não há versão publicada ou se a implementação não informa o número (falha fechada: a resposta por similaridade recusa).
    /// </summary>
    Task<PublishedDefinition?> GetPublishedVersionAsync(CancellationToken cancellationToken) => Task.FromResult<PublishedDefinition?>(null);
}

/// <summary>A versão publicada: o número e o conteúdo da mesma leitura.</summary>
public sealed record PublishedDefinition(int Number, AgentDefinitionContent Content);

/// <summary>O armazenamento das versões da definição do agente do tenant do contexto. As entidades voltam RASTREADAS para alteração.</summary>
public interface IAgentDefinitionStore : IAgentDefinitionReader
{
    /// <summary>O rascunho do tenant, ou nulo.</summary>
    Task<AgentDefinitionVersion?> FindDraftAsync(CancellationToken cancellationToken);

    /// <summary>A versão publicada do tenant, ou nulo.</summary>
    Task<AgentDefinitionVersion?> FindPublishedVersionAsync(CancellationToken cancellationToken);

    /// <summary>O próximo número de versão do tenant (nunca reaproveitado).</summary>
    Task<int> NextNumberAsync(CancellationToken cancellationToken);

    void Add(AgentDefinitionVersion version);

    /// <summary>Roda o trabalho (com várias gravações) numa transação do banco: ou tudo fica, ou nada.</summary>
    Task<T> InTransactionAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken);

    /// <summary>
    /// Bloqueio EXCLUSIVO da definição do tenant até o fim da transação corrente (só vale dentro de <see cref="InTransactionAsync{T}"/>): serializa quem lê o rascunho para alterá-lo e grava o resultado. Sem ele, duas
    /// alterações que leem a mesma base e gravam o conteúdo INTEIRO se sobrescrevem em silêncio (a segunda apaga a entrada da primeira) quando o rascunho ainda não existia na leitura.
    /// </summary>
    Task LockDefinitionAsync(CancellationToken cancellationToken);

    /// <summary>Grava tudo numa transação. Alteração concorrente do mesmo ponto de partida: <see cref="AgentDefinitionConflictException"/>.</summary>
    Task SaveAsync(CancellationToken cancellationToken);
}

/// <summary>Outra alteração da mesma definição aconteceu primeiro: releia e repita.</summary>
public sealed class AgentDefinitionConflictException : Exception
{
    public AgentDefinitionConflictException()
        : base("A definição do agente foi alterada por outra operação. Releia e tente de novo.")
    {
    }
}
