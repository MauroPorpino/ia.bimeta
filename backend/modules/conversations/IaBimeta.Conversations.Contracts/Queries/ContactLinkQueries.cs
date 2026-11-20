namespace IaBimeta.Conversations.Contracts.Queries;

/// <summary>Uma sessão de atendimento de um contato vinculado ao cliente em foco. Só identificadores e datas: o conteúdo das mensagens segue pelas rotas de item, com as autorizações delas.</summary>
public sealed record CustomerSessionRef(Guid SessionId, Guid ContactId, DateTimeOffset OpenedAt, DateTimeOffset LastMessageAt, DateTimeOffset? ClosedAt);

/// <param name="Resolved">O cliente tem pelo menos um vínculo ativo nesta conexão.</param>
/// <param name="Shared">O cliente está vinculado a mais de um contato, ou algum desses contatos está vinculado a outro cliente da conexão.</param>
/// <param name="NextCursor">Opaco; <c>null</c> quando não há mais páginas.</param>
public sealed record CustomerSessionPage(bool Resolved, bool Shared, IReadOnlyList<CustomerSessionRef> Items, string? NextCursor);

/// <summary>O cursor recebido não é um cursor desta consulta (adulterado ou de outro formato): erro do pedido (400), nunca ignorado.</summary>
public sealed class InvalidPageCursorException : Exception
{
    public InvalidPageCursorException()
        : base("O cursor da página é inválido.")
    {
    }
}

/// <summary>
/// As consultas do vínculo que o ContactCenter usa para o histórico por cliente e para o modo <c>CustomerOnly</c> (ADR 0041, seção 3; E2.3). Todas valem para a SESSÃO EMBUTIDA corrente (tenant, sessão, conexão, organização e
/// clientes em foco vêm do contexto e do Identity, nunca de parâmetro) e relêem o banco a CADA chamada: nada fica em cache, então a desvinculação vale na requisição seguinte. As cinco condições são simultâneas e a falta de
/// qualquer uma nega: tenant do contexto, sessão embutida ativa, cliente autorizado na sessão, vínculo ATIVO entre o cliente e o contato e a organização da conexão. Falha fechada: contexto que não é de sessão embutida,
/// cliente não autorizado e vínculo inexistente ou desfeito devolvem a mesma negação. O vínculo só localiza o contato; a decisão de acesso ao item continua sendo do ContactCenter.
/// </summary>
public interface IContactLinkQueries
{
    /// <summary>
    /// As sessões dos contatos com vínculo ativo para o cliente, da conversa mais recente para a mais antiga (ordem estável por <c>(última mensagem, id)</c>), de <paramref name="take"/> em <paramref name="take"/>. Inclui as
    /// sessões ANTERIORES e as POSTERIORES à criação do vínculo (o vínculo é do contato). <c>null</c> = cliente não autorizado na sessão embutida (ou não há sessão embutida): a mesma resposta de um cliente que não existe.
    /// </summary>
    /// <exception cref="InvalidPageCursorException">O cursor é inválido.</exception>
    Task<CustomerSessionPage?> ListCustomerSessionsAsync(string customerRef, int take, string? cursor, CancellationToken cancellationToken = default);

    /// <summary>A sessão é de um contato com vínculo ativo para ALGUM cliente autorizado na sessão embutida corrente, na organização da conexão? Falso para tudo o mais.</summary>
    Task<bool> IsSessionOfGrantedCustomerAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>As sessões (as mais recentes, no máximo <paramref name="take"/>) dos contatos com vínculo ativo para os clientes autorizados na sessão embutida corrente. Vazio para tudo o mais.</summary>
    Task<IReadOnlyList<Guid>> ListSessionIdsOfGrantedCustomersAsync(int take, CancellationToken cancellationToken = default);
}
