using IaBimeta.Agents.Domain;
using IaBimeta.Conversations.Contracts;

namespace IaBimeta.Agents.Tests;

/// <summary>
/// A configuração do Agents (CONVERSATION-LIFECYCLE.md §14): os padrões são a referência aprovada, e toda violação falha na partida (nunca no meio do processamento). Cada violação é um caso da lista, e
/// o teste escolhe por índice para o relatório dizer qual falhou.
/// </summary>
public sealed class AgentsOptionsTests
{
    private static readonly (string Nome, Action<AgentsOptions> Quebra)[] Violacoes =
    {
        ("QuietWindow zero", o => o.QuietWindow = TimeSpan.Zero),
        ("QuietWindow negativa", o => o.QuietWindow = TimeSpan.FromSeconds(-1)),
        ("MaxWait menor que QuietWindow", o => o.MaxWait = o.QuietWindow - TimeSpan.FromMilliseconds(1)),
        ("MaxWait zero", o => o.MaxWait = TimeSpan.Zero),
        ("RetryBackoff zero", o => o.RetryBackoff = TimeSpan.Zero),
        ("MaxRetryBackoff menor que RetryBackoff", o => o.MaxRetryBackoff = o.RetryBackoff - TimeSpan.FromMilliseconds(1)),
        ("TurnMaxDuration igual a ModelCallTimeout", o => o.TurnMaxDuration = o.ModelCallTimeout),
        ("TurnMaxDuration menor que ModelCallTimeout", o => o.TurnMaxDuration = o.ModelCallTimeout - TimeSpan.FromSeconds(1)),
        ("TurnMaxDuration zero", o => o.TurnMaxDuration = TimeSpan.Zero),
        ("ModelCallTimeout zero", o => o.ModelCallTimeout = TimeSpan.Zero),
        ("CapacityRetry zero", o => o.CapacityRetry = TimeSpan.Zero),
        ("ContentRecheck zero", o => o.ContentRecheck = TimeSpan.Zero),
        ("MaxTurnAge zero", o => o.MaxTurnAge = TimeSpan.Zero),
        ("ExecutorInterval zero", o => o.ExecutorInterval = TimeSpan.Zero),
        ("ReconcileInterval zero", o => o.ReconcileInterval = TimeSpan.Zero),
        ("AudioExtraWait negativa", o => o.AudioExtraWait = TimeSpan.FromSeconds(-1)),
        ("ImageExtraWait negativa", o => o.ImageExtraWait = TimeSpan.FromSeconds(-1)),
        ("DocumentExtraWait negativa", o => o.DocumentExtraWait = TimeSpan.FromSeconds(-1)),
        ("VideoAndOtherExtraWait negativa", o => o.VideoAndOtherExtraWait = TimeSpan.FromSeconds(-1)),
        ("CleanShutdownRetry negativa", o => o.CleanShutdownRetry = TimeSpan.FromSeconds(-1)),
        ("ReconcileMinimumAge negativa", o => o.ReconcileMinimumAge = TimeSpan.FromSeconds(-1)),
        ("MaxSupersedesPerBurst negativo", o => o.MaxSupersedesPerBurst = -1),
        ("MaxTurnAttempts zero", o => o.MaxTurnAttempts = 0),
        ("MaxTurnAttempts negativo", o => o.MaxTurnAttempts = -3),
        ("MaxInputMessages zero", o => o.MaxInputMessages = 0),
        ("MaxInputMessages acima de 200", o => o.MaxInputMessages = 201),
        ("MaxOutputTokens abaixo de 16", o => o.MaxOutputTokens = 15),
        ("MaxConcurrentTurnsPerTenant zero", o => o.MaxConcurrentTurnsPerTenant = 0),
        ("ExecutorBatchSize zero", o => o.ExecutorBatchSize = 0),
        ("ExecutorBatchSize acima de 1000", o => o.ExecutorBatchSize = 1001),
        ("ExecutorParallelism zero", o => o.ExecutorParallelism = 0),
        ("ExecutorParallelism acima de 64", o => o.ExecutorParallelism = 65),
        ("ReconcileSessionsPerTenant zero", o => o.ReconcileSessionsPerTenant = 0),
        ("ReconcileSessionsPerTenant acima de 100", o => o.ReconcileSessionsPerTenant = 101),
        ("ReconcileTenantBatchSize zero", o => o.ReconcileTenantBatchSize = 0),
        ("ReconcileTenantBatchSize acima de 1000", o => o.ReconcileTenantBatchSize = 1001),
        ("KnowledgeGapMaxPerTenant zero", o => o.KnowledgeGapMaxPerTenant = 0),
        ("KnowledgeGapMaxPerTenant acima de 1000000", o => o.KnowledgeGapMaxPerTenant = 1_000_001),
    };

