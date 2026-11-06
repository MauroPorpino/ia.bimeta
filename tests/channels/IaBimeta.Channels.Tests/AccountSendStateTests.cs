using IaBimeta.Channels.Application;
using IaBimeta.Channels.Domain;

namespace IaBimeta.Channels.Tests;

/// <summary>
/// O balde de fichas, a espera do provedor e a pausa da conta (SND-2 e SND-6), sem banco: a matemática do balde (reabastecimento contínuo, capacidade, espera exata), a espera imposta pelo provedor, a pausa que
/// não gasta ficha, a retomada gradual e as validações. A persistência (trava da linha, concorrência) é provada nos testes de integração.
/// </summary>
public sealed class AccountSendStateTests
{
    private const int Padrao = 80;
    private const double Rajada = 1d;

    private static readonly DateTimeOffset T0 = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    private static AccountSendState Nova(double fichas = 80, DateTimeOffset? instante = null) =>
        AccountSendState.Create(Fabrica.NovoTenant(), Guid.NewGuid(), fichas, instante ?? T0);

    [Fact]
    public void O_balde_cheio_concede_uma_rajada_do_tamanho_da_capacidade_e_depois_manda_esperar()
    {
        var estado = Nova(fichas: 3);

        // Vazão de 3 por segundo: a capacidade é 3 fichas.
        for (var i = 0; i < 3; i++)
        {
            Assert.True(estado.TryTake(T0, 3, Rajada).Granted);
        }

        var negada = estado.TryTake(T0, 3, Rajada);
        Assert.False(negada.Granted);
        Assert.False(negada.Paused);
        Assert.Equal(TimeSpan.FromMilliseconds(334), negada.Wait); // 1/3 s, arredondado para cima ao milissegundo
    }

    [Fact]
    public void O_balde_reabastece_de_forma_continua_pelo_tempo_decorrido()
    {
        var estado = Nova(fichas: 10);
        for (var i = 0; i < 10; i++)
        {
            Assert.True(estado.TryTake(T0, 10, Rajada).Granted);
        }

        Assert.False(estado.TryTake(T0, 10, Rajada).Granted);

        // 100 ms a 10 por segundo = 1 ficha.
        Assert.True(estado.TryTake(T0.AddMilliseconds(100), 10, Rajada).Granted);
        Assert.False(estado.TryTake(T0.AddMilliseconds(100), 10, Rajada).Granted);

        // 250 ms depois: 2,5 fichas, duas concedidas e a fração sobra.
        Assert.True(estado.TryTake(T0.AddMilliseconds(350), 10, Rajada).Granted);
        Assert.True(estado.TryTake(T0.AddMilliseconds(350), 10, Rajada).Granted);
        Assert.False(estado.TryTake(T0.AddMilliseconds(350), 10, Rajada).Granted);
    }

    [Fact]
    public void O_balde_nunca_passa_da_capacidade_mesmo_depois_de_muito_tempo_parado()
    {
        var estado = Nova(fichas: 5);

        // Uma hora parado: continua sendo 5 fichas, não 18.000.
        var concedidas = 0;
        while (estado.TryTake(T0.AddHours(1), 5, Rajada).Granted)
        {
            concedidas++;
            Assert.True(concedidas <= 5, "o balde passou da capacidade");
        }

        Assert.Equal(5, concedidas);
    }

    [Fact]
    public void A_espera_calculada_e_exata_a_fracao_que_falta_para_uma_ficha_inteira()
    {
        var estado = Nova(fichas: 1);
        Assert.True(estado.TryTake(T0, 1, Rajada).Granted);

        var espera = estado.TryTake(T0.AddMilliseconds(250), 1, Rajada);

        Assert.False(espera.Granted);
        Assert.Equal(TimeSpan.FromMilliseconds(750), espera.Wait);
        Assert.True(estado.TryTake(T0.AddMilliseconds(250) + espera.Wait, 1, Rajada).Granted);
    }

    [Fact]
    public void A_rajada_em_segundos_aumenta_a_capacidade_e_ha_sempre_ao_menos_uma_ficha()
    {
        Assert.Equal(160d, AccountSendState.Capacity(80, 2d));
        Assert.Equal(1d, AccountSendState.Capacity(1, 0.1d));
        Assert.Equal(80d, AccountSendState.Capacity(80, 1d));
    }

