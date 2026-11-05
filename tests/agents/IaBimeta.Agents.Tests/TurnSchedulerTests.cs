using IaBimeta.Agents.Application;
using IaBimeta.Agents.Domain;
using IaBimeta.Agents.Testing;
using IaBimeta.SharedKernel;

namespace IaBimeta.Agents.Tests;

/// <summary>
/// O agendador dos eventos do Conversations (T1, T2, T6, T8, T12, CON-7 e o reconciliador): só GRAVA o <c>AiTurn</c> no "banco" do módulo. Roda sobre o <see cref="InMemoryTurnStore"/>, que tem a
/// semântica do real (cópias na leitura, versão conferida na gravação, um turno aberto por sessão).
/// </summary>
public sealed class TurnSchedulerTests
{
    private static readonly Guid Sessao = Snapshots.SessionId;
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(90);

    private sealed class Cenario
    {
        public Cenario(TenantId? tenant = null, AgentsOptions? opcoes = null)
        {
            Tenant = tenant ?? TestContexts.TenantA;
            Opcoes = opcoes ?? new AgentsOptions();
            Relogio = new FakeClock();
            Banco = new InMemoryTurnStore(Opcoes, Relogio);
            Agendador = new TurnScheduler(Banco, TestContexts.System(Tenant), Opcoes, Relogio);
        }

        public TenantId Tenant { get; }

        public AgentsOptions Opcoes { get; }

        public FakeClock Relogio { get; }

        public InMemoryTurnStore Banco { get; }

        public TurnScheduler Agendador { get; }

        public DateTimeOffset Agora => Relogio.GetUtcNow();

        /// <summary>Um turno aberto da sessão, no estado pedido, gravado direto (como se já existisse).</summary>
        public AiTurn Semear(AiTurnState estado, Action<AiTurn>? ajuste = null)
        {
            var turno = AiTurn.Schedule(Tenant, Sessao, Agora, Agora, Opcoes, Agora);
            switch (estado)
            {
                case AiTurnState.Scheduled:
                    break;
                case AiTurnState.Ready:
                    turno.Claim(1, "executor-1", Agora, Lease);
                    turno.ReleaseForRetry(Agora, TimeSpan.FromSeconds(5), true);
                    break;
                case AiTurnState.Running:
                    turno.Claim(1, "executor-1", Agora, Lease);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(estado));
            }

            ajuste?.Invoke(turno);
            Banco.Seed(turno);
            return turno;
        }

        public AiTurn Aberto() => Banco.OfSession(Sessao).Single(t => t.IsOpen);