    private static readonly (string Nome, Action<AgentsOptions> Ajuste)[] Limites =
    {
        ("MaxInputMessages 1", o => o.MaxInputMessages = 1),
        ("MaxInputMessages 200", o => o.MaxInputMessages = 200),
        ("MaxSupersedesPerBurst 0", o => o.MaxSupersedesPerBurst = 0),
        ("MaxTurnAttempts 1", o => o.MaxTurnAttempts = 1),
        ("MaxWait igual a QuietWindow", o => o.MaxWait = o.QuietWindow),
        ("MaxRetryBackoff igual a RetryBackoff", o => o.MaxRetryBackoff = o.RetryBackoff),
        ("MaxOutputTokens 16", o => o.MaxOutputTokens = 16),
        ("esperas adicionais zeradas", o => { o.AudioExtraWait = o.ImageExtraWait = o.DocumentExtraWait = o.VideoAndOtherExtraWait = TimeSpan.Zero; }),
        ("CleanShutdownRetry zero", o => o.CleanShutdownRetry = TimeSpan.Zero),
        ("ReconcileMinimumAge zero", o => o.ReconcileMinimumAge = TimeSpan.Zero),
        ("lotes nos tetos", o => { o.ExecutorBatchSize = 1000; o.ExecutorParallelism = 64; o.ReconcileSessionsPerTenant = 100; o.ReconcileTenantBatchSize = 1000; }),
        ("lotes nos pisos", o => { o.ExecutorBatchSize = 1; o.ExecutorParallelism = 1; o.ReconcileSessionsPerTenant = 1; o.ReconcileTenantBatchSize = 1; o.MaxConcurrentTurnsPerTenant = 1; }),
    };

    public static IEnumerable<object[]> IndicesDeViolacao() => Enumerable.Range(0, Violacoes.Length).Select(i => new object[] { i });

    public static IEnumerable<object[]> IndicesDeLimite() => Enumerable.Range(0, Limites.Length).Select(i => new object[] { i });

    [Fact]
    public void Os_padroes_sao_a_referencia_aprovada_do_CONVERSATION_LIFECYCLE_secao_14()
    {
        var o = new AgentsOptions();

        Assert.Equal(TimeSpan.FromSeconds(3), o.QuietWindow);
        Assert.Equal(TimeSpan.FromSeconds(10), o.MaxWait);
        Assert.Equal(2, o.MaxSupersedesPerBurst);
        Assert.Equal(3, o.MaxTurnAttempts);
    }

    [Fact]
    public void O_padrao_e_valido_e_o_tempo_do_turno_passa_o_da_chamada_ao_modelo()
    {
        var o = new AgentsOptions();

        o.Validate();

        Assert.True(o.TurnMaxDuration > o.ModelCallTimeout);
        Assert.True(o.MaxWait >= o.QuietWindow);
        Assert.True(o.MaxConcurrentTurnsPerTenant >= 1);
    }

    [Theory]
    [MemberData(nameof(IndicesDeViolacao))]
    public void Cada_violacao_e_recusada_na_partida(int indice)
    {
        var (nome, quebra) = Violacoes[indice];
        var opcoes = new AgentsOptions();
        quebra(opcoes);

        var falha = Assert.Throws<InvalidOperationException>(opcoes.Validate);

        Assert.True(falha.Message.Length > 0, nome);
    }

    [Theory]
    [MemberData(nameof(IndicesDeLimite))]
    public void Os_valores_nos_limites_sao_aceitos(int indice)
    {
        var (nome, ajuste) = Limites[indice];
        var opcoes = new AgentsOptions();
        ajuste(opcoes);

        var excecao = Record.Exception(opcoes.Validate);

        Assert.True(excecao is null, $"{nome} deveria ser aceito, mas lançou: {excecao?.Message}");
    }