    [Fact]
    public void Um_relogio_que_volta_no_tempo_nao_regride_o_balde_nem_cria_fichas()
    {
        var estado = Nova(fichas: 2);
        Assert.True(estado.TryTake(T0.AddSeconds(10), 2, Rajada).Granted);
        Assert.True(estado.TryTake(T0.AddSeconds(10), 2, Rajada).Granted);
        Assert.False(estado.TryTake(T0.AddSeconds(10), 2, Rajada).Granted);

        // O instante é ANTERIOR ao último reabastecimento: nenhuma ficha nova.
        Assert.False(estado.TryTake(T0, 2, Rajada).Granted);
        Assert.Equal(T0.AddSeconds(10), estado.RefilledAt);
    }

    [Fact]
    public void A_vazao_propria_da_conta_vale_no_lugar_da_padrao_e_ao_limpa_la_volta_a_padrao()
    {
        var estado = Nova();
        Assert.Equal(Padrao, estado.EffectiveThroughput(Padrao));

        estado.SetThroughput(20, T0);
        Assert.Equal(20, estado.EffectiveThroughput(Padrao));

        estado.SetThroughput(null, T0);
        Assert.Equal(Padrao, estado.EffectiveThroughput(Padrao));
    }

    [Fact]
    public void Baixar_a_vazao_limita_o_balde_a_nova_capacidade_na_proxima_ficha()
    {
        var estado = Nova(fichas: 80);
        estado.SetThroughput(2, T0);

        var concedidas = 0;
        while (estado.TryTake(T0, Padrao, Rajada).Granted)
        {
            concedidas++;
        }

        Assert.Equal(2, concedidas);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(1001)]
    public void Vazao_fora_de_1_a_1000_e_recusada(int valor)
    {
        var estado = Nova();

        Assert.Throws<ChannelsValidationException>(() => estado.SetThroughput(valor, T0));
        Assert.Null(estado.ThroughputPerSecond);
    }

    // A espera imposta pelo provedor

    [Fact]
    public void Dentro_da_espera_imposta_pelo_provedor_nenhuma_ficha_e_concedida_mesmo_com_o_balde_cheio()
    {
        var estado = Nova(fichas: 80);
        estado.ApplyCooldown(T0.AddSeconds(30), T0);

        var durante = estado.TryTake(T0.AddSeconds(10), Padrao, Rajada);
        Assert.False(durante.Granted);
        Assert.False(durante.Paused);
        Assert.Equal(TimeSpan.FromSeconds(20), durante.Wait);

        // Terminada a espera, volta a conceder.
        Assert.True(estado.TryTake(T0.AddSeconds(30), Padrao, Rajada).Granted);
        Assert.Null(estado.CooldownUntil);
    }

    [Fact]
    public void A_espera_so_estende_e_nunca_encurta_uma_espera_em_curso()
    {
        var estado = Nova();
        estado.ApplyCooldown(T0.AddSeconds(60), T0);
        estado.ApplyCooldown(T0.AddSeconds(5), T0);
        Assert.Equal(T0.AddSeconds(60), estado.CooldownUntil);

        estado.ApplyCooldown(T0.AddSeconds(90), T0);
        Assert.Equal(T0.AddSeconds(90), estado.CooldownUntil);
    }

    // A pausa

    [Fact]
    public void A_conta_pausada_nao_concede_nem_gasta_ficha()
    {
        var estado = Nova(fichas: 5);
        Assert.True(estado.Pause("account-error.190", T0));
        var tokensAntes = estado.Tokens;

        for (var i = 0; i < 20; i++)
        {
            var resultado = estado.TryTake(T0.AddSeconds(i), Padrao, Rajada);
            Assert.True(resultado.Paused);
            Assert.False(resultado.Granted);
        }

        Assert.Equal(tokensAntes, estado.Tokens);
    }

    [Fact]
    public void Pausar_de_novo_nao_muda_nada_e_vale_o_motivo_da_primeira_pausa()
    {
        var estado = Nova();

        Assert.True(estado.Pause("account-error.190", T0));
        Assert.False(estado.Pause("manual", T0.AddMinutes(5)));

        Assert.Equal("account-error.190", estado.PauseReason);
        Assert.Equal(T0, estado.PausedAt);
    }

