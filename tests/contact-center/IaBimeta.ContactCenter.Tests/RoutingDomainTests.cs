using IaBimeta.ContactCenter.Domain;
using IaBimeta.SharedKernel;

namespace IaBimeta.ContactCenter.Tests;

/// <summary>Regras de domínio do roteamento: horário com fuso explícito, política da fila, regras de roteamento, presença e prioridade do item.</summary>
public sealed class RoutingDomainTests
{
    private static readonly DateTimeOffset Agora = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly TenantId Tenant = new(Guid.NewGuid());
    private static readonly Guid Organizacao = Guid.NewGuid();
    private static readonly Guid FilaId = Guid.NewGuid();

    private static QueueScheduleWindow Faixa(DayOfWeek dia, int inicio, int fim) => QueueScheduleWindow.Create(Tenant, FilaId, dia, inicio, fim);

    private static IEnumerable<QueueScheduleWindow> DiasUteis(int inicio, int fim) =>
        new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday }.Select(d => Faixa(d, inicio, fim));

    // Horário de atendimento com fuso explícito

    [Theory]
    [InlineData("2026-10-26T10:59:00Z", false)] // segunda, 07:59 em São Paulo (UTC-3)
    [InlineData("2026-10-26T11:00:00Z", true)]  // 08:00: o início é inclusivo
    [InlineData("2026-10-26T20:59:00Z", true)]  // 17:59
    [InlineData("2026-10-26T21:00:00Z", false)] // 18:00: o fim é exclusivo
    [InlineData("2026-10-31T15:00:00Z", false)] // sábado
    [InlineData("2026-11-01T15:00:00Z", false)] // domingo
    public void O_horario_vale_no_fuso_da_fila_com_inicio_inclusivo_e_fim_exclusivo(string instante, bool aberta)
    {
        var horario = new QueueSchedule("America/Sao_Paulo", DiasUteis(8 * 60, 18 * 60), Array.Empty<DateOnly>());

        Assert.Equal(aberta, horario.IsOpenAt(DateTimeOffset.Parse(instante)));
    }

    [Fact]
    public void O_mesmo_instante_cai_em_outro_dia_e_hora_conforme_o_fuso()
    {
        // 02:00Z de terça é segunda 23:00 em São Paulo (aberto até 24:00) e terça 02:00 em UTC (fechado: nenhuma faixa de terça cobre).
        var instante = DateTimeOffset.Parse("2026-10-27T02:00:00Z");
        var janelas = new[] { Faixa(DayOfWeek.Monday, 22 * 60, 24 * 60) };

        Assert.True(new QueueSchedule("America/Sao_Paulo", janelas, Array.Empty<DateOnly>()).IsOpenAt(instante));
        Assert.False(new QueueSchedule("UTC", janelas, Array.Empty<DateOnly>()).IsOpenAt(instante));
        Assert.Equal(new DateTime(2026, 10, 26, 23, 0, 0), new QueueSchedule("America/Sao_Paulo", janelas, Array.Empty<DateOnly>()).LocalTime(instante));
    }

    [Fact]
    public void O_horario_de_verao_e_o_do_proprio_fuso_a_mesma_hora_local_muda_o_instante_UTC()
    {
        var horario = new QueueSchedule("America/New_York", DiasUteis(9 * 60, 17 * 60), Array.Empty<DateOnly>());

        // Sexta 30/10/2026 ainda é horário de verão (UTC-4): 13:00Z são 09:00. Segunda 02/11 já não é (UTC-5): 14:00Z são 09:00.
        Assert.True(horario.IsOpenAt(DateTimeOffset.Parse("2026-10-30T13:00:00Z")));
        Assert.False(horario.IsOpenAt(DateTimeOffset.Parse("2026-11-02T13:00:00Z")));
        Assert.True(horario.IsOpenAt(DateTimeOffset.Parse("2026-11-02T14:00:00Z")));
    }

    [Fact]
    public void O_dia_fechado_vence_a_faixa_semanal_na_data_do_fuso_da_fila()
    {
        var janelas = DiasUteis(0, 24 * 60);
        var feriado = new DateOnly(2026, 10, 28); // quarta
        var horario = new QueueSchedule("America/Sao_Paulo", janelas, new[] { feriado });

        Assert.False(horario.IsOpenAt(DateTimeOffset.Parse("2026-10-28T15:00:00Z")));
        Assert.True(horario.IsOpenAt(DateTimeOffset.Parse("2026-10-29T15:00:00Z")));
        // 01:00Z de quinta ainda é quarta em São Paulo (22:00): o dia fechado vale pela data local, não pela de UTC.
        Assert.False(horario.IsOpenAt(DateTimeOffset.Parse("2026-10-29T01:00:00Z")));
    }

    [Fact]
    public void Sem_nenhuma_faixa_o_horario_com_fuso_nunca_abre()
    {
        Assert.False(new QueueSchedule("UTC", Array.Empty<QueueScheduleWindow>(), Array.Empty<DateOnly>()).IsOpenAt(Agora));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("Marte/Olympus")]
    [InlineData("BRT")]
    public void Fuso_horario_vazio_ou_desconhecido_e_recusado_e_nunca_trocado_por_outro(string? fuso)
    {
        Assert.Throws<ContactCenterValidationException>(() => QueueSchedule.ValidateTimeZone(fuso));
    }

    [Fact]
    public void Fuso_horario_IANA_valido_e_aceito_com_o_espaco_aparado_e_acima_do_limite_e_recusado()
    {
        Assert.Equal("America/Sao_Paulo", QueueSchedule.ValidateTimeZone("  America/Sao_Paulo "));
        Assert.Throws<ContactCenterValidationException>(() => QueueSchedule.ValidateTimeZone(new string('a', QueueSchedule.TimeZoneMaxLength + 1)));
    }

    [Theory]
    [InlineData(-1, 60)]
    [InlineData(60, 60)]
    [InlineData(120, 60)]
    [InlineData(0, 1441)]
    public void Faixa_invalida_e_recusada(int inicio, int fim)
    {
        Assert.Throws<ContactCenterValidationException>(() => Faixa(DayOfWeek.Monday, inicio, fim));
    }

    [Fact]
    public void Faixa_de_dia_da_semana_inexistente_e_recusada_e_o_fim_do_dia_e_aceito()
    {
        Assert.Throws<ContactCenterValidationException>(() => Faixa((DayOfWeek)9, 0, 60));
        Assert.Equal(24 * 60, Faixa(DayOfWeek.Sunday, 0, 24 * 60).EndMinute);
    }

    // Política da fila

    private static ServiceQueue Fila() => ServiceQueue.Create(Tenant, Organizacao, null, null, "Fila", Agora);

    [Fact]
    public void A_fila_nova_atende_sempre_aceita_e_espera_com_prioridade_normal_e_sem_limite()
    {
        var fila = Fila();

        Assert.Equal((null, OutOfHoursBehavior.Accept, NoAgentBehavior.Wait, null, 0, null), (fila.TimeZoneId, fila.OutOfHours, fila.NoAgent, fila.FallbackQueueId, fila.DefaultPriority, fila.MaxItemsPerAgent));
    }

    [Fact]
    public void Configurar_a_politica_avanca_a_versao_e_repetir_a_mesma_nao_muda_nada()
    {
        var fila = Fila();
        var contingencia = Guid.NewGuid();

        Assert.True(fila.Configure(contingencia, OutOfHoursBehavior.Fallback, NoAgentBehavior.Fallback, 3, 5, Agora.AddMinutes(1)));
        Assert.Equal((2L, Agora.AddMinutes(1)), (fila.Version, fila.UpdatedAt));
        Assert.False(fila.Configure(contingencia, OutOfHoursBehavior.Fallback, NoAgentBehavior.Fallback, 3, 5, Agora.AddMinutes(2)));
        Assert.Equal((2L, Agora.AddMinutes(1)), (fila.Version, fila.UpdatedAt));
        Assert.Equal((contingencia, OutOfHoursBehavior.Fallback, NoAgentBehavior.Fallback, 3, 5), (fila.FallbackQueueId, fila.OutOfHours, fila.NoAgent, fila.DefaultPriority, fila.MaxItemsPerAgent));
    }

    [Fact]
    public void Comportamento_de_contingencia_exige_fila_de_contingencia_e_ela_nao_pode_ser_a_propria_fila()
    {
        var fila = Fila();

        Assert.Throws<ContactCenterValidationException>(() => fila.Configure(null, OutOfHoursBehavior.Fallback, NoAgentBehavior.Wait, 0, null, Agora));
        Assert.Throws<ContactCenterValidationException>(() => fila.Configure(null, OutOfHoursBehavior.Accept, NoAgentBehavior.Fallback, 0, null, Agora));
        Assert.Throws<ContactCenterValidationException>(() => fila.Configure(fila.Id, OutOfHoursBehavior.Accept, NoAgentBehavior.Wait, 0, null, Agora));
        Assert.Throws<ContactCenterValidationException>(() => fila.Configure(Guid.Empty, OutOfHoursBehavior.Accept, NoAgentBehavior.Wait, 0, null, Agora));
        Assert.Equal(1L, fila.Version);
    }

    [Theory]
    [InlineData(-1, null)]
    [InlineData(10, null)]
    [InlineData(0, 0)]
    [InlineData(0, 1001)]
    public void Prioridade_e_limite_fora_da_faixa_sao_recusados(int prioridade, int? limite)
    {
        Assert.Throws<ContactCenterValidationException>(() => Fila().Configure(null, OutOfHoursBehavior.Accept, NoAgentBehavior.Wait, prioridade, limite, Agora));
    }

    [Fact]
    public void Comportamento_invalido_e_recusado()
    {
        Assert.Throws<ContactCenterValidationException>(() => Fila().Configure(Guid.NewGuid(), (OutOfHoursBehavior)9, NoAgentBehavior.Wait, 0, null, Agora));
        Assert.Throws<ContactCenterValidationException>(() => Fila().Configure(Guid.NewGuid(), OutOfHoursBehavior.Accept, (NoAgentBehavior)9, 0, null, Agora));
    }

    [Fact]
    public void Trocar_o_horario_sempre_avanca_a_versao_e_remover_o_fuso_faz_a_fila_atender_sempre()
    {
        var fila = Fila();

        fila.ReplaceSchedule("America/Sao_Paulo", Agora);
        Assert.Equal(("America/Sao_Paulo", 2L), (fila.TimeZoneId, fila.Version));
        fila.ReplaceSchedule(null, Agora);
        Assert.Equal((null, 3L), (fila.TimeZoneId, fila.Version));
        Assert.Throws<ContactCenterValidationException>(() => fila.ReplaceSchedule("Marte/Olympus", Agora));
        Assert.Equal(3L, fila.Version);
    }

    // Regras de roteamento

    private static RoutingRule Regra(Guid? canal = null, Guid? unidade = null, Guid? setor = null, int prioridade = 0, int ordem = 0) =>
        RoutingRule.Create(Tenant, Organizacao, "Regra", canal, unidade, setor, Guid.NewGuid(), prioridade, ordem, Agora);

    [Fact]
    public void Criterio_vazio_aceita_qualquer_valor_e_cada_criterio_informado_precisa_bater()
    {
        var canal = Guid.NewGuid();
        var unidade = Guid.NewGuid();
        var setor = Guid.NewGuid();

        Assert.True(Regra().Matches(Organizacao, canal, unidade, setor));
        Assert.True(Regra(canal).Matches(Organizacao, canal, null, null));
        Assert.False(Regra(canal).Matches(Organizacao, Guid.NewGuid(), null, null));
        Assert.True(Regra(null, unidade).Matches(Organizacao, canal, unidade, null));
        Assert.False(Regra(null, unidade).Matches(Organizacao, canal, null, null));
        Assert.False(Regra(null, unidade).Matches(Organizacao, canal, Guid.NewGuid(), null));
        Assert.True(Regra(canal, unidade, setor).Matches(Organizacao, canal, unidade, setor));
        Assert.False(Regra(canal, unidade, setor).Matches(Organizacao, canal, unidade, Guid.NewGuid()));
        Assert.False(Regra(canal, unidade, setor).Matches(Organizacao, canal, unidade, null));
    }

    [Fact]
    public void Regra_de_outra_organizacao_ou_desligada_nunca_serve()
    {
        var regra = Regra();

        Assert.False(regra.Matches(Guid.NewGuid(), Guid.NewGuid(), null, null));
        Assert.True(regra.SetEnabled(false, Agora));
        Assert.False(regra.Matches(Organizacao, Guid.NewGuid(), null, null));
        Assert.False(regra.SetEnabled(false, Agora));
        Assert.True(regra.SetEnabled(true, Agora));
        Assert.Equal(3L, regra.Version);
    }

    [Fact]
    public void A_especificidade_conta_os_criterios_definidos()
    {
        Assert.Equal(0, Regra().Specificity);
        Assert.Equal(1, Regra(Guid.NewGuid()).Specificity);
        Assert.Equal(2, Regra(null, Guid.NewGuid(), Guid.NewGuid()).Specificity);
        Assert.Equal(3, Regra(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()).Specificity);
    }

    [Fact]
    public void Dados_invalidos_da_regra_sao_recusados()
    {
        Assert.Throws<ContactCenterValidationException>(() => Regra(null, null, Guid.NewGuid()));
        Assert.Throws<ContactCenterValidationException>(() => Regra(prioridade: 10));
        Assert.Throws<ContactCenterValidationException>(() => Regra(prioridade: -1));
        Assert.Throws<ContactCenterValidationException>(() => Regra(canal: Guid.Empty));
        Assert.Throws<ContactCenterValidationException>(() => RoutingRule.Create(Tenant, Organizacao, "  ", null, null, null, Guid.NewGuid(), 0, 0, Agora));
        Assert.Throws<ContactCenterValidationException>(() => RoutingRule.Create(Tenant, Organizacao, "x", null, null, null, Guid.Empty, 0, 0, Agora));
        Assert.Throws<ContactCenterValidationException>(() => RoutingRule.Create(Tenant, Organizacao, new string('a', RoutingRule.NameMaxLength + 1), null, null, null, Guid.NewGuid(), 0, 0, Agora));
    }

    // Presença

    [Fact]
    public void A_presenca_muda_idempotente_e_recusa_situacao_invalida()
    {
        var presenca = AgentPresence.Create(Tenant, Guid.NewGuid(), AgentStatus.Available, Agora);

        Assert.False(presenca.Change(AgentStatus.Available, Agora.AddMinutes(1)));
        Assert.Equal((1L, Agora), (presenca.Version, presenca.UpdatedAt));
        Assert.True(presenca.Change(AgentStatus.Paused, Agora.AddMinutes(2)));
        Assert.Equal((AgentStatus.Paused, 2L, Agora.AddMinutes(2)), (presenca.Status, presenca.Version, presenca.UpdatedAt));
        Assert.Throws<ContactCenterValidationException>(() => presenca.Change((AgentStatus)9, Agora));
        Assert.Throws<ContactCenterValidationException>(() => AgentPresence.Create(Tenant, Guid.Empty, AgentStatus.Available, Agora));
    }

    // Prioridade do item

    private static QueueItem Item(int prioridade = 0) =>
        QueueItem.Enter(Tenant, Guid.NewGuid(), Organizacao, null, null, FilaId, Guid.NewGuid(), null, "handoff", Agora, prioridade);

    [Fact]
    public void A_prioridade_do_item_vem_da_entrada_e_fora_da_faixa_e_recusada()
    {
        Assert.Equal(5, Item(5).Priority);
        Assert.Throws<ContactCenterValidationException>(() => Item(10));
        Assert.Throws<ContactCenterValidationException>(() => Item(-1));
    }

    [Fact]
    public void Mudar_a_prioridade_registra_antes_e_depois_no_historico_e_repetir_o_mesmo_valor_nao_faz_nada()
    {
        var item = Item(2);
        item.CompletePending(2, Agora);
        var supervisor = Guid.NewGuid();
        var versao = item.Version;

        var historico = item.ChangePriority(7, Guid.NewGuid(), supervisor, "urgent-case", Agora);

        Assert.NotNull(historico);
        Assert.Equal((HistoryKind.PriorityChanged, 2, 7, supervisor), (historico!.Kind, historico.FromPriority, historico.ToPriority, historico.ActorUserId));
        Assert.Equal((7, versao + 1), (item.Priority, item.Version));
        Assert.Null(item.ChangePriority(7, Guid.NewGuid(), supervisor, "urgent-case", Agora));
        Assert.Equal(versao + 1, item.Version);
    }

    [Fact]
    public void A_prioridade_muda_mesmo_com_operacao_pendente_mas_nao_em_item_encerrado()
    {
        var item = Item();
        Assert.True(item.HasPending);

        Assert.NotNull(item.ChangePriority(4, Guid.NewGuid(), null, "urgent-case", Agora));
        Assert.True(item.HasPending);

        item.CancelPending("operation-rejected", Agora);
        Assert.Throws<QueueItemStateException>(() => item.ChangePriority(1, Guid.NewGuid(), null, "urgent-case", Agora));
        Assert.Throws<ContactCenterValidationException>(() => Item().ChangePriority(10, Guid.NewGuid(), null, "urgent-case", Agora));
        Assert.Throws<ContactCenterValidationException>(() => Item().ChangePriority(1, Guid.NewGuid(), null, "Texto livre", Agora));
    }

    [Fact]
    public void A_entrada_com_unidade_de_destino_leva_a_unidade_para_o_item_so_na_conclusao_e_guarda_a_regra()
    {
        var unidade = Guid.NewGuid();
        var setor = Guid.NewGuid();
        var regra = Guid.NewGuid();
        var item = QueueItem.Enter(Tenant, Guid.NewGuid(), Organizacao, null, null, FilaId, Guid.NewGuid(), null, "handoff", Agora, 3, regra, unidade, setor);

        Assert.Equal((null, regra, 3), (item.BusinessUnitId, item.RoutingRuleId, item.Priority));
        Assert.Equal((unidade, setor), (item.Pending!.BusinessUnitId, item.Pending.DepartmentId));

        item.CompletePending(3, Agora);
        Assert.Equal((unidade, setor), (item.BusinessUnitId, item.DepartmentId));
        Assert.Throws<ContactCenterValidationException>(() => QueueItem.Enter(Tenant, Guid.NewGuid(), Organizacao, null, null, FilaId, Guid.NewGuid(), null, "handoff", Agora, 0, null, null, Guid.NewGuid()));
    }
}