        public IReadOnlyList<AiTurn> Todos() => Banco.OfSession(Sessao);
    }

    // T1 e T2

    [Fact]
    public async Task T1_a_primeira_mensagem_cria_o_turno_agendado_com_o_tenant_do_contexto()
    {
        var c = new Cenario();

        await c.Agendador.OnInboundMessageAsync(Sessao, c.Agora, default);

        var turno = Assert.Single(c.Todos());
        Assert.Equal((AiTurnState.Scheduled, c.Tenant, Sessao), (turno.State, turno.TenantId, turno.SessionId));
        Assert.Equal((c.Agora, c.Agora, c.Agora + c.Opcoes.QuietWindow), (turno.FirstReceivedAt, turno.LastReceivedAt, turno.FireAt));
        Assert.Equal(0, turno.SupersedeCount);
    }

    [Fact]
    public async Task O_mesmo_evento_repetido_nunca_cria_um_segundo_turno_aberto()
    {
        var c = new Cenario();

        await c.Agendador.OnInboundMessageAsync(Sessao, c.Agora, default);
        var disparo = c.Aberto().FireAt;
        await c.Agendador.OnInboundMessageAsync(Sessao, c.Agora, default);
        await c.Agendador.OnInboundMessageAsync(Sessao, c.Agora, default);

        var turno = Assert.Single(c.Todos());
        Assert.Equal(disparo, turno.FireAt);
    }

    [Fact]
    public async Task T2_mensagem_nova_com_o_turno_agendado_recalcula_o_disparo_sem_criar_turno()
    {
        var c = new Cenario();
        var primeira = c.Agora;
        await c.Agendador.OnInboundMessageAsync(Sessao, primeira, default);

        c.Relogio.Advance(TimeSpan.FromSeconds(2));
        await c.Agendador.OnInboundMessageAsync(Sessao, c.Agora, default);

        var turno = Assert.Single(c.Todos());
        Assert.Equal(AiTurnState.Scheduled, turno.State);
        Assert.Equal((primeira, primeira.AddSeconds(2), primeira.AddSeconds(5)), (turno.FirstReceivedAt, turno.LastReceivedAt, turno.FireAt));
    }

    [Fact]
    public async Task T2_o_fluxo_continuo_nunca_empurra_o_disparo_alem_do_teto_desde_a_primeira()
    {
        var c = new Cenario();
        var primeira = c.Agora;
        await c.Agendador.OnInboundMessageAsync(Sessao, primeira, default);

        for (var i = 0; i < 30; i++)
        {
            c.Relogio.Advance(TimeSpan.FromSeconds(1));
            await c.Agendador.OnInboundMessageAsync(Sessao, c.Agora, default);

            Assert.True(c.Aberto().FireAt <= primeira + c.Opcoes.MaxWait);
        }
    }

    [Fact]
    public async Task O_horario_futuro_do_evento_e_limitado_ao_agora()
    {
        var c = new Cenario();

        await c.Agendador.OnInboundMessageAsync(Sessao, c.Agora.AddHours(1), default);

        var turno = c.Aberto();
        Assert.Equal((c.Agora, c.Agora, c.Agora + c.Opcoes.QuietWindow), (turno.FirstReceivedAt, turno.LastReceivedAt, turno.FireAt));
    }

    [Fact]
    public async Task Evento_atrasado_gera_turno_devido_ja_porque_o_disparo_ja_passou()
    {
        var c = new Cenario();
        c.Relogio.Advance(TimeSpan.FromMinutes(1));

        await c.Agendador.OnInboundMessageAsync(Sessao, c.Agora.AddSeconds(-30), default);

        Assert.True(c.Aberto().IsDue(c.Agora));
    }

    [Fact]
    public async Task TRN_2_mensagem_com_o_turno_Ready_nao_muda_nada_porque_o_snapshot_do_T4_ja_a_pega()
    {
        var c = new Cenario();
        var semente = c.Semear(AiTurnState.Ready);

        await c.Agendador.OnInboundMessageAsync(Sessao, c.Agora.AddSeconds(1), default);

        var turno = Assert.Single(c.Todos());
        Assert.Equal((AiTurnState.Ready, semente.Version), (turno.State, turno.Version));
    }

    // T6 e T12

    [Fact]
    public async Task T6_mensagem_com_o_turno_em_andamento_grava_o_antigo_Superseded_e_o_sucessor_na_mesma_transacao()
    {
        var c = new Cenario();
        var antigo = c.Semear(AiTurnState.Running);
        var gravacoes = c.Banco.SaveCount;
        c.Relogio.Advance(TimeSpan.FromSeconds(5));

        await c.Agendador.OnInboundMessageAsync(Sessao, c.Agora, default);

        Assert.Equal(gravacoes + 1, c.Banco.SaveCount);
        var turnos = c.Todos();
        Assert.Equal(2, turnos.Count);
        var velho = turnos.Single(t => t.Id == antigo.Id);
        var novo = turnos.Single(t => t.Id != antigo.Id);
        Assert.Equal(AiTurnState.Superseded, velho.State);
        Assert.Equal((AiTurnState.Scheduled, 1, c.Agora + c.Opcoes.QuietWindow), (novo.State, novo.SupersedeCount, novo.FireAt));
        Assert.Single(turnos, t => t.IsOpen);
    }

    [Fact]
    public async Task T6_as_interrupcoes_valem_ate_o_limite_da_rajada_e_depois_vem_a_T12()
    {
        var c = new Cenario();
        c.Semear(AiTurnState.Running);

        // Duas interrupções (o padrão): cada uma troca o turno em andamento por um sucessor, que o executor reivindica de novo.
        for (var rodada = 1; rodada <= c.Opcoes.MaxSupersedesPerBurst; rodada++)
        {
            c.Relogio.Advance(TimeSpan.FromSeconds(5));
            await c.Agendador.OnInboundMessageAsync(Sessao, c.Agora, default);

            var sucessor = c.Aberto();
            Assert.Equal((AiTurnState.Scheduled, rodada), (sucessor.State, sucessor.SupersedeCount));
            c.Banco.Mutate(sucessor.Id, t => t.Claim(rodada + 1, "executor-x", c.Agora, Lease));
        }

        var turnosAntes = c.Todos().Count;
        var emAndamento = c.Aberto();
        Assert.Equal(AiTurnState.Running, emAndamento.State);

        // Acima do limite: T12, a entrada fica pendente e nenhum sucessor nasce.
        c.Relogio.Advance(TimeSpan.FromSeconds(5));
        await c.Agendador.OnInboundMessageAsync(Sessao, c.Agora, default);

        Assert.Equal(turnosAntes, c.Todos().Count);
        var atual = c.Aberto();
        Assert.Equal((emAndamento.Id, AiTurnState.Running, true), (atual.Id, atual.State, atual.InputPending));
    }

    [Fact]
    public async Task Sem_interrupcoes_permitidas_a_primeira_mensagem_durante_o_turno_ja_e_T12()
    {
        var c = new Cenario(opcoes: new AgentsOptions { MaxSupersedesPerBurst = 0 });
        var antigo = c.Semear(AiTurnState.Running);

        await c.Agendador.OnInboundMessageAsync(Sessao, c.Agora.AddSeconds(1), default);

        var turno = Assert.Single(c.Todos());
        Assert.Equal((antigo.Id, AiTurnState.Running, true), (turno.Id, turno.State, turno.InputPending));
    }

    [Fact]
    public async Task O_turno_em_andamento_com_handoff_decidido_nunca_e_substituido_so_marca_a_entrada_pendente()
    {
        var c = new Cenario();
        var antigo = c.Semear(AiTurnState.Running, t => t.RecordHandoff("ai-decision.price", c.Agora));

        await c.Agendador.OnInboundMessageAsync(Sessao, c.Agora.AddSeconds(1), default);

        var turno = Assert.Single(c.Todos());
        Assert.Equal((antigo.Id, AiTurnState.Running, true, "ai-decision.price"), (turno.Id, turno.State, turno.InputPending, turno.HandoffReason));
    }

    // T8: o humano assume

    [Theory]
    [InlineData(AiTurnState.Scheduled)]
    [InlineData(AiTurnState.Ready)]
    [InlineData(AiTurnState.Running)]
    public async Task T8_o_responsavel_deixou_de_ser_a_IA_cancela_o_turno_aberto_em_qualquer_estado(AiTurnState estado)
    {
        var c = new Cenario();
        c.Semear(estado);

        await c.Agendador.OnLeftAiControlAsync(Sessao, "mode-changed", default);

        var turno = Assert.Single(c.Todos());
        Assert.Equal((AiTurnState.Cancelled, "mode-changed"), (turno.State, turno.EndReason));
    }

    [Fact]
    public async Task O_humano_assumiu_com_a_IA_em_voo_o_turno_fica_Cancelled_e_nenhuma_conclusao_e_possivel_depois()
    {
        var c = new Cenario();
        var emVoo = c.Semear(AiTurnState.Running);

        await c.Agendador.OnLeftAiControlAsync(Sessao, "session-closed", default);

        // O executor que voltar da chamada relê o turno (ApplyAsync) e vê que ele já não está em andamento: não conclui, não responde.
        var relido = c.Banco.Get(emVoo.Id);
        Assert.Equal(AiTurnState.Cancelled, relido.State);
        Assert.Throws<InvalidAiTurnTransitionException>(() => relido.Complete(c.Agora, "replied"));
        Assert.Throws<InvalidAiTurnTransitionException>(() => relido.Discard(c.Agora, "stale-input"));
        Assert.DoesNotContain(c.Todos(), t => t.IsOpen);
    }

    [Fact]
    public async Task OnLeftAiControl_sem_turno_aberto_e_inocuo_e_nem_grava()
    {
        var c = new Cenario();

        await c.Agendador.OnLeftAiControlAsync(Sessao, "mode-changed", default);

        Assert.Equal(0, c.Banco.SaveCount);
        Assert.Empty(c.Todos());
    }

    [Fact]
    public async Task OnLeftAiControl_nao_mexe_em_turno_ja_terminal()
    {
        var c = new Cenario();
        var turno = c.Semear(AiTurnState.Running);
        c.Banco.Mutate(turno.Id, t => t.Complete(c.Agora, "replied"));
        var versao = c.Banco.Get(turno.Id).Version;

        await c.Agendador.OnLeftAiControlAsync(Sessao, "mode-changed", default);

        var depois = c.Banco.Get(turno.Id);
        Assert.Equal((AiTurnState.Completed, versao), (depois.State, depois.Version));
    }

    // CON-7: o retorno a IA

    [Fact]
    public async Task CON_7_o_retorno_a_IA_sem_turno_aberto_cria_o_turno_devido_ja()
    {
        var c = new Cenario();

        await c.Agendador.OnReturnedToAiAsync(Sessao, default);

        var turno = Assert.Single(c.Todos());
        Assert.Equal((AiTurnState.Scheduled, c.Agora, c.Tenant), (turno.State, turno.FireAt, turno.TenantId));
        Assert.True(turno.IsDue(c.Agora));
    }

    [Fact]
    public async Task O_retorno_a_IA_acorda_o_turno_agendado_que_ainda_esperava()
    {
        var c = new Cenario();
        c.Semear(AiTurnState.Scheduled);
        c.Relogio.Advance(TimeSpan.FromSeconds(1));

        await c.Agendador.OnReturnedToAiAsync(Sessao, default);

        var turno = Assert.Single(c.Todos());
        Assert.Equal(c.Agora, turno.FireAt);
    }

    [Theory]
    [InlineData(AiTurnState.Ready)]
    [InlineData(AiTurnState.Running)]
    public async Task O_retorno_a_IA_nao_mexe_em_turno_Ready_ou_em_andamento_nem_grava(AiTurnState estado)
    {
        var c = new Cenario();
        var semente = c.Semear(estado);
        var gravacoes = c.Banco.SaveCount;

        await c.Agendador.OnReturnedToAiAsync(Sessao, default);

        Assert.Equal(gravacoes, c.Banco.SaveCount);
        var turno = Assert.Single(c.Todos());
        Assert.Equal((estado, semente.Version), (turno.State, turno.Version));
    }

    // Conteúdo pronto ou falho

    [Fact]
    public async Task O_conteudo_que_ficou_pronto_ou_falhou_acorda_so_o_turno_Scheduled()
    {
        var c = new Cenario();
        c.Semear(AiTurnState.Scheduled, t => t.DeferForContentSafe(c.Agora));
        c.Relogio.Advance(TimeSpan.FromSeconds(1));

        await c.Agendador.OnContentChangedAsync(Sessao, default);

        Assert.Equal(c.Agora, c.Aberto().FireAt);
    }

    [Theory]
    [InlineData(AiTurnState.Ready)]
    [InlineData(AiTurnState.Running)]
    public async Task O_conteudo_pronto_nao_mexe_em_turno_Ready_ou_em_andamento(AiTurnState estado)
    {
        var c = new Cenario();
        var semente = c.Semear(estado);

        await c.Agendador.OnContentChangedAsync(Sessao, default);

        var turno = Assert.Single(c.Todos());
        Assert.Equal((estado, semente.Version), (turno.State, turno.Version));
    }

    [Fact]
    public async Task O_conteudo_pronto_sem_turno_aberto_nao_cria_turno_nem_grava()
    {
        var c = new Cenario();

        await c.Agendador.OnContentChangedAsync(Sessao, default);

        Assert.Empty(c.Todos());
        Assert.Equal(0, c.Banco.SaveCount);
    }

    // Mudança do estado em que o turno foi montado (unidade e idioma)

    [Fact]
    public async Task A_mudanca_de_estado_troca_o_turno_em_andamento_por_um_sucessor_imediato_que_nao_conta_interrupcao()
    {
        var c = new Cenario();
        var antigo = c.Semear(AiTurnState.Running, t => t.MarkInputPending(c.Agora));
        c.Relogio.Advance(TimeSpan.FromSeconds(10));

        await c.Agendador.OnSessionStateChangedAsync(Sessao, default);

        var turnos = c.Todos();
        Assert.Equal(2, turnos.Count);
        Assert.Equal(AiTurnState.Superseded, turnos.Single(t => t.Id == antigo.Id).State);
        var sucessor = turnos.Single(t => t.Id != antigo.Id);
        Assert.Equal((AiTurnState.Scheduled, c.Agora, 0), (sucessor.State, sucessor.FireAt, sucessor.SupersedeCount));
        Assert.True(sucessor.IsDue(c.Agora));
    }

    [Theory]
    [InlineData(AiTurnState.Scheduled)]
    [InlineData(AiTurnState.Ready)]
    public async Task A_mudanca_de_estado_nao_mexe_no_turno_agendado_ou_pronto_que_le_o_estado_novo_no_snapshot(AiTurnState estado)
    {
        var c = new Cenario();
        var semente = c.Semear(estado);

        await c.Agendador.OnSessionStateChangedAsync(Sessao, default);

        var turno = Assert.Single(c.Todos());
        Assert.Equal((estado, semente.Version), (turno.State, turno.Version));
    }

    [Fact]
    public async Task A_mudanca_de_estado_nao_substitui_o_turno_em_andamento_com_handoff_decidido()
    {
        var c = new Cenario();
        c.Semear(AiTurnState.Running, t => t.RecordHandoff("content-unavailable", c.Agora));

        await c.Agendador.OnSessionStateChangedAsync(Sessao, default);

        var turno = Assert.Single(c.Todos());
        Assert.Equal(AiTurnState.Running, turno.State);
    }

    [Fact]
    public async Task A_mudanca_de_estado_sem_turno_aberto_nao_faz_nada()
    {
        var c = new Cenario();

        await c.Agendador.OnSessionStateChangedAsync(Sessao, default);

        Assert.Empty(c.Todos());
        Assert.Equal(0, c.Banco.SaveCount);
    }

    // O reconciliador (CON-15)

    [Fact]
    public async Task EnsureTurnForPending_sem_turno_aberto_cria_o_turno_devido_ja_e_devolve_verdadeiro()
    {
        var c = new Cenario();

        var criou = await c.Agendador.EnsureTurnForPendingAsync(Sessao, default);

        Assert.True(criou);
        var turno = Assert.Single(c.Todos());
        Assert.Equal((AiTurnState.Scheduled, c.Tenant), (turno.State, turno.TenantId));
        Assert.True(turno.IsDue(c.Agora));
    }

    [Theory]
    [InlineData(AiTurnState.Scheduled)]
    [InlineData(AiTurnState.Ready)]
    [InlineData(AiTurnState.Running)]
    public async Task EnsureTurnForPending_com_turno_aberto_nao_cria_outro(AiTurnState estado)
    {
        var c = new Cenario();
        c.Semear(estado);

        var criou = await c.Agendador.EnsureTurnForPendingAsync(Sessao, default);

        Assert.False(criou);
        Assert.Single(c.Todos());
    }

    [Fact]
    public async Task EnsureTurnForPending_repetido_cria_um_so()
    {
        var c = new Cenario();

        var primeira = await c.Agendador.EnsureTurnForPendingAsync(Sessao, default);
        var segunda = await c.Agendador.EnsureTurnForPendingAsync(Sessao, default);

        Assert.True(primeira);
        Assert.False(segunda);
        Assert.Single(c.Todos());
    }

    // Isolamento e concorrência

    [Fact]
    public async Task O_tenant_do_turno_criado_e_sempre_o_do_contexto_e_tenants_diferentes_nao_se_misturam()
    {
        var a = new Cenario(TestContexts.TenantA);
        var b = new Cenario(TestContexts.TenantB);

        // A mesma sessão (mesmo identificador) em dois tenants, cada um com o próprio "banco": nada de um aparece no outro.
        await a.Agendador.OnInboundMessageAsync(Sessao, a.Agora, default);
        await b.Agendador.OnLeftAiControlAsync(Sessao, "mode-changed", default);

        Assert.Equal(TestContexts.TenantA, Assert.Single(a.Todos()).TenantId);
        Assert.Empty(b.Todos());

        await b.Agendador.OnInboundMessageAsync(Sessao, b.Agora, default);
        Assert.Equal(TestContexts.TenantB, Assert.Single(b.Todos()).TenantId);
        Assert.Equal(AiTurnState.Scheduled, Assert.Single(a.Todos()).State);
    }

    [Fact]
    public async Task Sem_contexto_de_tenant_o_agendador_falha_fechado_e_nao_grava()
    {
        var banco = new InMemoryTurnStore(new AgentsOptions(), new FakeClock());
        var agendador = new TurnScheduler(banco, new RequestContextHolder(), new AgentsOptions(), new FakeClock());

        await Assert.ThrowsAsync<TenantContextRequiredException>(() => agendador.OnInboundMessageAsync(Sessao, DateTimeOffset.UtcNow, default));

        Assert.Empty(banco.All);
    }

    [Fact]
    public async Task Conflito_de_versao_com_o_executor_lanca_e_nao_grava_nada()
    {
        var c = new Cenario();
        var semente = c.Semear(AiTurnState.Scheduled);
        var gravacoes = c.Banco.SaveCount;

        // Outra operação (o executor) altera o turno logo antes de a gravação do agendador.
        c.Banco.BeforeNextSave = () => c.Banco.Mutate(semente.Id, t => t.Extend(c.Agora.AddSeconds(30), c.Opcoes, c.Agora.AddSeconds(30)));

        await Assert.ThrowsAsync<AiTurnConflictException>(() => c.Agendador.OnInboundMessageAsync(Sessao, c.Agora.AddSeconds(1), default));

        Assert.Equal(gravacoes, c.Banco.SaveCount);
        var turno = Assert.Single(c.Todos());
        Assert.Equal(c.Agora.AddSeconds(30), turno.LastReceivedAt);
    }

    [Fact]
    public async Task Conflito_no_T6_nao_cria_o_sucessor_nem_marca_o_antigo()
    {
        var c = new Cenario();
        var antigo = c.Semear(AiTurnState.Running);
        c.Banco.BeforeNextSave = () => c.Banco.Mutate(antigo.Id, t => t.Complete(c.Agora, "replied"));

        await Assert.ThrowsAsync<AiTurnConflictException>(() => c.Agendador.OnInboundMessageAsync(Sessao, c.Agora.AddSeconds(1), default));

        var turno = Assert.Single(c.Todos());
        Assert.Equal(AiTurnState.Completed, turno.State);
    }

    [Fact]
    public async Task Dois_agendadores_criando_o_turno_da_sessao_ao_mesmo_tempo_deixam_um_so_aberto()
    {
        var c = new Cenario();

        // O segundo não enxerga o turno do primeiro (a leitura foi antes): a gravação dele bate no índice único e vira conflito.
        c.Banco.BeforeNextSave = () => c.Banco.Seed(AiTurn.Schedule(c.Tenant, Sessao, c.Agora, c.Agora, c.Opcoes, c.Agora));

        await Assert.ThrowsAsync<AiTurnConflictException>(() => c.Agendador.OnInboundMessageAsync(Sessao, c.Agora, default));

        Assert.Single(c.Todos(), t => t.IsOpen);
    }
}

/// <summary>Atalho dos testes: coloca o turno agendado na situação de quem esperou conteúdo (início da espera registrado), pelas transições legais.</summary>
internal static class AiTurnTestExtensions
{
    public static void DeferForContentSafe(this AiTurn turno, DateTimeOffset agora)
    {
        turno.Claim(1, "executor-1", agora, TimeSpan.FromSeconds(90));
        turno.DeferForContent(agora, TimeSpan.FromSeconds(30));
    }
}