    [Theory]
    [InlineData("Account Error:190!", "account-error-190-")]
    [InlineData("  MANUAL ", "manual")]
    [InlineData("", "unspecified")]
    [InlineData(null, "unspecified")]
    public void O_motivo_da_pausa_e_um_codigo_curto_e_seguro(string? bruto, string esperado)
    {
        var estado = Nova();

        estado.Pause(bruto!, T0);

        Assert.Equal(esperado, estado.PauseReason);
    }

    [Fact]
    public void O_motivo_da_pausa_e_limitado_ao_tamanho_da_coluna()
    {
        var estado = Nova();

        estado.Pause(new string('a', 500), T0);

        Assert.Equal(AccountSendState.PauseReasonMaxLength, estado.PauseReason!.Length);
    }

    [Fact]
    public void A_retomada_limpa_pausa_e_espera_e_recomeca_com_uma_so_ficha()
    {
        var estado = Nova(fichas: 80);
        estado.Pause("account-error.190", T0);
        estado.ApplyCooldown(T0.AddMinutes(10), T0);
        var retomada = T0.AddMinutes(2);

        Assert.True(estado.Resume(retomada));

        Assert.False(estado.IsPaused);
        Assert.Null(estado.PauseReason);
        Assert.Null(estado.CooldownUntil);
        Assert.Equal(retomada, estado.ResumedAt);
        Assert.Equal(T0, estado.PausedAt);

        // Gradual: no mesmo instante só há uma ficha; a vazão volta com o tempo, não em rajada.
        Assert.True(estado.TryTake(retomada, Padrao, Rajada).Granted);
        Assert.False(estado.TryTake(retomada, Padrao, Rajada).Granted);
    }

    [Fact]
    public void Retomar_uma_conta_que_nao_esta_pausada_nao_muda_nada()
    {
        var estado = Nova(fichas: 80);

        Assert.False(estado.Resume(T0.AddMinutes(1)));

        Assert.Null(estado.ResumedAt);
        Assert.Equal(80d, estado.Tokens);
    }

    // As opções

    [Fact]
    public void As_opcoes_padrao_sao_as_da_documentacao_e_validas()
    {
        var opcoes = new ChannelSendingOptions();

        opcoes.Validate();

        Assert.Equal(80, opcoes.DefaultThroughputPerSecond);
        Assert.Equal(TimeSpan.FromSeconds(5), opcoes.DefaultRateLimitCooldown);
    }

    [Theory]
    [InlineData(0, 1d, 20, 5, 300)]
    [InlineData(1001, 1d, 20, 5, 300)]
    [InlineData(80, 0d, 20, 5, 300)]
    [InlineData(80, 61d, 20, 5, 300)]
    [InlineData(80, 1d, 0, 5, 300)]
    [InlineData(80, 1d, 61, 5, 300)]
    [InlineData(80, 1d, 20, 0, 300)]
    [InlineData(80, 1d, 20, 10, 5)]
    public void Opcoes_invalidas_falham_na_partida(int vazao, double rajada, int esperaLocal, int cooldown, int teto)
    {
        var opcoes = new ChannelSendingOptions
        {
            DefaultThroughputPerSecond = vazao,
            BurstSeconds = rajada,
            MaxLocalWait = TimeSpan.FromSeconds(esperaLocal),
            DefaultRateLimitCooldown = TimeSpan.FromSeconds(cooldown),
            MaxRateLimitCooldown = TimeSpan.FromSeconds(teto),
        };

        Assert.Throws<InvalidOperationException>(opcoes.Validate);
    }

