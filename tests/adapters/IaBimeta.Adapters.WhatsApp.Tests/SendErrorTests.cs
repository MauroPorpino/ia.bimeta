using System.Net;
using System.Text;
using IaBimeta.Adapters.WhatsApp.Testing;
using IaBimeta.Channels.Contracts.Providers;

namespace IaBimeta.Adapters.WhatsApp;

/// <summary>
/// O mapeamento dos ERROS do envio (ADR 0011, A.2, itens 6 a 10, e E.4): cada código do provedor leva ao desfecho certo, o código vem só de <c>error.code</c>, o que deixa dúvida sobre o efeito é <c>Unknown</c> (nunca
/// reenviado), o cancelamento do chamador propaga, e nenhum texto do cliente, token ou corpo do provedor vai a resultado. HTTP simulado: nenhuma rede e nenhum token real.
/// </summary>
public sealed class SendErrorTests
{
    private static async Task<ProviderSendResult> Send(FakeHandler handler, WhatsAppCloudAdapter adaptador, HttpStatusCode status, string corpo, params (string, string)[] cabecalhos)
    {
        handler.Returns(status, corpo, cabecalhos);
        return await adaptador.SendTextAsync(Sends.Request(), default);
    }

    // Retentativa com espera

    [Theory]
    [InlineData(130429, HttpStatusCode.TooManyRequests, ProviderRateLimitScope.Account)]
    [InlineData(130429, HttpStatusCode.BadRequest, ProviderRateLimitScope.Account)]
    [InlineData(80007, HttpStatusCode.TooManyRequests, ProviderRateLimitScope.Account)]
    [InlineData(80007, HttpStatusCode.BadRequest, ProviderRateLimitScope.Account)]
    [InlineData(4, HttpStatusCode.TooManyRequests, ProviderRateLimitScope.Account)]
    [InlineData(4, HttpStatusCode.BadRequest, ProviderRateLimitScope.Account)]
    [InlineData(131056, HttpStatusCode.BadRequest, ProviderRateLimitScope.Recipient)]
    [InlineData(131056, HttpStatusCode.TooManyRequests, ProviderRateLimitScope.Recipient)]
    public async Task Limite_de_taxa_e_recusa_transitoria_com_o_codigo_e_o_alcance_do_limite(int codigo, HttpStatusCode http, ProviderRateLimitScope alcance)
    {
        var (adaptador, handler, _) = Sends.Adapter();

        var resultado = await Send(handler, adaptador, http, SendBodies.Error(codigo));

        Assert.Equal(new ProviderSendResult(ProviderSendOutcome.RejectedTransient, null, codigo.ToString(), null, alcance), resultado);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Falha_de_envio_desconhecida_131000_e_transitoria_sem_prova_de_limite_de_taxa(HttpStatusCode http)
    {
        var (adaptador, handler, _) = Sends.Adapter();

        var resultado = await Send(handler, adaptador, http, SendBodies.Error(131000));

        Assert.Equal(new ProviderSendResult(ProviderSendOutcome.RejectedTransient, null, "131000"), resultado);
        Assert.Null(resultado.RateLimitScope);
    }

    [Fact]
    public async Task Limite_de_taxa_da_conta_leva_a_espera_sugerida_pelo_provedor_junto_do_alcance()
    {
        var (adaptador, handler, _) = Sends.Adapter();

        var resultado = await Send(handler, adaptador, HttpStatusCode.TooManyRequests, SendBodies.Error(80007), ("Retry-After", "42"));

        Assert.Equal(new ProviderSendResult(ProviderSendOutcome.RejectedTransient, null, "80007", TimeSpan.FromSeconds(42), ProviderRateLimitScope.Account), resultado);
    }

    [Fact]
    public async Task HTTP_429_com_corpo_de_erro_completo_e_codigo_desconhecido_e_limite_de_taxa_da_conta()
    {
        var (adaptador, handler, _) = Sends.Adapter();

        var resultado = await Send(handler, adaptador, HttpStatusCode.TooManyRequests, SendBodies.Error(99999), ("Retry-After", "9"));

        Assert.Equal(new ProviderSendResult(ProviderSendOutcome.RejectedTransient, null, "99999", TimeSpan.FromSeconds(9), ProviderRateLimitScope.Account), resultado);
    }

    [Theory]
    [InlineData("<html>Too many requests</html>", "http-429")]
    [InlineData("", "http-429")]
    [InlineData("{}", "http-429")]
    public async Task HTTP_429_sem_corpo_de_erro_completo_nao_prova_nada_e_fica_desconhecido(string corpo, string codigo)
    {
        var (adaptador, handler, _) = Sends.Adapter();

        var resultado = await Send(handler, adaptador, HttpStatusCode.TooManyRequests, corpo);

        Assert.Equal(new ProviderSendResult(ProviderSendOutcome.Unknown, null, codigo), resultado);
    }

    [Fact]
    public async Task A_espera_sugerida_pelo_provedor_em_segundos_vai_em_RetryAfter()
    {
        var (adaptador, handler, _) = Sends.Adapter();

        var resultado = await Send(handler, adaptador, HttpStatusCode.TooManyRequests, SendBodies.Error(130429), ("Retry-After", "17"));

        Assert.Equal(ProviderSendOutcome.RejectedTransient, resultado.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(17), resultado.RetryAfter);
    }

    [Fact]
    public async Task A_espera_sugerida_em_data_e_calculada_pelo_relogio_e_nunca_e_negativa()
    {
        var agora = new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
        var (adaptador, handler, _) = Sends.Adapter(clock: new FixedClock(agora));

        var futuro = await Send(handler, adaptador, HttpStatusCode.BadRequest, SendBodies.Error(131056), ("Retry-After", agora.AddSeconds(30).ToString("R")));
        var passado = await Send(handler, adaptador, HttpStatusCode.BadRequest, SendBodies.Error(131056), ("Retry-After", agora.AddSeconds(-30).ToString("R")));

        Assert.Equal(TimeSpan.FromSeconds(30), futuro.RetryAfter);
        Assert.Equal(TimeSpan.Zero, passado.RetryAfter);
    }

    [Fact]
    public async Task Sem_cabecalho_de_espera_RetryAfter_e_nulo_e_a_espera_so_acompanha_a_recusa_transitoria()
    {
        var (adaptador, handler, _) = Sends.Adapter();

        var semEspera = await Send(handler, adaptador, HttpStatusCode.BadRequest, SendBodies.Error(130429));
        var conta = await Send(handler, adaptador, HttpStatusCode.BadRequest, SendBodies.Error(190), ("Retry-After", "60"));
        var janela = await Send(handler, adaptador, HttpStatusCode.BadRequest, SendBodies.Error(131047), ("Retry-After", "60"));

        Assert.Null(semEspera.RetryAfter);
        Assert.Null(conta.RetryAfter);
        Assert.Null(janela.RetryAfter);
    }

    // Janela fechada, falhas definitivas e erros de conta

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task Janela_de_24_horas_fechada_e_WindowClosed(HttpStatusCode http)
    {
        var (adaptador, handler, _) = Sends.Adapter();

        var resultado = await Send(handler, adaptador, http, SendBodies.Error(131047));

        Assert.Equal(new ProviderSendResult(ProviderSendOutcome.WindowClosed, null, "131047"), resultado);
    }

    [Theory]
    [InlineData(131049)]
    [InlineData(131026)]
    [InlineData(131048)]
    public async Task Entrega_recusada_pelo_provedor_e_falha_definitiva_da_mensagem(int codigo)
    {
        var (adaptador, handler, _) = Sends.Adapter();

        var resultado = await Send(handler, adaptador, HttpStatusCode.BadRequest, SendBodies.Error(codigo));

        Assert.Equal(new ProviderSendResult(ProviderSendOutcome.RejectedPermanent, null, codigo.ToString()), resultado);
    }

    [Theory]
    [InlineData(190, HttpStatusCode.BadRequest)]
    [InlineData(190, HttpStatusCode.Unauthorized)]
    [InlineData(368, HttpStatusCode.BadRequest)]
    [InlineData(368, HttpStatusCode.Forbidden)]
    [InlineData(0, HttpStatusCode.Unauthorized)]
    [InlineData(3, HttpStatusCode.BadRequest)]
    [InlineData(10, HttpStatusCode.BadRequest)]
    public async Task Token_expirado_conta_restrita_e_permissao_ausente_sao_erro_da_conta(int codigo, HttpStatusCode http)
    {
        var (adaptador, handler, _) = Sends.Adapter();

        var resultado = await Send(handler, adaptador, http, SendBodies.Error(codigo));

        Assert.Equal(new ProviderSendResult(ProviderSendOutcome.AccountError, null, codigo.ToString()), resultado);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "http-401")]
    [InlineData(HttpStatusCode.Forbidden, "http-403")]
    public async Task Estado_401_ou_403_sem_codigo_conhecido_e_erro_da_conta_mesmo_com_corpo_ilegivel(HttpStatusCode http, string codigo)
    {
        var (adaptador, handler, _) = Sends.Adapter();

        Assert.Equal(new ProviderSendResult(ProviderSendOutcome.AccountError, null, codigo), await Send(handler, adaptador, http, "<html>Acesso negado</html>"));
        Assert.Equal(new ProviderSendResult(ProviderSendOutcome.AccountError, null, codigo), await Send(handler, adaptador, http, string.Empty));
        Assert.Equal(ProviderSendOutcome.AccountError, (await Send(handler, adaptador, http, SendBodies.Error(99999))).Outcome);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(131008)]
    [InlineData(131009)]
    [InlineData(132000)]
    [InlineData(133010)]
    [InlineData(1)]
    public async Task Demais_4xx_de_validacao_com_corpo_de_erro_completo_sao_recusa_definitiva(int codigo)
    {
        var (adaptador, handler, _) = Sends.Adapter();

        var resultado = await Send(handler, adaptador, HttpStatusCode.BadRequest, SendBodies.Error(codigo));

        Assert.Equal(new ProviderSendResult(ProviderSendOutcome.RejectedPermanent, null, codigo.ToString()), resultado);
    }

