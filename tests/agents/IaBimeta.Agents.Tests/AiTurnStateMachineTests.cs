using IaBimeta.Agents.Domain;
using IaBimeta.Agents.Testing;

namespace IaBimeta.Agents.Tests;

/// <summary>
/// A máquina de estados do <see cref="AiTurn"/> (CONVERSATION-LIFECYCLE.md §5, T1 a T12): cada transição legal, cada operação ilegal por estado de origem, a fórmula do agrupamento (GRP-2) e as invariantes
/// que o banco também confere (Running ⇔ lane, dono e lease; terminal ⇔ fim e motivo). Sem I/O e sem relógio real.
/// </summary>
public sealed class AiTurnStateMachineTests
{
    private static readonly DateTimeOffset T0 = Snapshots.T0;
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(90);
    private static readonly AgentsOptions Options = new();

    private static readonly AiTurnState[] Todos = Enum.GetValues<AiTurnState>();
    private static readonly AiTurnState[] Abertos = { AiTurnState.Scheduled, AiTurnState.Ready, AiTurnState.Running };
    private static readonly AiTurnState[] SoRunning = { AiTurnState.Running };

    /// <summary>Todas as operações que mudam o estado, com os estados de origem em que são legais.</summary>
    private static readonly (string Nome, Action<AiTurn, DateTimeOffset> Executar, AiTurnState[] Legais)[] Operacoes =
    {
        ("Extend", (t, agora) => t.Extend(agora, Options, agora), new[] { AiTurnState.Scheduled }),
        ("Supersede", (t, agora) => t.Supersede(agora, Options, agora), SoRunning),
        ("MarkInputPending", (t, agora) => t.MarkInputPending(agora), SoRunning),
        ("Claim", (t, agora) => t.Claim(2, "executor-2", agora, Lease), Abertos),
        ("DeferForContent", (t, agora) => t.DeferForContent(agora, TimeSpan.FromSeconds(3)), SoRunning),
        ("ReleaseForRetry", (t, agora) => t.ReleaseForRetry(agora, TimeSpan.FromSeconds(5), true), SoRunning),
        ("RecordHandoff", (t, agora) => t.RecordHandoff("ai-decision.price", agora), SoRunning),
        ("RecordNoticeDone", (t, agora) => t.RecordNoticeDone(agora), SoRunning),
        ("Complete", (t, agora) => t.Complete(agora, "replied"), SoRunning),
        ("Discard", (t, agora) => t.Discard(agora, "stale-input"), SoRunning),
        ("Cancel", (t, agora) => t.Cancel(agora, "mode-changed"), Abertos),
        ("Kill", (t, agora) => t.Kill(agora, "ai-invalid-credentials"), SoRunning),
    };

    public static IEnumerable<object[]> Matriz() =>
        Operacoes.SelectMany(op => Todos.Select(estado => new object[] { op.Nome, estado }));

    private static AiTurn Novo(DateTimeOffset? em = null)
    {
        var quando = em ?? T0;
        return AiTurn.Schedule(TestContexts.TenantA, Snapshots.SessionId, quando, quando, Options, quando);
    }

    /// <summary>Um turno no estado pedido, chegando lá só pelas transições legais.</summary>
    private static AiTurn EmEstado(AiTurnState estado)
    {
        var turno = Novo();
        switch (estado)
        {
            case AiTurnState.Scheduled:
                break;
            case AiTurnState.Ready:
                turno.Claim(1, "executor-1", T0, Lease);
                turno.ReleaseForRetry(T0, TimeSpan.FromSeconds(5), true);
                break;
            case AiTurnState.Running:
                turno.Claim(1, "executor-1", T0, Lease);
                break;
            case AiTurnState.Completed:
                turno.Claim(1, "executor-1", T0, Lease);
                turno.Complete(T0, "replied");
                break;
            case AiTurnState.Discarded:
                turno.Claim(1, "executor-1", T0, Lease);
                turno.Discard(T0, "stale-input");
                break;
            case AiTurnState.Superseded:
                turno.Claim(1, "executor-1", T0, Lease);
                turno.Supersede(T0, Options, T0);
                break;
            case AiTurnState.Cancelled:
                turno.Cancel(T0, "session-closed");
                break;
            case AiTurnState.Dead:
                turno.Claim(1, "executor-1", T0, Lease);
                turno.Kill(T0, "attempts-exhausted");
                break;
        }

        Assert.Equal(estado, turno.State);
        return turno;
    }

