using IaBimeta.SharedKernel;

namespace IaBimeta.Agents.Domain;

/// <summary>O modo da resposta direta por similaridade de um tenant (ADR 0034). Começa <see cref="Off"/>: ligar é decisão do administrador, depois da avaliação.</summary>
public enum SemanticAnswerMode : short
{
    /// <summary>Desligado: a busca semântica nem é consultada. É o kill switch do tenant (vale na hora, sem publicar nada).</summary>
    Off = 0,

    /// <summary>Avaliação: a busca roda e a decisão é registrada (o que TERIA respondido), mas NUNCA responde: o turno segue ao modelo. Serve a calibrar os limiares com tráfego real sem risco.</summary>
    Evaluate = 1,

    /// <summary>Ativo: responde direto com o texto aprovado quando TODAS as salvaguardas passam.</summary>
    Active = 2,
}

/// <summary>O resultado de uma decisão semântica, registrado por turno (sem texto do cliente).</summary>
public enum SemanticOutcome : short
{
    /// <summary>Respondeu direto com o texto aprovado da entrada (modo ativo).</summary>
    Answered = 1,

    /// <summary>Teria respondido, mas o modo é avaliação: o turno seguiu ao modelo.</summary>
    WouldAnswer = 2,

    /// <summary>Nenhum candidato acima do piso de candidatos.</summary>
    NoCandidates = 3,

    /// <summary>O melhor candidato não atingiu o limiar de resposta direta do tenant.</summary>
    BelowThreshold = 4,

    /// <summary>Outra entrada ficou perto demais do melhor candidato: a similaridade não decide sozinha.</summary>
    Ambiguous = 5,

    /// <summary>O melhor candidato é uma entrada sem <c>AutoReply</c>: nunca responde direto.</summary>
    NotAutoReply = 6,

    /// <summary>Uma salvaguarda determinística recusou (negação, números, tamanho): o texto parece com a pergunta, mas pode pedir o contrário ou outra coisa.</summary>
    GuardRejected = 7,

    /// <summary>O índice foi montado para outra versão da definição que a publicada agora: nunca responde com índice defasado.</summary>
    Stale = 8,

    /// <summary>O serviço de vetores ou o índice não estavam disponíveis: o turno segue o fluxo normal.</summary>
    Unavailable = 9,
}

/// <summary>
/// A configuração da resposta direta por similaridade de UM tenant (ADR 0034). Chave: o tenant. Os limiares são DO TENANT, conservadores por padrão e calibrados no modo de avaliação. Mudar vale na hora (não
/// passa pela publicação do conhecimento): é o interruptor de emergência do administrador. A concorrência é otimista (<see cref="Revision"/>).
/// </summary>
public sealed class SemanticAnswerSettings : ITenantOwned
{
    /// <summary>O limiar padrão de similaridade para responder direto. CONSERVADOR e ainda NÃO calibrado com o modelo real: a homologação mede e ajusta.</summary>
    public const double DefaultMinScore = 0.90;

    /// <summary>A distância mínima entre o melhor candidato e o melhor de OUTRA entrada.</summary>
    public const double DefaultMinMargin = 0.05;

    public const double MinScoreFloor = 0.60;

    private SemanticAnswerSettings()
    {
        TenantId = null!;
    }

    public TenantId TenantId { get; private set; }

    public SemanticAnswerMode Mode { get; private set; }

    public double MinScore { get; private set; }

    public double MinMargin { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public Guid? UpdatedBy { get; private set; }

    public int Revision { get; private set; }

    /// <summary>As configurações de um tenant que nunca mexeu: desligado, com os limiares padrão.</summary>
    public static SemanticAnswerSettings Default(TenantId tenant, DateTimeOffset now) => new()
    {
        TenantId = tenant,
        Mode = SemanticAnswerMode.Off,
        MinScore = DefaultMinScore,
        MinMargin = DefaultMinMargin,
        UpdatedAt = now,
        Revision = 1,
    };

    /// <exception cref="AgentsValidationException">Limiar fora do intervalo seguro.</exception>
    public void Apply(SemanticAnswerMode mode, double minScore, double minMargin, DateTimeOffset now, Guid? by)
    {
        if (!Enum.IsDefined(mode) || double.IsNaN(minScore) || minScore is < MinScoreFloor or > 1 || double.IsNaN(minMargin) || minMargin is < 0 or > 0.5)
        {
            throw new AgentsValidationException($"O limiar de similaridade deve ficar entre {MinScoreFloor:0.00} e 1 e a margem entre 0 e 0,5.");
        }

        Mode = mode;
        MinScore = Math.Round(minScore, 4);
        MinMargin = Math.Round(minMargin, 4);
        UpdatedAt = now;
        UpdatedBy = by;
        Revision++;
    }
}

/// <summary>
/// O registro de UMA decisão semântica de um turno (ADR 0034): sem o texto do cliente, só o que mediu. Chave <c>(tenant, turno)</c>: o turno repetido conta uma vez. Serve às métricas (acertos, sem correspondência,
/// ambíguas, latência) e à calibração dos limiares.
/// </summary>
public sealed class SemanticDecision : ITenantOwned
{
    private SemanticDecision()
    {
        TenantId = null!;
    }

    public TenantId TenantId { get; private set; }

    public Guid TurnId { get; private set; }

    public DateTimeOffset DecidedAt { get; private set; }

    public SemanticAnswerMode Mode { get; private set; }

    public SemanticOutcome Outcome { get; private set; }

    /// <summary>A entrada do FAQ candidata (posição na definição publicada), quando houve.</summary>
    public int? EntryOrdinal { get; private set; }

    public double? Score { get; private set; }

    public double? Margin { get; private set; }

    public int? DefinitionNumber { get; private set; }

    public int EmbeddingMilliseconds { get; private set; }

    public int SearchMilliseconds { get; private set; }

    /// <summary>O código curto e fixo da indisponibilidade (nunca texto do provedor nem do cliente).</summary>
    public string? FailureCode { get; private set; }
}

/// <summary>O que o <c>TurnRunner</c> grava de uma decisão semântica.</summary>
public sealed record SemanticDecisionRecord(
    TenantId Tenant, Guid TurnId, DateTimeOffset At, SemanticAnswerMode Mode, SemanticOutcome Outcome, int? EntryOrdinal, double? Score, double? Margin, int? DefinitionNumber,
    int EmbeddingMilliseconds, int SearchMilliseconds, string? FailureCode);
