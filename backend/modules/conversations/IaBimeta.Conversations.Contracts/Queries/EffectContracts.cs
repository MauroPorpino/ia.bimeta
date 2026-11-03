namespace IaBimeta.Conversations.Contracts.Queries;

/// <summary>
/// Qual efeito se pergunta: a transição de atendimento (<c>TransitionId</c>), o encaminhamento de unidade (<c>TransferId</c>) e, previstos pelas emendas do
/// ADR 0009, a resposta humana (7ª operação) e o encerramento delegado (9ª operação). Os dois últimos são RESERVADOS: a consulta de efeito os RECUSA
/// (<c>ConversationsValidationException</c>, antes de ler qualquer coisa) até serem respondidos, junto das operações (fatias A4 e A6). Só a transição e o
/// encaminhamento têm consulta hoje, e qualquer outro valor falha fechado.
/// </summary>
public enum EffectKind
{
    Transition = 1,

    Transfer = 2,

    /// <summary>O <c>OperationId</c> da resposta humana (ADR 0014, seção A). Reservado até a fatia A4.</summary>
    HumanReply = 3,

    /// <summary>O <c>OperationId</c> do encerramento delegado (ADR 0009, item 10). Reservado até a fatia A6.</summary>
    SessionClose = 4,
}

/// <summary>
/// A pergunta de SOMENTE LEITURA "este efeito já foi aplicado?": o tenant vem do contexto confiável, nunca do pedido; a chave é a sessão, o tipo do efeito
/// e o identificador que a intenção do chamador guardou (o <c>TransitionId</c> da transição, ou o <c>TransferId</c> do encaminhamento, que são DISTINTOS
/// entre si e do identificador do pedido do usuário). Uma transição derivada (a "humano sem responsável" da transferência sem o atendente) tem o
/// identificador derivado dela, não o da original.
/// </summary>
public sealed record GetEffectQuery(Guid SessionId, EffectKind Kind, Guid OperationId);

/// <summary>O que a consulta de efeito sabe dizer. Só <see cref="Applied"/> é PROVA; os demais nunca concedem autoridade.</summary>
public enum EffectOutcome
{
    /// <summary>O efeito foi aplicado, e o Conversations o registrou com o estado que ele produziu (a versão, a fronteira e o período, quando conhecidos).</summary>
    Applied = 1,

    /// <summary>
    /// Nenhum registro deste efeito, neste instante. NÃO é falha definitiva (a operação ainda pode ser aplicada depois, pelo mesmo identificador) e não
    /// concede autoridade alguma. A sessão que não existe no tenant, ou que o contexto não pode ler, responde como o efeito inexistente (TEN-2).
    /// </summary>
    NotFound = 2,

    /// <summary>
    /// A consulta não pôde ser respondida (falha de infraestrutura, tempo esgotado). Quem a faz a produz ao capturar a falha: o provedor devolve os demais.
    /// A pendência continua, e a pergunta é repetida depois.
    /// </summary>
    Unavailable = 3,

    /// <summary>
    /// Há registro, mas ele NÃO PROVA uma mudança: a operação foi avaliada e não mudou nada (a sessão já estava no estado pedido por outra via), ou o
    /// registro é anterior aos campos que provariam a mudança. Quem consome não deve repetir nem concluir a operação com base nele.
    /// </summary>
    Indeterminate = 4,
}

/// <summary>
/// A resposta da consulta de efeito. Os campos só existem em <see cref="EffectOutcome.Applied"/>, e cada um pode ser nulo se o registro é anterior a ele:
/// nulo é identidade DESCONHECIDA, nunca zero nem "igual ao estado de agora". O encaminhamento não carrega período nem fronteira, e nenhum registro
/// guarda a geração da colocação: quem precisa dela a deriva da autorização da operação, ou a trata como desconhecida.
/// </summary>
/// <param name="StateVersion">A <c>StateVersion</c> em que a sessão ficou depois do efeito.</param>
/// <param name="MessageSeq">A sequência da última mensagem no instante do efeito (a fronteira estável do que veio antes e depois dele).</param>
/// <param name="HandlingPeriod">O período de atendimento humano em que a sessão ficou depois do efeito.</param>
public sealed record EffectView(EffectOutcome Outcome, long? StateVersion = null, long? MessageSeq = null, long? HandlingPeriod = null);