    private static void AssertInvariantes(AiTurn t)
    {
        var emAndamento = t.State == AiTurnState.Running;

        // Em andamento se, e somente se, tem o token de lane, o dono e o lease (o CHECK do banco).
        Assert.Equal(emAndamento, t.LaneToken is not null && t.ClaimedBy is not null && t.LeaseExpiresAt is not null);
        if (!emAndamento)
        {
            Assert.Null(t.LaneToken);
            Assert.Null(t.ClaimedBy);
            Assert.Null(t.LeaseExpiresAt);
        }

        // Terminal se, e somente se, tem o instante do fim e o motivo; aberto nunca os tem.
        Assert.Equal(t.IsTerminal, t.EndedAt is not null && t.EndReason is not null);
        Assert.Equal(t.IsOpen, t.EndedAt is null);
        Assert.Equal(t.IsOpen, t.State is AiTurnState.Scheduled or AiTurnState.Ready or AiTurnState.Running);

        // O recuo só existe no estado pronto.
        if (t.NextAttemptAt is not null)
        {
            Assert.Equal(AiTurnState.Ready, t.State);
        }

        if (t.EndReason is not null)
        {
            Assert.False(string.IsNullOrWhiteSpace(t.EndReason));
            Assert.True(t.EndReason.Length <= 100);
        }

        Assert.True(t.Version >= 1);
        Assert.True(t.Attempts >= 0 && t.SupersedeCount >= 0);
    }

    // A tabela: cada operação, de cada estado de origem

    [Theory]
    [MemberData(nameof(Matriz))]
    public void Operacao_so_e_legal_nos_estados_de_origem_da_tabela_e_nas_demais_lanca_sem_mudar_nada(string operacao, AiTurnState origem)
    {
        var (_, executar, legais) = Operacoes.Single(o => o.Nome == operacao);
        var turno = EmEstado(origem);
        var versaoAntes = turno.Version;
        var agora = T0.AddMinutes(10);

        if (legais.Contains(origem))
        {
            executar(turno, agora);

            Assert.True(turno.Version > versaoAntes, $"{operacao} a partir de {origem} precisa avançar a versão.");
            AssertInvariantes(turno);
        }
        else
        {
            Assert.Throws<InvalidAiTurnTransitionException>(() => executar(turno, agora));

            Assert.Equal(origem, turno.State);
            Assert.Equal(versaoAntes, turno.Version);
            AssertInvariantes(turno);
        }
    }

    [Fact]
    public void A_matriz_cobre_os_oito_estados_e_todas_as_operacoes_que_mudam_o_estado()
    {
        Assert.Equal(8, Todos.Length);
        Assert.Equal(12, Operacoes.Length);
        Assert.Equal(Operacoes.Length * Todos.Length, Matriz().Count());
    }

    // T1 a T12, uma a uma

    [Fact]
    public void T1_agendar_cria_o_turno_Scheduled_na_versao_1_sem_tentativas()
    {
        var turno = Novo();

        Assert.Equal(AiTurnState.Scheduled, turno.State);
        Assert.Equal((TestContexts.TenantA, Snapshots.SessionId, 1, 0, 0), (turno.TenantId, turno.SessionId, turno.Version, turno.Attempts, turno.SupersedeCount));
        Assert.Equal(T0 + Options.QuietWindow, turno.FireAt);
        Assert.NotEqual(Guid.Empty, turno.Id);
        Assert.False(turno.InputPending);
        Assert.False(turno.NoticeDone);
        Assert.Null(turno.HandoffReason);
        AssertInvariantes(turno);
    }

