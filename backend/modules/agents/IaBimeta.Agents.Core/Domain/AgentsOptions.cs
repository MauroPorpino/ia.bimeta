using IaBimeta.Conversations.Contracts;

namespace IaBimeta.Agents.Domain;

/// <summary>
/// A configuração do Agents (seção <c>Agents:Options</c>): agrupamento, interrupções, tentativas, tempos e capacidade (CONVERSATION-LIFECYCLE.md §14). Os valores padrão são a REFERÊNCIA
/// inicial aprovada, a calibrar com dados reais: configuração, não constantes de código. O Agents controla estes parâmetros; o Conversations aplica regras fixas de aceitação e não os lê.
/// No MVP os valores valem para todos os tenants (a configuração por tenant é evolução futura).
/// </summary>
public sealed class AgentsOptions
{
    /// <summary>A janela quieta do agrupamento: o turno espera isto depois da ÚLTIMA mensagem (GRP-1).</summary>
    public TimeSpan QuietWindow { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>O teto da espera do agrupamento, contado desde a PRIMEIRA mensagem pendente (GRP-1).</summary>
    public TimeSpan MaxWait { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Quantas interrupções (mensagem nova com o turno em andamento) a rajada admite sem resposta aceita; depois o turno passa a não ser interrompível (INT-1, T12).</summary>
    public int MaxSupersedesPerBurst { get; set; } = 2;

    /// <summary>As tentativas de um mesmo turno por falha transitória (ou saída inválida) antes de ele morrer (T10).</summary>
    public int MaxTurnAttempts { get; set; } = 3;

    /// <summary>A espera depois da primeira falha transitória; dobra a cada tentativa até <see cref="MaxRetryBackoff"/>.</summary>
    public TimeSpan RetryBackoff { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan MaxRetryBackoff { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// O tempo que um executor tem para terminar um turno: vale como lease. Passado esse tempo sem conclusão, o turno é reivindicado de novo (o processo caiu). Precisa ser MAIOR que
    /// o tempo limite da chamada ao modelo, com folga para registrar a resposta.
    /// </summary>
    public TimeSpan TurnMaxDuration { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>O tempo limite de UMA chamada ao modelo (o AiGateway o limita ao máximo dele).</summary>
    public TimeSpan ModelCallTimeout { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>Quantas mensagens recentes entram no contexto do turno (o Conversations limita ao teto dele).</summary>
    public int MaxInputMessages { get; set; } = 30;

    /// <summary>O teto de tokens de saída de uma resposta (o AiGateway o limita ao teto do tenant e ao do modelo).</summary>
    public int MaxOutputTokens { get; set; } = 600;

    /// <summary>
    /// Liga a resposta direta do FAQ (ADR 0029): a mensagem IGUAL à pergunta ou a um alias de uma entrada com <c>AutoReply</c> é respondida com o texto aprovado do tenant, sem chamar o modelo. Desligar manda todos os
    /// turnos ao modelo, sem mexer nas definições. Vale para todos os tenants (no MVP não há configuração por tenant); o opt-in por entrada continua sendo da definição.
    /// </summary>
    public bool FaqDirectAnswerEnabled { get; set; } = true;

    /// <summary>
    /// O interruptor GLOBAL da resposta direta por similaridade (ADR 0034). Desligado (padrão), nenhum tenant consulta a busca semântica, qualquer que seja o modo dele. Ligado, cada tenant ainda precisa ter o
    /// modo <c>Evaluate</c> ou <c>Active</c> (o padrão do tenant é desligado) e o <c>Knowledge:Semantic:Enabled</c> do índice. Os três são independentes: qualquer um desligado devolve o turno ao fluxo anterior.
    /// </summary>
    public bool SemanticAnswerEnabled { get; set; }

    /// <summary>
    /// Liga as sugestões da IA LOCAL para o FAQ (ADR 0034): classificar dúvidas, agrupar perguntas parecidas, sugerir aliases e entradas novas e apontar duplicatas e contradições. Desligado por padrão. A IA só propõe
    /// (a revisão humana decide e nada é publicado por ela) e só roda quando o administrador pede.
    /// </summary>
    public bool LocalSuggestionsEnabled { get; set; }

    /// <summary>
    /// Liga a consulta do agente a operações PÚBLICAS de leitura do Motor de Integração (ADR 0036). Desligado por padrão. Ligado, o turno com operações elegíveis pode pedir UMA consulta (esquema `lookup`) e faz uma segunda chamada ao modelo
    /// com o resultado. Exige o módulo Capabilities composto no host e `TurnMaxDuration` maior que duas chamadas ao modelo mais o orçamento da consulta.
    /// </summary>
    public bool IntegrationLookupsEnabled { get; set; }

    /// <summary>Quantas consultas um turno pode fazer. Nesta fase só 1 (ADR 0036); outro valor é recusado na partida.</summary>
    public int LookupMaxPerTurn { get; set; } = 1;

    /// <summary>O orçamento de TODA a consulta (catálogo já oferecido, execução e resposta do motor). Estourado, o turno transfere para um atendente (`integration_unavailable`). De 1 a 30 s.</summary>
    public TimeSpan LookupBudget { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Liga o REGISTRO DE LACUNAS do FAQ (ADR 0030): a pergunta de cliente que não teve resposta direta e vale uma revisão vira uma ocorrência (só referências e um hash, nunca o texto). Independe da resposta direta:
    /// um tenant que ainda não ligou nenhum <c>AutoReply</c> também descobre o que os clientes perguntam. Desligado, nada é gravado. Sem <see cref="KnowledgeGapHashKey"/> o registro também não roda.
    /// </summary>
    public bool KnowledgeGapsEnabled { get; set; } = true;

    /// <summary>
    /// A chave MESTRA (Base64, pelo menos 32 bytes) de onde sai a chave do hash de cada tenant (<see cref="KnowledgeGapHasher"/>). Vem da configuração do ambiente, nunca de arquivo versionado, e NUNCA vai a log nem
    /// a resposta. Ausente: o registro de lacunas fica desligado (o turno não falha por isso). Inválida ou curta: o host não parte.
    /// </summary>
    public string? KnowledgeGapHashKey { get; set; }

    /// <summary>
    /// Quantas lacunas (perguntas distintas) um tenant guarda. Atingido o teto, as perguntas NOVAS não abrem lacuna (as que já existem continuam contando): um cliente que manda mil textos diferentes não enche
    /// o banco. A revisão (descartar ou enviar ao rascunho) e, depois, a retenção é que liberam espaço.
    /// </summary>
    public int KnowledgeGapMaxPerTenant { get; set; } = 5000;

    /// <summary>A espera por capacidade: o limite de chamadas simultâneas do tenant no AiGateway está cheio, e o turno volta depois sem gastar tentativa.</summary>
    public TimeSpan CapacityRetry { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>De quanto em quanto tempo um turno que espera conteúdo pendente reavalia o grupo.</summary>
    public TimeSpan ContentRecheck { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>A espera adicional por conteúdo pendente, por tipo (MED-2). Sem processador de mídia no MVP, a espera é curta; vencida, vale a política do tipo.</summary>
    public TimeSpan AudioExtraWait { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan ImageExtraWait { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan DocumentExtraWait { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Vídeo e outros tipos não têm processamento previsto: sem espera, vão direto ao atendente (MED-2).</summary>
    public TimeSpan VideoAndOtherExtraWait { get; set; } = TimeSpan.Zero;

    /// <summary>A idade máxima de um turno que só espera (capacidade ou reentrega do pedido de handoff): passada, ele morre em vez de esperar para sempre.</summary>
    public TimeSpan MaxTurnAge { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Quantos turnos de um tenant rodam ao mesmo tempo no executor (contados no banco, entre todos os processos). Fica abaixo do limite de chamadas simultâneas do AiGateway.</summary>
    public int MaxConcurrentTurnsPerTenant { get; set; } = 4;

    /// <summary>Quantos turnos o executor leva a trabalho por ciclo, somando os tenants.</summary>
    public int ExecutorBatchSize { get; set; } = 32;

    /// <summary>Quantos turnos rodam em paralelo no mesmo processo.</summary>
    public int ExecutorParallelism { get; set; } = 8;

    /// <summary>De quanto em quanto tempo o executor procura turnos devidos.</summary>
    public TimeSpan ExecutorInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>A espera depois de o executor encerrar limpo (T11) ou de perder a vaga do tenant: sem contar tentativa.</summary>
    public TimeSpan CleanShutdownRetry { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>O reconciliador (CON-15) só olha sessões cuja última mensagem tem pelo menos esta idade, para não disputar com o handler do evento.</summary>
    public TimeSpan ReconcileMinimumAge { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Quantas sessões abertas mais recentes cada tenant o reconciliador examina por ciclo.</summary>
    public int ReconcileSessionsPerTenant { get; set; } = 100;

    /// <summary>De quanto em quanto tempo o reconciliador roda.</summary>
    public TimeSpan ReconcileInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Quantos tenants o reconciliador atende por ciclo.</summary>
    public int ReconcileTenantBatchSize { get; set; } = 100;

    /// <summary>A espera adicional para um tipo de conteúdo (MED-2).</summary>
    public TimeSpan ExtraWaitFor(MessageKind kind) => kind switch
    {
        MessageKind.Audio => AudioExtraWait,
        MessageKind.Image => ImageExtraWait,
        MessageKind.Document => DocumentExtraWait,
        _ => VideoAndOtherExtraWait,
    };

    /// <exception cref="InvalidOperationException">Algum valor é inválido: falha na partida, nunca no meio do processamento.</exception>
    public void Validate()
    {
        var positivos = new[]
        {
            QuietWindow, MaxWait, RetryBackoff, MaxRetryBackoff, TurnMaxDuration, ModelCallTimeout, CapacityRetry, ContentRecheck, MaxTurnAge, ExecutorInterval, ReconcileInterval,
        };
        var naoNegativos = new[] { AudioExtraWait, ImageExtraWait, DocumentExtraWait, VideoAndOtherExtraWait, CleanShutdownRetry, ReconcileMinimumAge };

        if (positivos.Any(t => t <= TimeSpan.Zero)
            || naoNegativos.Any(t => t < TimeSpan.Zero)
            || MaxWait < QuietWindow
            || MaxRetryBackoff < RetryBackoff
            || TurnMaxDuration <= ModelCallTimeout
            || MaxSupersedesPerBurst < 0
            || MaxTurnAttempts < 1
            || MaxInputMessages is < 1 or > 200
            || MaxOutputTokens < 16
            || MaxConcurrentTurnsPerTenant < 1
            || ExecutorBatchSize is < 1 or > 1000
            || ExecutorParallelism is < 1 or > 64
            || ReconcileSessionsPerTenant is < 1 or > 100
            || ReconcileTenantBatchSize is < 1 or > 1000
            || KnowledgeGapMaxPerTenant is < 1 or > 1_000_000)
        {
            throw new InvalidOperationException(
                "A configuração do Agents é inválida (tempos, tentativas, mensagens, tetos, capacidade ou lotes); o tempo do turno precisa passar o da chamada ao modelo.");
        }

        if (LookupMaxPerTurn != 1 || LookupBudget < TimeSpan.FromSeconds(1) || LookupBudget > TimeSpan.FromSeconds(30))
        {
            throw new InvalidOperationException("Agents:Options: LookupMaxPerTurn só pode ser 1 nesta fase e LookupBudget fica entre 1 e 30 segundos.");
        }

        if (IntegrationLookupsEnabled && TurnMaxDuration <= (ModelCallTimeout * 2) + LookupBudget)
        {
            throw new InvalidOperationException("Agents:Options: com a consulta de integração ligada, TurnMaxDuration precisa passar o dobro de ModelCallTimeout mais LookupBudget (duas chamadas ao modelo e a consulta).");
        }

        try
        {
            _ = KnowledgeGapHasher.FromBase64(KnowledgeGapHashKey);
        }
        catch (FormatException)
        {
            // O valor da chave nunca entra na mensagem.
            throw new InvalidOperationException("Agents:Options:KnowledgeGapHashKey deve ser Base64 de pelo menos 32 bytes (ou ficar vazia para desligar o registro de lacunas).");
        }
    }
}