    [Fact]
    public async Task Erro_4xx_sem_codigo_mas_com_corpo_de_erro_completo_e_recusa_definitiva_com_o_estado_como_codigo()
    {
        var (adaptador, handler, _) = Sends.Adapter();

        var resultado = await Send(handler, adaptador, HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"algo\",\"type\":\"GraphMethodException\"}}");

        Assert.Equal(new ProviderSendResult(ProviderSendOutcome.RejectedPermanent, null, "http-400"), resultado);
    }

    [Fact]
    public async Task O_codigo_como_texto_de_digitos_tambem_vale()
    {
        var (adaptador, handler, _) = Sends.Adapter();

        var resultado = await Send(handler, adaptador, HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"x\",\"code\":\"131047\"}}");

        Assert.Equal(new ProviderSendResult(ProviderSendOutcome.WindowClosed, null, "131047"), resultado);
    }

    // O código vem só de error.code

    [Fact]
    public async Task O_desfecho_vem_so_de_error_code_nunca_do_texto_da_mensagem_ou_dos_detalhes()
    {
        var (adaptador, handler, _) = Sends.Adapter();

        // A mensagem diz "(#131047)" e os detalhes dizem "190", mas o código é 131049: vale o código.
        var resultado = await Send(handler, adaptador, HttpStatusCode.BadRequest, SendBodies.Error(131049, "(#131047) Re-engagement message", "token expired 190"));
        Assert.Equal(new ProviderSendResult(ProviderSendOutcome.RejectedPermanent, null, "131049"), resultado);

        // Sem error.code, a mensagem não classifica nada: 400 de corpo completo é recusa definitiva genérica.
        var semCodigo = await Send(handler, adaptador, HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"(#190) Access token has expired\",\"type\":\"OAuthException\"}}");
        Assert.Equal(new ProviderSendResult(ProviderSendOutcome.RejectedPermanent, null, "http-400"), semCodigo);
    }