    [Fact]
    public void T1_agendar_exige_a_sessao_e_aceita_um_identificador_dado()
    {
        Assert.Throws<ArgumentException>(() => AiTurn.Schedule(TestContexts.TenantA, Guid.Empty, T0, T0, Options, T0));
        var id = Guid.NewGuid();

        Assert.Equal(id, AiTurn.Schedule(TestContexts.TenantA, Snapshots.SessionId, T0, T0, Options, T0, id: id).Id);
    }

    [Fact]
    public void T1_imediato_nasce_devido_ja_para_o_retorno_a_IA_e_o_sucessor_de_recusa()
    {
        var agora = T0.AddSeconds(30);

        var turno = AiTurn.Schedule(TestContexts.TenantA, Snapshots.SessionId, T0, T0, Options, agora, immediate: true);

        Assert.Equal(agora, turno.FireAt);
        Assert.True(turno.IsDue(agora));
    }

    [Fact]
    public void T2_mensagem_nova_com_o_turno_agendado_recalcula_o_disparo()
    {
        var turno = Novo();

        turno.Extend(T0.AddSeconds(1), Options, T0.AddSeconds(1));

        Assert.Equal(AiTurnState.Scheduled, turno.State);
        Assert.Equal(T0.AddSeconds(1) + Options.QuietWindow, turno.FireAt);
        Assert.Equal(T0.AddSeconds(1), turno.LastReceivedAt);
    }

    [Fact]
    public void T3_o_agendado_so_fica_devido_no_disparo_e_o_executor_o_assume()
    {
        var turno = Novo();

        Assert.False(turno.IsDue(turno.FireAt - TimeSpan.FromMilliseconds(1)));
        Assert.True(turno.IsDue(turno.FireAt));

        turno.Claim(7, "executor-1", turno.FireAt, Lease);

        Assert.Equal(AiTurnState.Running, turno.State);
    }

    [Fact]
    public void T4_reivindicar_entrega_o_token_de_lane_o_dono_e_o_lease()
    {
        var turno = Novo();

        turno.Claim(42, "worker:1", T0.AddSeconds(3), Lease);

        Assert.Equal((AiTurnState.Running, 42L, "worker:1"), (turno.State, turno.LaneToken!.Value, turno.ClaimedBy));
        Assert.Equal(T0.AddSeconds(3) + Lease, turno.LeaseExpiresAt);
        Assert.Equal(0, turno.Attempts);
        AssertInvariantes(turno);
    }

    [Fact]
    public void T4b_BeginTurn_recusado_cancela_o_turno_sem_sucessor()
    {
        var turno = EmEstado(AiTurnState.Running);

        turno.Cancel(T0.AddSeconds(4), "not-ai-responder");

        Assert.Equal((AiTurnState.Cancelled, "not-ai-responder"), (turno.State, turno.EndReason));
        AssertInvariantes(turno);
    }

    [Fact]
    public void T5_resposta_aceita_conclui_o_turno()
    {
        var turno = EmEstado(AiTurnState.Running);

        turno.Complete(T0.AddSeconds(8), "replied");

        Assert.Equal((AiTurnState.Completed, "replied", (DateTimeOffset?)T0.AddSeconds(8)), (turno.State, turno.EndReason, turno.EndedAt));
        AssertInvariantes(turno);
    }

    [Fact]
    public void T6_mensagem_nova_com_o_turno_em_andamento_o_troca_pelo_sucessor_agendado()
    {
        var turno = EmEstado(AiTurnState.Running);

        var sucessor = turno.Supersede(T0.AddSeconds(5), Options, T0.AddSeconds(5));

        Assert.Equal((AiTurnState.Superseded, "superseded"), (turno.State, turno.EndReason));
        Assert.Equal(AiTurnState.Scheduled, sucessor.State);
        Assert.NotEqual(turno.Id, sucessor.Id);
        Assert.Equal((turno.TenantId, turno.SessionId), (sucessor.TenantId, sucessor.SessionId));
        AssertInvariantes(turno);
        AssertInvariantes(sucessor);
    }

