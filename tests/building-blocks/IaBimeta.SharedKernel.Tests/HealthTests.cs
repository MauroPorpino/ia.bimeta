using IaBimeta.SharedKernel;

namespace IaBimeta.SharedKernel.Tests;

/// <summary>Os limites das verificações de saúde (ADR 0026) e o texto de idade das constatações.</summary>
public class HealthTests
{
    [Fact]
    public void Os_padroes_sao_validos_e_positivos()
    {
        var limites = new HealthThresholds();

        limites.Validate();

        Assert.Equal(TimeSpan.FromMinutes(5), limites.EventStuckAfter);
        Assert.Equal(TimeSpan.FromMinutes(30), limites.QueueWaitingAfter);
        Assert.Equal(TimeSpan.FromSeconds(60), limites.WorkerSilenceAfter);
        Assert.Equal(TimeSpan.FromHours(24), limites.RecentFailureWindow);
    }

    [Theory]
    [InlineData(nameof(HealthThresholds.EventStuckAfter))]
    [InlineData(nameof(HealthThresholds.OutboundStuckAfter))]
    [InlineData(nameof(HealthThresholds.TurnStuckAfter))]
    [InlineData(nameof(HealthThresholds.QueueWaitingAfter))]
    [InlineData(nameof(HealthThresholds.WorkerSilenceAfter))]
    [InlineData(nameof(HealthThresholds.RecentFailureWindow))]
    public void Limite_zero_ou_negativo_e_recusado(string propriedade)
    {
        foreach (var valor in new[] { TimeSpan.Zero, TimeSpan.FromSeconds(-1) })
        {
            var limites = new HealthThresholds();
            typeof(HealthThresholds).GetProperty(propriedade)!.SetValue(limites, valor);

            Assert.Throws<InvalidOperationException>(limites.Validate);
        }
    }

    [Theory]
    [InlineData(0, "0 s")]
    [InlineData(45, "45 s")]
    [InlineData(89, "89 s")]
    [InlineData(120, "2 min")]
    [InlineData(5400 - 1, "89 min")]
    [InlineData(5400, "1 h")]
    [InlineData(3600 * 5, "5 h")]
    [InlineData(3600 * 47, "47 h")]
    [InlineData(3600 * 72, "3 d")]
    [InlineData(-10, "0 s")]
    public void A_idade_sai_na_menor_unidade_legivel(int segundos, string esperado) =>
        Assert.Equal(esperado, HealthText.Age(TimeSpan.FromSeconds(segundos)));

    [Fact]
    public void O_contexto_sem_empresa_inclui_todas_e_com_empresa_so_a_dela()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var limites = new HealthThresholds();

        Assert.True(new HealthContext(limites, DateTimeOffset.UtcNow).Includes(a));
        Assert.True(new HealthContext(limites, DateTimeOffset.UtcNow, a).Includes(a));
        Assert.False(new HealthContext(limites, DateTimeOffset.UtcNow, a).Includes(b));
    }
}
