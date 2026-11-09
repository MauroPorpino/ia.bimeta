using IaBimeta.SharedKernel;

namespace IaBimeta.SharedKernel.Tests;

/// <summary>A política de retenção (ADR 0025): padrões aprovados, sobrescrita por empresa, validação que falha na partida e as passadas que o expurgo executa.</summary>
public class RetentionTests
{
    private static readonly DateTimeOffset Agora = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Os_padroes_sao_os_da_politica_inicial_aprovada()
    {
        var opcoes = new RetentionOptions();

        opcoes.Validate();

        Assert.Equal(7, opcoes.DaysFor(RetentionCategory.ProcessedEvents)); // payload do evento processado: 7 dias
        Assert.Equal(30, opcoes.DaysFor(RetentionCategory.InboxMarkers)); // metadados do Inbox: 30 dias
        Assert.Equal(30, opcoes.DaysFor(RetentionCategory.Sessions));
        Assert.Equal(30, opcoes.DaysFor(RetentionCategory.PasswordResetTokens));
        Assert.Equal(7, opcoes.DaysFor(RetentionCategory.LoginThrottles));
        Assert.Equal(30, opcoes.DaysFor(RetentionCategory.OutboundAttachments)); // arquivo enviado pelo atendente (ADR 0031): 30 dias, o mesmo do arquivo subido na Meta
    }

    [Fact]
    public void O_prazo_do_arquivo_enviado_pode_ser_sobrescrito_por_empresa_e_precisa_ficar_entre_1_e_3650_dias()
    {
        var opcoes = new RetentionOptions();
        opcoes.Tenants["lab-a"] = new Dictionary<RetentionCategory, int> { [RetentionCategory.OutboundAttachments] = 7 };

        Assert.Equal(7, opcoes.DaysFor(RetentionCategory.OutboundAttachments, "lab-a"));
        Assert.Equal(30, opcoes.DaysFor(RetentionCategory.OutboundAttachments, "lab-b"));
        opcoes.Validate();

        opcoes.Days[RetentionCategory.OutboundAttachments] = 0;
        Assert.Throws<InvalidOperationException>(opcoes.Validate);
        opcoes.Days[RetentionCategory.OutboundAttachments] = RetentionOptions.MaximumDays + 1;
        Assert.Throws<InvalidOperationException>(opcoes.Validate);
    }

    [Fact]
    public void A_sobrescrita_vale_so_para_a_empresa_e_so_para_as_categorias_citadas()
    {
        var opcoes = new RetentionOptions();
        opcoes.Tenants["lab-a"] = new Dictionary<RetentionCategory, int> { [RetentionCategory.ProcessedEvents] = 14 };

        Assert.Equal(14, opcoes.DaysFor(RetentionCategory.ProcessedEvents, "lab-a"));
        Assert.Equal(14, opcoes.DaysFor(RetentionCategory.ProcessedEvents, "LAB-A")); // o slug não distingue maiúsculas
        Assert.Equal(7, opcoes.DaysFor(RetentionCategory.ProcessedEvents, "lab-b"));
        Assert.Equal(30, opcoes.DaysFor(RetentionCategory.Sessions, "lab-a")); // categoria não citada: o padrão
        opcoes.Validate();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(RetentionOptions.MaximumDays + 1)]
    public void Prazo_fora_do_intervalo_e_recusado(int dias)
    {
        var opcoes = new RetentionOptions();
        opcoes.Days[RetentionCategory.Sessions] = dias;

        Assert.Throws<InvalidOperationException>(opcoes.Validate);
    }

    [Fact]
    public void A_marca_do_Inbox_nao_pode_expirar_antes_do_evento_processado_nem_na_sobrescrita_de_uma_empresa()
    {
        var padrao = new RetentionOptions();
        padrao.Days[RetentionCategory.InboxMarkers] = 7; // igual ao dos processados (7)
        Assert.Throws<InvalidOperationException>(padrao.Validate);

        var porEmpresa = new RetentionOptions();
        porEmpresa.Tenants["lab-a"] = new Dictionary<RetentionCategory, int> { [RetentionCategory.ProcessedEvents] = 40 }; // passa de 30 dias do Inbox
        Assert.Throws<InvalidOperationException>(porEmpresa.Validate);
    }