    [Theory]
    [InlineData("stale-input")]
    [InlineData("turn-not-active")]
    [InlineData("not-ai-responder")]
    public void T7_e_T7b_resposta_recusada_descarta_o_turno(string motivo)
    {
        var turno = EmEstado(AiTurnState.Running);

        turno.Discard(T0.AddSeconds(9), motivo);

        Assert.Equal((AiTurnState.Discarded, motivo), (turno.State, turno.EndReason));
        AssertInvariantes(turno);
    }

    [Theory]
    [InlineData(AiTurnState.Scheduled)]
    [InlineData(AiTurnState.Ready)]
    [InlineData(AiTurnState.Running)]
    public void T8_o_responsavel_deixou_de_ser_a_IA_cancela_qualquer_turno_aberto(AiTurnState origem)
    {
        var turno = EmEstado(origem);

        turno.Cancel(T0.AddSeconds(2), "mode-changed");

        Assert.Equal((AiTurnState.Cancelled, "mode-changed"), (turno.State, turno.EndReason));
        AssertInvariantes(turno);
    }

    [Fact]
    public void T9_falha_transitoria_devolve_o_turno_a_Ready_gastando_uma_tentativa_e_com_recuo()
    {
        var turno = EmEstado(AiTurnState.Running);

        turno.ReleaseForRetry(T0.AddSeconds(6), TimeSpan.FromSeconds(5), countAttempt: true);

        Assert.Equal((AiTurnState.Ready, 1), (turno.State, turno.Attempts));
        Assert.Equal(T0.AddSeconds(11), turno.NextAttemptAt);
        AssertInvariantes(turno);
    }

    [Fact]
    public void T10_falha_permanente_mata_o_turno_com_o_motivo()
    {
        var turno = EmEstado(AiTurnState.Running);

        turno.Kill(T0.AddSeconds(6), "ai-invalid-credentials");

        Assert.Equal((AiTurnState.Dead, "ai-invalid-credentials"), (turno.State, turno.EndReason));
        AssertInvariantes(turno);
    }

    [Fact]
    public void T11_encerramento_limpo_do_worker_devolve_a_Ready_sem_gastar_tentativa()
    {
        var turno = EmEstado(AiTurnState.Running);

        turno.ReleaseForRetry(T0.AddSeconds(6), TimeSpan.FromSeconds(1), countAttempt: false);

        Assert.Equal((AiTurnState.Ready, 0), (turno.State, turno.Attempts));
        AssertInvariantes(turno);
    }

    [Fact]
    public void T12_entrada_com_o_turno_nao_interrompivel_marca_a_entrada_pendente_e_o_turno_continua_Running()
    {
        var turno = EmEstado(AiTurnState.Running);

        turno.MarkInputPending(T0.AddSeconds(5));

        Assert.Equal((AiTurnState.Running, true), (turno.State, turno.InputPending));
        AssertInvariantes(turno);
    }

    // O agrupamento (GRP-1 e GRP-2)