    [Fact]
    public void A_lista_de_violacoes_cobre_toda_propriedade_de_tempo_e_de_tamanho()
    {
        // Se alguém acrescentar uma propriedade numérica ou de tempo à configuração, este teste obriga a decidir se ela precisa de validação.
        var cobertas = Violacoes.Select(v => v.Nome.Split(' ')[0]).ToHashSet();
        var validadas = new[]
        {
            nameof(AgentsOptions.QuietWindow), nameof(AgentsOptions.MaxWait), nameof(AgentsOptions.RetryBackoff), nameof(AgentsOptions.MaxRetryBackoff),
            nameof(AgentsOptions.TurnMaxDuration), nameof(AgentsOptions.ModelCallTimeout), nameof(AgentsOptions.CapacityRetry), nameof(AgentsOptions.ContentRecheck),
            nameof(AgentsOptions.MaxTurnAge), nameof(AgentsOptions.ExecutorInterval), nameof(AgentsOptions.ReconcileInterval), nameof(AgentsOptions.AudioExtraWait),
            nameof(AgentsOptions.ImageExtraWait), nameof(AgentsOptions.DocumentExtraWait), nameof(AgentsOptions.VideoAndOtherExtraWait), nameof(AgentsOptions.CleanShutdownRetry),
            nameof(AgentsOptions.ReconcileMinimumAge), nameof(AgentsOptions.MaxSupersedesPerBurst), nameof(AgentsOptions.MaxTurnAttempts), nameof(AgentsOptions.MaxInputMessages),
            nameof(AgentsOptions.MaxOutputTokens), nameof(AgentsOptions.MaxConcurrentTurnsPerTenant), nameof(AgentsOptions.ExecutorBatchSize), nameof(AgentsOptions.ExecutorParallelism),
            nameof(AgentsOptions.ReconcileSessionsPerTenant), nameof(AgentsOptions.ReconcileTenantBatchSize), nameof(AgentsOptions.KnowledgeGapMaxPerTenant),
        };
        var propriedadesNumericas = typeof(AgentsOptions).GetProperties()
            .Where(p => p.PropertyType == typeof(TimeSpan) || p.PropertyType == typeof(int))
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(propriedadesNumericas, validadas.OrderBy(n => n, StringComparer.Ordinal));
        Assert.All(validadas, nome => Assert.Contains(nome, cobertas));
    }

    [Theory]
    [InlineData(MessageKind.Audio)]
    [InlineData(MessageKind.Image)]
    [InlineData(MessageKind.Document)]
    [InlineData(MessageKind.Video)]
    [InlineData(MessageKind.Other)]
    [InlineData(MessageKind.Text)]
    public void ExtraWaitFor_devolve_a_espera_do_tipo_e_texto_ou_outros_valem_a_de_video(MessageKind tipo)
    {
        var o = new AgentsOptions
        {
            AudioExtraWait = TimeSpan.FromSeconds(11),
            ImageExtraWait = TimeSpan.FromSeconds(22),
            DocumentExtraWait = TimeSpan.FromSeconds(33),
            VideoAndOtherExtraWait = TimeSpan.FromSeconds(44),
        };

        var esperado = tipo switch
        {
            MessageKind.Audio => TimeSpan.FromSeconds(11),
            MessageKind.Image => TimeSpan.FromSeconds(22),
            MessageKind.Document => TimeSpan.FromSeconds(33),
            _ => TimeSpan.FromSeconds(44),
        };

        Assert.Equal(esperado, o.ExtraWaitFor(tipo));
    }

    [Fact]
    public void Por_padrao_video_e_outros_nao_esperam_e_audio_imagem_e_documento_esperam_10_segundos()
    {
        var o = new AgentsOptions();

        Assert.Equal(TimeSpan.Zero, o.ExtraWaitFor(MessageKind.Video));
        Assert.Equal(TimeSpan.Zero, o.ExtraWaitFor(MessageKind.Other));
        Assert.Equal(TimeSpan.FromSeconds(10), o.ExtraWaitFor(MessageKind.Audio));
        Assert.Equal(TimeSpan.FromSeconds(10), o.ExtraWaitFor(MessageKind.Image));
        Assert.Equal(TimeSpan.FromSeconds(10), o.ExtraWaitFor(MessageKind.Document));
    }
}