    [Fact]
    public void Categoria_removida_ou_empresa_sem_nome_e_recusada()
    {
        var semCategoria = new RetentionOptions();
        semCategoria.Days.Remove(RetentionCategory.LoginThrottles);
        Assert.Throws<InvalidOperationException>(semCategoria.Validate);

        var semNome = new RetentionOptions();
        semNome.Tenants[" "] = new Dictionary<RetentionCategory, int> { [RetentionCategory.Sessions] = 5 };
        Assert.Throws<InvalidOperationException>(semNome.Validate);
    }

    [Fact]
    public async Task Sem_sobrescrita_ha_uma_unica_passada_para_todas_as_empresas()
    {
        var politica = new ConfiguredRetentionPolicy(new RetentionOptions(), (_, _) => Task.FromResult<Guid?>(null));

        var passadas = await politica.PassesAsync(RetentionCategory.ProcessedEvents, Agora);

        var unica = Assert.Single(passadas);
        Assert.Null(unica.TenantId);
        Assert.Empty(unica.ExcludedTenants);
        Assert.Equal(Agora.AddDays(-7), unica.Cutoff);
        Assert.True(unica.Includes(Guid.NewGuid()));
    }

    [Fact]
    public async Task Cada_empresa_com_prazo_proprio_tem_passada_propria_e_sai_da_passada_padrao()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var opcoes = new RetentionOptions();
        opcoes.Tenants["lab-a"] = new Dictionary<RetentionCategory, int> { [RetentionCategory.Sessions] = 90 };
        opcoes.Tenants["lab-b"] = new Dictionary<RetentionCategory, int> { [RetentionCategory.Sessions] = 2, [RetentionCategory.LoginThrottles] = 3 };
        var politica = new ConfiguredRetentionPolicy(opcoes, (slug, _) => Task.FromResult<Guid?>(slug == "lab-a" ? a : slug == "lab-b" ? b : null));

        var sessoes = await politica.PassesAsync(RetentionCategory.Sessions, Agora);
        var contadores = await politica.PassesAsync(RetentionCategory.LoginThrottles, Agora);
        var eventos = await politica.PassesAsync(RetentionCategory.ProcessedEvents, Agora);

        Assert.Equal(3, sessoes.Count);
        var padrao = sessoes.Single(p => p.TenantId is null);
        Assert.False(padrao.Includes(a)); // quem tem passada própria não cai na padrão
        Assert.False(padrao.Includes(b));
        Assert.True(padrao.Includes(Guid.NewGuid()));
        Assert.Equal(Agora.AddDays(-90), sessoes.Single(p => p.TenantId == a).Cutoff);
        Assert.Equal(Agora.AddDays(-2), sessoes.Single(p => p.TenantId == b).Cutoff);
        Assert.True(sessoes.Single(p => p.TenantId == a).Includes(a));
        Assert.False(sessoes.Single(p => p.TenantId == a).Includes(b));

        Assert.Equal(2, contadores.Count); // só lab-b sobrescreve esta categoria: lab-a fica na padrão
        Assert.True(contadores.Single(p => p.TenantId is null).Includes(a));
        Assert.Single(eventos); // ninguém sobrescreve os eventos
    }

    [Fact]
    public async Task Empresa_inexistente_na_configuracao_falha_em_vez_de_aplicar_um_prazo_suposto()
    {
        var opcoes = new RetentionOptions();
        opcoes.Tenants["lab-fantasma"] = new Dictionary<RetentionCategory, int> { [RetentionCategory.Sessions] = 5 };
        var politica = new ConfiguredRetentionPolicy(opcoes, (_, _) => Task.FromResult<Guid?>(null));

        var erro = await Assert.ThrowsAsync<InvalidOperationException>(() => politica.PassesAsync(RetentionCategory.Sessions, Agora));

        Assert.Contains("lab-fantasma", erro.Message);
        // Uma categoria que a empresa não sobrescreve não precisa resolvê-la.
        Assert.Single(await politica.PassesAsync(RetentionCategory.ProcessedEvents, Agora));
    }

    [Fact]
    public void A_politica_invalida_nao_chega_a_ser_construida()
    {
        var opcoes = new RetentionOptions();
        opcoes.Days[RetentionCategory.Sessions] = 0;

        Assert.Throws<InvalidOperationException>(() => new ConfiguredRetentionPolicy(opcoes, (_, _) => Task.FromResult<Guid?>(null)));
    }
}

