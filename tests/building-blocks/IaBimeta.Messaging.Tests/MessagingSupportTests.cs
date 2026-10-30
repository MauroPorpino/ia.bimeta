using IaBimeta.Conversations.Contracts;
using IaBimeta.Conversations.Contracts.Events;
using IaBimeta.Messaging;
using IaBimeta.SharedKernel;

namespace IaBimeta.Messaging.Tests;

/// <summary>A mecânica pura do Messaging: espera entre tentativas, opções, JSON do payload e os resultados do handler.</summary>
public sealed class MessagingSupportTests
{
    private static readonly MessagingOptions Opcoes = new()
    {
        BaseBackoff = TimeSpan.FromSeconds(10),
        MaxBackoff = TimeSpan.FromMinutes(5),
    };

    [Theory]
    [InlineData(1, 0.0, 5)]
    [InlineData(1, 1.0, 10)]
    [InlineData(2, 1.0, 20)]
    [InlineData(3, 1.0, 40)]
    [InlineData(4, 0.5, 60)]   // 80 s * 0,75
    [InlineData(5, 1.0, 160)]
    public void A_espera_dobra_a_cada_tentativa_e_o_jitter_vai_de_cinquenta_a_cem_por_cento(int tentativa, double jitter, double segundosEsperados)
    {
        Assert.Equal(segundosEsperados, Backoff.For(tentativa, Opcoes, jitter).TotalSeconds, precision: 3);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(20)]
    [InlineData(1000)]
    [InlineData(int.MaxValue)]
    public void A_espera_nunca_passa_do_teto_nem_estoura_com_muitas_tentativas(int tentativa)
    {
        var espera = Backoff.For(tentativa, Opcoes, 1.0);

        Assert.Equal(Opcoes.MaxBackoff, espera);
        Assert.True(Backoff.For(tentativa, Opcoes, 0.0) >= TimeSpan.FromMinutes(2.5));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Tentativa_zero_ou_negativa_vale_como_a_primeira(int tentativa)
    {
        Assert.Equal(Backoff.For(1, Opcoes, 0.3), Backoff.For(tentativa, Opcoes, 0.3));
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(2.0)]
    public void O_jitter_fora_de_zero_a_um_e_limitado_e_a_espera_fica_dentro_da_faixa(double jitter)
    {
        var espera = Backoff.For(1, Opcoes, jitter);

        Assert.InRange(espera, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void As_opcoes_padrao_sao_validas()
    {
        new MessagingOptions().Validate();
    }

    [Theory]
    [InlineData("MaxAttempts", 0)]
    [InlineData("BatchSize", 0)]
    [InlineData("BatchSize", 1001)]
    public void Opcoes_numericas_invalidas_falham_na_partida(string campo, int valor)
    {
        var opcoes = new MessagingOptions();
        typeof(MessagingOptions).GetProperty(campo)!.SetValue(opcoes, valor);

        Assert.Throws<InvalidOperationException>(opcoes.Validate);
    }

    [Fact]
    public void Opcoes_de_tempo_invalidas_falham_na_partida()
    {
        Assert.Throws<InvalidOperationException>(() => new MessagingOptions { LeaseDuration = TimeSpan.Zero }.Validate());
        Assert.Throws<InvalidOperationException>(() => new MessagingOptions { BaseBackoff = TimeSpan.Zero }.Validate());
        Assert.Throws<InvalidOperationException>(() => new MessagingOptions { BaseBackoff = TimeSpan.FromMinutes(10), MaxBackoff = TimeSpan.FromMinutes(1) }.Validate());
        Assert.Throws<InvalidOperationException>(() => new MessagingOptions { PollInterval = TimeSpan.FromSeconds(-1) }.Validate());
        Assert.Throws<InvalidOperationException>(() => new MessagingOptions { ProcessedRetention = TimeSpan.Zero }.Validate());
    }

    [Fact]
    public void O_payload_de_um_contrato_real_vai_e_volta_pelo_JSON_com_enums_em_texto_e_o_canal_como_identificador()
    {
        var canal = ChannelInstanceId.New();
        var original = new InboundMessageRegisteredV1(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 7, MessageKind.Audio, ContentState.Pending, true, canal);

        var json = MessagingJson.Serialize(original);
        var volta = (InboundMessageRegisteredV1)MessagingJson.Deserialize(json, typeof(InboundMessageRegisteredV1));

        Assert.Equal(original, volta);
        Assert.Contains("\"kind\":\"Audio\"", json);
        Assert.Contains("\"contentState\":\"Pending\"", json);
        Assert.Contains($"\"channelInstanceId\":\"{canal.Value}\"", json);
    }

    [Fact]
    public void O_contrato_do_modo_de_atendimento_vai_e_volta_com_o_responsavel_nulo()
    {
        var original = new HandlingModeChangedV1(Guid.NewGuid(), HandlingMode.AiAutonomous, HandlingMode.HumanAssisted, null, 2, Guid.NewGuid());

        var volta = (HandlingModeChangedV1)MessagingJson.Deserialize(MessagingJson.Serialize(original), typeof(HandlingModeChangedV1));

        Assert.Equal(original, volta);
    }

    [Fact]
    public void T13_os_campos_de_identidade_dos_eventos_de_atendimento_vao_e_voltam()
    {
        var transicao = new HandlingModeChangedV1(Guid.NewGuid(), HandlingMode.HumanAssisted, HandlingMode.AiAutonomous, null, 9, Guid.NewGuid(), 4, HandlingPeriod: 3, PlacementEpoch: 2);
        var encaminhamento = new SessionTransferredV1(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), null, 9, 4, FromPlacementEpoch: 2, ToPlacementEpoch: 3);

        Assert.Equal(transicao, (HandlingModeChangedV1)MessagingJson.Deserialize(MessagingJson.Serialize(transicao), typeof(HandlingModeChangedV1)));
        Assert.Equal(encaminhamento, (SessionTransferredV1)MessagingJson.Deserialize(MessagingJson.Serialize(encaminhamento), typeof(SessionTransferredV1)));
    }

    [Fact]
    public void T13_o_evento_no_formato_antigo_sem_os_campos_de_identidade_le_com_a_identidade_desconhecida_e_nunca_inventada()
    {
        // O JSON como o Conversations o gravava ANTES dos campos de identidade (eventos que ainda estão no outbox ou no inbox na hora da mudança).
        var sessao = Guid.NewGuid();
        var transicaoId = Guid.NewGuid();
        var transicaoAntiga = $$"""{"sessionId":"{{sessao}}","from":"HumanAssisted","to":"AiAutonomous","responderRef":null,"stateVersion":9,"transitionId":"{{transicaoId}}","messageSeq":4}""";
        var transferenciaAntiga =
            $$"""{"sessionId":"{{sessao}}","transferId":"{{transicaoId}}","fromBusinessUnitId":null,"fromDepartmentId":null,"toBusinessUnitId":"{{sessao}}","toDepartmentId":null,"stateVersion":9,"messageSeq":4}""";

        var transicao = (HandlingModeChangedV1)MessagingJson.Deserialize(transicaoAntiga, typeof(HandlingModeChangedV1));
        Assert.Equal((4L, null, null), (transicao.MessageSeq, transicao.HandlingPeriod, transicao.PlacementEpoch));

        var transferencia = (SessionTransferredV1)MessagingJson.Deserialize(transferenciaAntiga, typeof(SessionTransferredV1));
        Assert.Equal((4L, null, null), (transferencia.MessageSeq, transferencia.FromPlacementEpoch, transferencia.ToPlacementEpoch));
    }

    [Fact]
    public void Payload_nulo_ou_ilegivel_e_recusado_na_leitura_e_nulo_na_escrita()
    {
        Assert.Throws<ArgumentNullException>(() => MessagingJson.Serialize(null!));
        Assert.Throws<System.Text.Json.JsonException>(() => MessagingJson.Deserialize("null", typeof(HandlingModeChangedV1)));
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => MessagingJson.Deserialize("{\"sessionId\": \"nao-e-guid\"}", typeof(HandlingModeChangedV1)));
    }

    [Fact]
    public void Os_resultados_do_handler_carregam_o_motivo_e_a_espera()
    {
        Assert.Equal(HandlerOutcome.Success, HandlerResult.Success.Outcome);
        var repetir = HandlerResult.Retry(TimeSpan.FromSeconds(30), "fora-do-ar");
        Assert.Equal((HandlerOutcome.Retry, TimeSpan.FromSeconds(30), "fora-do-ar"), (repetir.Outcome, repetir.RetryAfter, repetir.Reason));
        var morto = HandlerResult.Dead("regra");
        Assert.Equal((HandlerOutcome.Dead, (TimeSpan?)null, "regra"), (morto.Outcome, morto.RetryAfter, morto.Reason));
    }

    private sealed record Exemplo(string Valor);

    private sealed class ManipuladorDeExemplo : EventHandlerBase<Exemplo>
    {
        public override string EventType => "teste.exemplo";

        public override int EventVersion => 3;

        protected override Task<HandlerResult> HandleAsync(EventEnvelope envelope, Exemplo payload, CancellationToken cancellationToken) =>
            Task.FromResult(payload.Valor == "ok" ? HandlerResult.Success : HandlerResult.Retry());
    }

    private static EventEnvelope Envelope(object payload) =>
        new(Guid.NewGuid(), "teste.exemplo", 3, new TenantId(Guid.NewGuid()), Guid.NewGuid(), null, "chave", DateTimeOffset.UtcNow, payload);

    [Fact]
    public async Task O_manipulador_base_entrega_o_payload_no_tipo_certo_e_recusa_o_tipo_errado_como_falha_permanente()
    {
        IEventHandler manipulador = new ManipuladorDeExemplo();

        Assert.Equal((typeof(Exemplo), "teste.exemplo", 3), (manipulador.PayloadType, manipulador.EventType, manipulador.EventVersion));
        Assert.Equal(HandlerOutcome.Success, (await manipulador.HandleAsync(Envelope(new Exemplo("ok")), default)).Outcome);
        Assert.Equal(HandlerOutcome.Retry, (await manipulador.HandleAsync(Envelope(new Exemplo("x")), default)).Outcome);
        var errado = await manipulador.HandleAsync(Envelope("texto solto"), default);
        Assert.Equal((HandlerOutcome.Dead, "payload-type-mismatch"), (errado.Outcome, errado.Reason));
        await Assert.ThrowsAsync<ArgumentNullException>(() => manipulador.HandleAsync(null!, default));
    }

    [Fact]
    public void Os_contratos_de_tabela_do_outbox_seguem_o_padrao_do_documento()
    {
        // BOUNDARIES.md §12.5: as colunas padrão e os estados são parte do contrato do Messaging.
        Assert.Equal(new[] { 1, 2, 3, 4 }, Enum.GetValues<OutboxState>().Select(e => (int)e).OrderBy(x => x).ToArray());
        var propriedades = typeof(OutboxMessage).GetProperties().Select(p => p.Name).ToHashSet();
        foreach (var coluna in new[]
        {
            "Id", "TenantId", "OrderingKey", "IngestSeq", "State", "Attempts", "NextAttemptAt", "LeaseOwner", "LeaseToken", "LeaseExpiresAt",
            "Payload", "LastError", "DeadReason", "CreatedAt",
        })
        {
            Assert.Contains(coluna, propriedades);
        }
    }
}