    [Fact]
    public void A_espera_imposta_e_a_do_provedor_ou_a_padrao_e_sempre_limitada_ao_teto()
    {
        var opcoes = new ChannelSendingOptions { DefaultRateLimitCooldown = TimeSpan.FromSeconds(5), MaxRateLimitCooldown = TimeSpan.FromMinutes(1) };

        Assert.Equal(TimeSpan.FromSeconds(17), opcoes.CooldownFor(TimeSpan.FromSeconds(17)));
        Assert.Equal(TimeSpan.FromSeconds(5), opcoes.CooldownFor(null));
        Assert.Equal(TimeSpan.FromSeconds(5), opcoes.CooldownFor(TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromSeconds(5), opcoes.CooldownFor(TimeSpan.FromSeconds(-3)));
        Assert.Equal(TimeSpan.FromMinutes(1), opcoes.CooldownFor(TimeSpan.FromHours(6)));
    }

    [Theory]
    [InlineData("https://atendimento.exemplo.com.br", true)]
    [InlineData("https://atendimento.exemplo.com.br/", true)]
    [InlineData("  https://api.exemplo.com.br:8443  ", true)]
    [InlineData("http://atendimento.exemplo.com.br", false)]
    [InlineData("https://localhost:5155", false)]
    [InlineData("https://127.0.0.1", false)]
    [InlineData("https://app.localhost", false)]
    [InlineData("https://maquina.local", false)]
    [InlineData("https://usuario:senha@atendimento.exemplo.com.br", false)]
    [InlineData("https://atendimento.exemplo.com.br/?x=1", false)]
    [InlineData("https://atendimento.exemplo.com.br/#a", false)]
    [InlineData("atendimento.exemplo.com.br", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void O_endereco_publico_do_webhook_precisa_ser_HTTPS_absoluto_e_nao_local(string? url, bool valido)
    {
        var opcoes = new ChannelWebhookOptions { PublicBaseUrl = url };

        Assert.Equal(valido, opcoes.IsPublicHttps(out var endereco));
        Assert.Equal(valido, endereco is not null);
    }
}

/// <summary>
/// A distinção que protege o <c>SendUnknown</c> (EFX-3): só um despacho cuja CHAMADA ao provedor começou (<c>Dispatching</c> com tentativas) pode ter chegado à Meta. A intenção sozinha (zero tentativas) nunca
/// chamou o provedor: pode esperar (vazão), seguir ou falhar com certeza, e nunca vira "resultado incerto".
/// </summary>
public sealed class OutboundMessageProviderReachTests
{
    private static OutboundMessage Intencao() =>
        OutboundMessage.Intend(Guid.NewGuid(), Fabrica.NovoTenant(), Guid.NewGuid(), IaBimeta.SharedKernel.ChannelInstanceId.New(), new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));

    private static readonly DateTimeOffset Agora = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void So_a_intencao_nao_chamou_o_provedor_e_nao_e_incerta()
    {
        var despacho = Intencao();

        Assert.Equal((OutboundState.Dispatching, 0), (despacho.State, despacho.Attempts));
        Assert.False(despacho.MayHaveReachedProvider);
        Assert.False(despacho.IsResolved);
    }

    [Fact]
    public void Depois_de_comecar_a_tentativa_a_chamada_pode_ter_chegado_ao_provedor()
    {
        var despacho = Intencao();

        despacho.BeginAttempt(Agora);

        Assert.True(despacho.MayHaveReachedProvider);
    }

    [Fact]
    public void A_recusa_transitoria_com_prova_de_que_nada_saiu_nao_e_incerta_ate_a_proxima_tentativa_comecar()
    {
        var despacho = Intencao();
        despacho.BeginAttempt(Agora);

        despacho.MarkRetry("130429", Agora);
        Assert.False(despacho.MayHaveReachedProvider);

        despacho.BeginAttempt(Agora.AddSeconds(5));
        Assert.True(despacho.MayHaveReachedProvider);
        Assert.Equal(2, despacho.Attempts);
    }

    [Theory]
    [InlineData("sent")]
    [InlineData("failed")]
    [InlineData("unknown")]
    public void Desfecho_gravado_nunca_e_tratado_como_chamada_em_curso(string desfecho)
    {
        var despacho = Intencao();
        despacho.BeginAttempt(Agora);

        switch (desfecho)
        {
            case "sent":
                despacho.MarkSent("wamid.X", Agora);
                break;
            case "failed":
                despacho.MarkFailed("relay-dead", Agora);
                break;
            default:
                despacho.MarkSendUnknown("interrupted", Agora);
                break;
        }

        Assert.False(despacho.MayHaveReachedProvider);
        Assert.True(despacho.IsResolved);
    }
}