/// <summary>O agendamento do expurgo técnico no Worker (ADR 0026): quando a execução diária está devida e a validação das opções.</summary>
public class RetentionScheduleTests
{
    private static readonly DateTimeOffset Hoje = new(2026, 11, 6, 0, 0, 0, TimeSpan.Zero);

    private static RetentionScheduleOptions Ligado(double hora = 6) => new() { Enabled = true, TimeOfDayUtc = TimeSpan.FromHours(hora) };

    [Fact]
    public void Vem_desligado_e_desligado_nunca_esta_devido()
    {
        var padrao = new RetentionScheduleOptions();

        Assert.False(padrao.Enabled);
        Assert.False(padrao.IsDue(Hoje.AddHours(12), null));
        Assert.Equal(TimeSpan.FromHours(6), padrao.TimeOfDayUtc);
    }

    [Fact]
    public void Antes_da_hora_marcada_nao_esta_devido_e_depois_dela_sim()
    {
        Assert.False(Ligado().IsDue(Hoje.AddHours(5).AddMinutes(59), null));
        Assert.True(Ligado().IsDue(Hoje.AddHours(6), null));
        Assert.True(Ligado().IsDue(Hoje.AddHours(23), null));
    }

    [Fact]
    public void Depois_de_um_sucesso_de_hoje_so_volta_a_ficar_devido_no_dia_seguinte()
    {
        var sucessoDeHoje = Hoje.AddHours(6).AddMinutes(1);

        Assert.False(Ligado().IsDue(Hoje.AddHours(10), sucessoDeHoje));
        Assert.False(Ligado().IsDue(Hoje.AddDays(1).AddHours(5), sucessoDeHoje)); // amanhã, antes da hora
        Assert.True(Ligado().IsDue(Hoje.AddDays(1).AddHours(6), sucessoDeHoje));
    }

    [Fact]
    public void Quem_estava_fora_do_ar_na_hora_marcada_executa_ao_voltar_e_um_sucesso_de_ontem_nao_vale_para_hoje()
    {
        var sucessoDeOntem = Hoje.AddDays(-1).AddHours(6);

        Assert.True(Ligado().IsDue(Hoje.AddHours(15), null)); // o Worker acabou de subir, às 15h
        Assert.True(Ligado().IsDue(Hoje.AddHours(7), sucessoDeOntem));
    }

    [Theory]
    [InlineData(-1.0, 15, 1000)]
    [InlineData(24.0, 15, 1000)]
    [InlineData(6.0, 0.5, 1000)]
    [InlineData(6.0, 15, 50)]
    public void Opcoes_invalidas_sao_recusadas_e_a_validacao_vale_para_a_politica_inteira(double hora, double retryMinutos, int verificacaoMs)
    {
        var agenda = new RetentionScheduleOptions
        {
            Enabled = true,
            TimeOfDayUtc = TimeSpan.FromHours(hora),
            RetryAfter = TimeSpan.FromMinutes(retryMinutos),
            CheckInterval = TimeSpan.FromMilliseconds(verificacaoMs),
        };

        Assert.Throws<InvalidOperationException>(agenda.Validate);
        Assert.Throws<InvalidOperationException>(() => new RetentionOptions { Schedule = agenda }.Validate());
    }

    [Fact]
    public void A_politica_padrao_continua_valida_com_o_agendamento_dentro()
    {
        var opcoes = new RetentionOptions();

        opcoes.Validate();

        Assert.False(opcoes.Schedule.Enabled);
    }
}