    [Fact]
    public void Os_padroes_do_agrupamento_sao_os_da_referencia_aprovada()
    {
        Assert.Equal((TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10)), (Options.QuietWindow, Options.MaxWait));
    }

    [Theory]
    [InlineData(0, 0, 3)]
    [InlineData(0, 2, 5)]
    [InlineData(0, 7, 10)]
    [InlineData(0, 8, 10)]
    [InlineData(0, 30, 10)]
    [InlineData(4, 5, 8)]
    public void GRP_2_o_disparo_e_o_menor_entre_a_ultima_mais_a_janela_quieta_e_a_primeira_mais_o_teto(int primeira, int ultima, int esperado)
    {
        var disparo = AiTurn.ComputeFireAt(T0.AddSeconds(primeira), T0.AddSeconds(ultima), Options);

        Assert.Equal(T0.AddSeconds(esperado), disparo);
    }

    [Fact]
    public void GRP_1_a_janela_reinicia_a_cada_mensagem_enquanto_o_teto_nao_chega()
    {
        var turno = Novo();
        Assert.Equal(T0.AddSeconds(3), turno.FireAt);

        turno.Extend(T0.AddSeconds(2), Options, T0.AddSeconds(2));
        Assert.Equal(T0.AddSeconds(5), turno.FireAt);

        turno.Extend(T0.AddSeconds(4), Options, T0.AddSeconds(4));
        Assert.Equal(T0.AddSeconds(7), turno.FireAt);
    }

    [Fact]
    public void GRP_1_o_fluxo_continuo_de_mensagens_dispara_no_teto_desde_a_primeira_e_nunca_depois()
    {
        var turno = Novo();

        for (var segundo = 1; segundo <= 40; segundo++)
        {
            turno.Extend(T0.AddSeconds(segundo), Options, T0.AddSeconds(segundo));

            Assert.True(turno.FireAt <= T0 + Options.MaxWait, $"No segundo {segundo} o disparo passou do teto.");
        }

        Assert.Equal(T0 + Options.MaxWait, turno.FireAt);
    }

    [Fact]
    public void Extend_nunca_regride_a_ultima_mensagem_e_uma_mais_antiga_ajusta_a_primeira()
    {
        var turno = Novo(T0.AddSeconds(10));
        turno.Extend(T0.AddSeconds(15), Options, T0.AddSeconds(15));

        // Evento fora de ordem, entre as duas: não muda nada além do disparo recalculado.
        turno.Extend(T0.AddSeconds(12), Options, T0.AddSeconds(16));
        Assert.Equal((T0.AddSeconds(10), T0.AddSeconds(15)), (turno.FirstReceivedAt, turno.LastReceivedAt));

        // Evento mais antigo que a primeira: a primeira recua, e o teto passa a contar dela.
        turno.Extend(T0.AddSeconds(6), Options, T0.AddSeconds(17));
        Assert.Equal((T0.AddSeconds(6), T0.AddSeconds(15)), (turno.FirstReceivedAt, turno.LastReceivedAt));
        Assert.Equal(T0.AddSeconds(16), turno.FireAt);
    }

    // Wake

    [Fact]
    public void Wake_adianta_o_agendado_para_agora_e_devolve_verdadeiro()
    {
        var turno = Novo();
        var agora = T0.AddSeconds(1);
        var versao = turno.Version;

        Assert.True(turno.Wake(agora));

        Assert.Equal(agora, turno.FireAt);
        Assert.Equal(versao + 1, turno.Version);
    }

    [Fact]
    public void Wake_do_agendado_ja_devido_nao_muda_nada()
    {
        var turno = Novo();
        var versao = turno.Version;

        Assert.False(turno.Wake(turno.FireAt));
        Assert.False(turno.Wake(turno.FireAt.AddMinutes(1)));

        Assert.Equal(versao, turno.Version);
    }

    [Theory]
    [InlineData(AiTurnState.Ready)]
    [InlineData(AiTurnState.Running)]
    [InlineData(AiTurnState.Completed)]
    [InlineData(AiTurnState.Discarded)]
    [InlineData(AiTurnState.Superseded)]
    [InlineData(AiTurnState.Cancelled)]
    [InlineData(AiTurnState.Dead)]
    public void Wake_fora_do_estado_agendado_nao_faz_nada_nem_lanca(AiTurnState estado)
    {
        var turno = EmEstado(estado);
        var versao = turno.Version;

        Assert.False(turno.Wake(T0.AddSeconds(1)));

        Assert.Equal((estado, versao), (turno.State, turno.Version));
    }

    // Supersede

    [Fact]
    public void Supersede_entrega_o_sucessor_com_a_janela_quieta_recomecando_na_mensagem_nova()
    {
        var turno = EmEstado(AiTurnState.Running);
        var chegada = T0.AddSeconds(20);

        var sucessor = turno.Supersede(chegada, Options, chegada);

        Assert.Equal((chegada, chegada, chegada + Options.QuietWindow), (sucessor.FirstReceivedAt, sucessor.LastReceivedAt, sucessor.FireAt));
        Assert.Equal(1, sucessor.Version);
        Assert.Equal(0, sucessor.Attempts);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void Supersede_conta_a_interrupcao_so_quando_e_rajada_do_cliente(bool contaInterrupcao, int esperado)
    {
        var turno = EmEstado(AiTurnState.Running);

        var sucessor = turno.Supersede(T0.AddSeconds(1), Options, T0.AddSeconds(1), countsAsInterruption: contaInterrupcao);

        Assert.Equal(esperado, sucessor.SupersedeCount);
    }

    [Fact]
    public void Supersede_acumula_o_contador_da_rajada_nos_sucessores()
    {
        var turno = EmEstado(AiTurnState.Running);
        var primeiro = turno.Supersede(T0.AddSeconds(1), Options, T0.AddSeconds(1));
        primeiro.Claim(2, "executor-2", T0.AddSeconds(5), Lease);

        var segundo = primeiro.Supersede(T0.AddSeconds(6), Options, T0.AddSeconds(6));

        Assert.Equal(2, segundo.SupersedeCount);
    }

    [Fact]
    public void Supersede_imediato_dispara_ja()
    {
        var turno = EmEstado(AiTurnState.Running);
        var agora = T0.AddSeconds(30);

        var sucessor = turno.Supersede(agora, Options, agora, countsAsInterruption: false, immediate: true);

        Assert.Equal(agora, sucessor.FireAt);
        Assert.True(sucessor.IsDue(agora));
    }

    // Claim

    [Fact]
    public void Claim_de_Ready_limpa_o_recuo_e_mantem_as_tentativas()
    {
        var turno = EmEstado(AiTurnState.Ready);
        Assert.NotNull(turno.NextAttemptAt);

        turno.Claim(9, "executor-9", T0.AddSeconds(10), Lease);

        Assert.Equal((AiTurnState.Running, 1, (DateTimeOffset?)null), (turno.State, turno.Attempts, turno.NextAttemptAt));
        AssertInvariantes(turno);
    }

    [Fact]
    public void Claim_de_Running_com_o_lease_vencido_gasta_uma_tentativa_o_lease_perdido()
    {
        var turno = EmEstado(AiTurnState.Running);
        var depoisDoLease = T0 + Lease + TimeSpan.FromSeconds(1);
        Assert.True(turno.IsDue(depoisDoLease));

        turno.Claim(3, "executor-3", depoisDoLease, Lease);

        Assert.Equal((AiTurnState.Running, 1, 3L, "executor-3"), (turno.State, turno.Attempts, turno.LaneToken!.Value, turno.ClaimedBy));
        Assert.Equal(depoisDoLease + Lease, turno.LeaseExpiresAt);
    }

    [Fact]
    public void Claim_zera_a_entrada_pendente_porque_o_snapshot_novo_ja_a_enxerga()
    {
        var turno = EmEstado(AiTurnState.Running);
        turno.MarkInputPending(T0.AddSeconds(2));
        Assert.True(turno.InputPending);

        turno.Claim(5, "executor-5", T0 + Lease + TimeSpan.FromSeconds(1), Lease);

        Assert.False(turno.InputPending);
    }

    [Fact]
    public void Claim_limita_o_nome_do_dono_a_200_caracteres_e_recusa_dono_vazio()
    {
        var turno = Novo();

        Assert.Throws<ArgumentException>(() => turno.Claim(1, "  ", T0, Lease));
        turno.Claim(1, new string('x', 300), T0, Lease);

        Assert.Equal(200, turno.ClaimedBy!.Length);
    }

    // IsDue

    [Fact]
    public void IsDue_por_estado_usa_o_disparo_o_recuo_e_o_fim_do_lease()
    {
        var agendado = Novo();
        Assert.False(agendado.IsDue(agendado.FireAt.AddTicks(-1)));
        Assert.True(agendado.IsDue(agendado.FireAt));

        var pronto = EmEstado(AiTurnState.Ready);
        Assert.False(pronto.IsDue(pronto.NextAttemptAt!.Value.AddTicks(-1)));
        Assert.True(pronto.IsDue(pronto.NextAttemptAt.Value));

        var emAndamento = EmEstado(AiTurnState.Running);
        Assert.False(emAndamento.IsDue(emAndamento.LeaseExpiresAt!.Value.AddTicks(-1)));
        Assert.True(emAndamento.IsDue(emAndamento.LeaseExpiresAt.Value));
    }

    [Theory]
    [InlineData(AiTurnState.Completed)]
    [InlineData(AiTurnState.Discarded)]
    [InlineData(AiTurnState.Superseded)]
    [InlineData(AiTurnState.Cancelled)]
    [InlineData(AiTurnState.Dead)]
    public void IsDue_de_turno_terminal_e_sempre_falso(AiTurnState estado)
    {
        Assert.False(EmEstado(estado).IsDue(T0.AddYears(1)));
    }

    // DeferForContent e ReleaseForRetry

    [Fact]
    public void DeferForContent_volta_a_Scheduled_sem_gastar_tentativa_e_guarda_o_inicio_da_espera_so_na_primeira_vez()
    {
        var turno = EmEstado(AiTurnState.Running);

        turno.DeferForContent(T0.AddSeconds(4), TimeSpan.FromSeconds(3));

        Assert.Equal((AiTurnState.Scheduled, 0, (DateTimeOffset?)T0.AddSeconds(4)), (turno.State, turno.Attempts, turno.ContentWaitStartedAt));
        Assert.Equal(T0.AddSeconds(7), turno.FireAt);
        AssertInvariantes(turno);

        // Segunda espera: o início da contagem não anda (a espera adicional conta da primeira vez).
        turno.Claim(2, "executor-2", T0.AddSeconds(7), Lease);
        turno.DeferForContent(T0.AddSeconds(8), TimeSpan.FromSeconds(3));

        Assert.Equal(T0.AddSeconds(4), turno.ContentWaitStartedAt);
        Assert.Equal(T0.AddSeconds(11), turno.FireAt);
    }

    [Fact]
    public void ReleaseForRetry_sem_contar_tentativa_mantem_o_contador_e_com_contar_soma_um()
    {
        var semContar = EmEstado(AiTurnState.Running);
        semContar.ReleaseForRetry(T0, TimeSpan.FromSeconds(2), countAttempt: false);

        var contando = EmEstado(AiTurnState.Running);
        contando.ReleaseForRetry(T0, TimeSpan.FromSeconds(2), countAttempt: true);

        Assert.Equal((0, 1), (semContar.Attempts, contando.Attempts));
        Assert.Equal(T0.AddSeconds(2), contando.NextAttemptAt);
    }

    [Fact]
    public void ReleaseForRetry_com_espera_negativa_vale_zero()
    {
        var turno = EmEstado(AiTurnState.Running);

        turno.ReleaseForRetry(T0, TimeSpan.FromSeconds(-30), countAttempt: false);

        Assert.Equal(T0, turno.NextAttemptAt);
    }

    // Handoff decidido e aviso

    [Fact]
    public void RecordHandoff_e_idempotente_o_primeiro_motivo_vale_e_a_repeticao_nao_mexe_na_versao()
    {
        var turno = EmEstado(AiTurnState.Running);

        turno.RecordHandoff("ai-decision.price", T0.AddSeconds(1));
        var versao = turno.Version;
        turno.RecordHandoff("ai-processing-failed", T0.AddSeconds(2));

        Assert.Equal("ai-decision.price", turno.HandoffReason);
        Assert.Equal(versao, turno.Version);
    }

    [Fact]
    public void RecordHandoff_limita_o_motivo_a_100_caracteres()
    {
        var turno = EmEstado(AiTurnState.Running);

        turno.RecordHandoff(new string('m', 250), T0);

        Assert.Equal(100, turno.HandoffReason!.Length);
    }

    [Fact]
    public void RecordNoticeDone_e_idempotente()
    {
        var turno = EmEstado(AiTurnState.Running);

        turno.RecordNoticeDone(T0.AddSeconds(1));
        var versao = turno.Version;
        turno.RecordNoticeDone(T0.AddSeconds(2));

        Assert.True(turno.NoticeDone);
        Assert.Equal(versao, turno.Version);
    }

    [Fact]
    public void MarkInputPending_repetido_nao_mexe_na_versao()
    {
        var turno = EmEstado(AiTurnState.Running);
        turno.MarkInputPending(T0.AddSeconds(1));
        var versao = turno.Version;

        turno.MarkInputPending(T0.AddSeconds(2));

        Assert.Equal(versao, turno.Version);
    }

    // Os estados finais: o que limpam e gravam

    [Theory]
    [InlineData("complete")]
    [InlineData("discard")]
    [InlineData("cancel")]
    [InlineData("kill")]
    public void Estado_terminal_limpa_lane_dono_lease_e_recuo_e_grava_o_fim_e_o_motivo(string operacao)
    {
        var turno = EmEstado(AiTurnState.Running);
        var fim = T0.AddSeconds(30);
        var versao = turno.Version;

        switch (operacao)
        {
            case "complete":
                turno.Complete(fim, "replied");
                break;
            case "discard":
                turno.Discard(fim, "stale-input");
                break;
            case "cancel":
                turno.Cancel(fim, "session-closed");
                break;
            default:
                turno.Kill(fim, "ai-quota-or-billing");
                break;
        }

        Assert.True(turno.IsTerminal);
        Assert.Equal((fim, versao + 1), (turno.EndedAt!.Value, turno.Version));
        Assert.Null(turno.NextAttemptAt);
        AssertInvariantes(turno);
    }

    [Fact]
    public void Cancelar_um_turno_Ready_limpa_o_recuo_que_o_CHECK_do_banco_so_admite_em_Ready()
    {
        var turno = EmEstado(AiTurnState.Ready);
        Assert.NotNull(turno.NextAttemptAt);

        turno.Cancel(T0.AddSeconds(1), "session-closed");

        Assert.Null(turno.NextAttemptAt);
        AssertInvariantes(turno);
    }

    [Fact]
    public void Deferir_um_turno_que_esteve_Ready_deixa_o_recuo_nulo()
    {
        var turno = EmEstado(AiTurnState.Ready);
        turno.Claim(2, "executor-2", T0.AddSeconds(5), Lease);

        turno.DeferForContent(T0.AddSeconds(6), TimeSpan.FromSeconds(3));

        Assert.Null(turno.NextAttemptAt);
        AssertInvariantes(turno);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void O_motivo_do_fim_nunca_fica_vazio_vale_unspecified(string motivo)
    {
        var turno = EmEstado(AiTurnState.Running);

        turno.Discard(T0, motivo);

        Assert.Equal("unspecified", turno.EndReason);
    }

    [Fact]
    public void O_motivo_do_fim_e_aparado_e_limitado_a_100_caracteres()
    {
        var turno = EmEstado(AiTurnState.Running);

        turno.Kill(T0, "  " + new string('x', 150) + "  ");

        Assert.Equal(100, turno.EndReason!.Length);
        Assert.Equal(new string('x', 100), turno.EndReason);
    }

    // Invariantes sob uma sequência longa e determinística

    [Fact]
    public void Sequencia_aleatoria_com_semente_fixa_mantem_as_invariantes_e_visita_todos_os_estados()
    {
        var sorteio = new Random(20261105);
        var visitados = new HashSet<AiTurnState>();
        var turno = Novo();
        var agora = T0;

        for (var passo = 0; passo < 3000; passo++)
        {
            agora = agora.AddSeconds(sorteio.Next(1, 40));
            var legais = Operacoes.Where(o => o.Legais.Contains(turno.State)).ToList();

            if (legais.Count == 0 || turno.IsTerminal)
            {
                visitados.Add(turno.State);
                turno = Novo(agora);
                continue;
            }

            var versaoAntes = turno.Version;
            var escolhida = legais[sorteio.Next(legais.Count)];
            escolhida.Executar(turno, agora);

            Assert.True(turno.Version >= versaoAntes, $"A versão regrediu em {escolhida.Nome}.");
            AssertInvariantes(turno);
            visitados.Add(turno.State);
        }

        Assert.Equal(Todos.OrderBy(e => e), visitados.OrderBy(e => e));
    }
}