    [Fact]
    public async Task Nenhum_texto_livre_do_provedor_nem_do_cliente_aparece_no_resultado()
    {
        var (adaptador, handler, _) = Sends.Adapter();
        handler.Returns(HttpStatusCode.BadRequest, SendBodies.Error(131049, "ECO-DO-TEXTO-DO-CLIENTE Olá Maria", "ECO-DO-TOKEN EAAG-CANARIO-DO-TENANT-A-9f3a1c"));

        var resultado = await adaptador.SendTextAsync(Sends.Request(text: "ECO-DO-TEXTO-DO-CLIENTE Olá Maria"), default);

        var texto = resultado.ToString();
        Assert.DoesNotContain("ECO-DO", texto);
        Assert.DoesNotContain("Maria", texto);
        Assert.DoesNotContain(Sends.Canary, texto);
        Assert.DoesNotContain(Sends.Recipient, texto);
    }

    [Theory]
    [InlineData("{\"error\":{\"code\":\"131047 OR 1=1\",\"message\":\"x\"}}")]
    [InlineData("{\"error\":{\"code\":-1,\"message\":\"x\"}}")]
    [InlineData("{\"error\":{\"code\":1.5,\"message\":\"x\"}}")]
    [InlineData("{\"error\":{\"code\":99999999999,\"message\":\"x\"}}")]
    [InlineData("{\"error\":{\"code\":{\"a\":1},\"message\":\"x\"}}")]
    [InlineData("{\"error\":{\"code\":null,\"message\":\"x\"}}")]
    public async Task Codigo_que_nao_e_inteiro_nao_negativo_e_ignorado_e_nunca_e_repetido(string corpo)
    {
        var (adaptador, handler, _) = Sends.Adapter();

        var resultado = await Send(handler, adaptador, HttpStatusCode.BadRequest, corpo);

        Assert.Equal(new ProviderSendResult(ProviderSendOutcome.RejectedPermanent, null, "http-400"), resultado);
    }

    // Ambíguo: nunca reenviado

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "http-500")]
    [InlineData(HttpStatusCode.BadGateway, "http-502")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "http-503")]
    [InlineData(HttpStatusCode.GatewayTimeout, "http-504")]
    public async Task Erro_5xx_e_desconhecido_a_mensagem_pode_ter_sido_enviada(HttpStatusCode http, string codigo)
    {
        var (adaptador, handler, _) = Sends.Adapter();

        Assert.Equal(new ProviderSendResult(ProviderSendOutcome.Unknown, null, codigo), await Send(handler, adaptador, http, "<html>Bad gateway</html>"));
        Assert.Equal(new ProviderSendResult(ProviderSendOutcome.Unknown, null, codigo), await Send(handler, adaptador, http, string.Empty));
        Assert.Equal(ProviderSendOutcome.Unknown, (await Send(handler, adaptador, http, SendBodies.Error(2, "Service temporarily unavailable"))).Outcome);
    }

    [Fact]
    public async Task Tempo_esgotado_do_servidor_408_sem_codigo_conhecido_nao_prova_que_nada_foi_enviado()
    {
        var (adaptador, handler, _) = Sends.Adapter();

        var resultado = await Send(handler, adaptador, HttpStatusCode.RequestTimeout, SendBodies.Error(99999));

        Assert.Equal(ProviderSendOutcome.Unknown, resultado.Outcome);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Erro_4xx_com_corpo_ilegivel_nao_prova_nada_e_e_desconhecido(HttpStatusCode http)
    {
        var (adaptador, handler, _) = Sends.Adapter();

        Assert.Equal(ProviderSendOutcome.Unknown, (await Send(handler, adaptador, http, "<html>Erro</html>")).Outcome);
        Assert.Equal(ProviderSendOutcome.Unknown, (await Send(handler, adaptador, http, "[]")).Outcome);
        Assert.Equal(ProviderSendOutcome.Unknown, (await Send(handler, adaptador, http, "{\"error\":\"texto\"}")).Outcome);
        Assert.Equal(ProviderSendOutcome.Unknown, (await Send(handler, adaptador, http, string.Empty)).Outcome);
    }

    [Fact]
    public async Task Redirecionamento_e_desconhecido_e_nunca_e_seguido()
    {
        var (adaptador, handler, _) = Sends.Adapter();
        handler.Behavior = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://outro.exemplo/roubar") } });

        var resultado = await adaptador.SendTextAsync(Sends.Request(), default);

        Assert.Equal(new ProviderSendResult(ProviderSendOutcome.Unknown, null, "http-302"), resultado);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void O_cliente_proprio_do_adaptador_nao_segue_redirecionamento_nem_guarda_cookie_e_limita_a_vida_da_conexao()
    {
        var opcoes = new WhatsAppOptions();

        using var handler = WhatsAppCloudAdapter.CreateHandler(opcoes);

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.Equal(opcoes.PooledConnectionLifetime, handler.PooledConnectionLifetime);
        Assert.Equal(opcoes.ConnectTimeout, handler.ConnectTimeout);

        // O adaptador que cria o próprio cliente também o descarta.
        using var adaptador = WhatsAppCloudAdapter.Create(opcoes);
        Assert.NotNull(adaptador);
    }

    [Fact]
    public async Task Resposta_com_declaracao_de_tamanho_acima_do_limite_e_ilegivel_e_desconhecida()
    {
        var (adaptador, handler, _) = Sends.Adapter(o => o.MaxResponseBytes = 1024);
        handler.Returns(HttpStatusCode.OK, SendBodies.Ok() + new string(' ', 5000));

        var resultado = await adaptador.SendTextAsync(Sends.Request(), default);

        Assert.Equal(new ProviderSendResult(ProviderSendOutcome.Unknown, null, "invalid-response"), resultado);
    }

    [Fact]
    public async Task Resposta_sem_tamanho_declarado_e_lida_em_blocos_e_cortada_no_limite()
    {
        var (adaptador, handler, _) = Sends.Adapter(o => o.MaxResponseBytes = 1024);
        var corpo = Encoding.UTF8.GetBytes(SendBodies.Ok() + new string(' ', 5000));
        handler.Behavior = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ForwardOnlyStream(corpo)) });

        var resultado = await adaptador.SendTextAsync(Sends.Request(), default);

        Assert.Equal(new ProviderSendResult(ProviderSendOutcome.Unknown, null, "invalid-response"), resultado);
    }

    [Fact]
    public async Task Corpo_de_erro_gigante_com_estado_401_ainda_e_erro_de_conta()
    {
        var (adaptador, handler, _) = Sends.Adapter(o => o.MaxResponseBytes = 1024);

        var resultado = await Send(handler, adaptador, HttpStatusCode.Unauthorized, new string('x', 5000));

        Assert.Equal(new ProviderSendResult(ProviderSendOutcome.AccountError, null, "http-401"), resultado);
    }

    // Tempo, rede e cancelamento

    [Fact]
    public async Task Tempo_esgotado_e_desconhecido_e_nao_lanca()
    {
        var (adaptador, handler, _) = Sends.Adapter(o => o.RequestTimeout = TimeSpan.FromMilliseconds(100));
        handler.Behavior = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        };

        var resultado = await adaptador.SendTextAsync(Sends.Request(), default);

        Assert.Equal(new ProviderSendResult(ProviderSendOutcome.Unknown, null, "timeout"), resultado);
    }

    [Fact]
    public async Task Tempo_esgotado_na_leitura_do_corpo_tambem_e_desconhecido()
    {
        var (adaptador, handler, _) = Sends.Adapter(o => o.RequestTimeout = TimeSpan.FromMilliseconds(150));
        handler.Behavior = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NeverEndingStream()) });

        var resultado = await adaptador.SendTextAsync(Sends.Request(), default);

        Assert.Equal(new ProviderSendResult(ProviderSendOutcome.Unknown, null, "timeout"), resultado);
    }

    [Fact]
    public async Task Falha_de_rede_e_desconhecida_e_a_mensagem_da_excecao_nunca_vai_ao_resultado()
    {
        var (adaptador, handler, _) = Sends.Adapter();
        handler.Behavior = (_, _) => throw new HttpRequestException($"Connection refused https://graph.facebook.com/ Authorization: Bearer {Sends.Canary} texto do cliente");

        var resultado = await adaptador.SendTextAsync(Sends.Request(), default);

        Assert.Equal(new ProviderSendResult(ProviderSendOutcome.Unknown, null, "network-error"), resultado);
        Assert.DoesNotContain(Sends.Canary, resultado.ToString());
    }

    [Fact]
    public async Task Queda_de_conexao_no_meio_da_leitura_e_desconhecida()
    {
        var (adaptador, handler, _) = Sends.Adapter();
        handler.Behavior = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BrokenStream()) });

        var resultado = await adaptador.SendTextAsync(Sends.Request(), default);

        Assert.Equal(new ProviderSendResult(ProviderSendOutcome.Unknown, null, "network-error"), resultado);
    }

    [Fact]
    public async Task O_cancelamento_do_chamador_propaga_e_nao_vira_resultado()
    {
        var (adaptador, handler, _) = Sends.Adapter();
        handler.Behavior = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        };
        using var fonte = new CancellationTokenSource();

        var envio = adaptador.SendTextAsync(Sends.Request(), fonte.Token);
        await fonte.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => envio);
    }

    [Fact]
    public async Task Token_ja_cancelado_propaga_sem_virar_resultado()
    {
        var (adaptador, _, _) = Sends.Adapter();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adaptador.SendTextAsync(Sends.Request(), new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task O_adaptador_nao_repete_o_envio_por_conta_propria_em_nenhum_desfecho()
    {
        foreach (var (status, corpo) in new[]
                 {
                     (HttpStatusCode.InternalServerError, "x"),
                     (HttpStatusCode.TooManyRequests, SendBodies.Error(130429)),
                     (HttpStatusCode.BadRequest, SendBodies.Error(131000)),
                     (HttpStatusCode.BadRequest, SendBodies.Error(190)),
                     (HttpStatusCode.OK, "lixo"),
                 })
        {
            var (adaptador, handler, _) = Sends.Adapter();
            handler.Returns(status, corpo);

            await adaptador.SendTextAsync(Sends.Request(), default);

            Assert.Single(handler.Requests);
        }
    }

    private sealed class NeverEndingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class BrokenStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("conexão encerrada com TEXTO-DO-CLIENTE");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw new IOException("conexão encerrada com TEXTO-DO-CLIENTE");

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
