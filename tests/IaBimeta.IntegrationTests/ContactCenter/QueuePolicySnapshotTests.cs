using IaBimeta.ContactCenter.Application;
using IaBimeta.IntegrationTests.Channels;
using IaBimeta.IntegrationTests.Conversations;

namespace IaBimeta.IntegrationTests.ContactCenter;

/// <summary>
/// A política de uma fila (fuso, faixas de atendimento, dias fechados e comportamento fora do horário) é lida de UM instante só: uma
/// leitura que acontece enquanto o gestor troca o horário enxerga o horário antigo inteiro ou o novo inteiro, nunca o fuso novo com as
/// faixas antigas. PostgreSQL real, com a troca de horário e as leituras correndo ao mesmo tempo.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class QueuePolicySnapshotTests(PostgresFixture banco)
{
    // Dois horários em que a fila está FECHADA agora, e uma mistura deles que a deixaria ABERTA: o primeiro tem as faixas de todos os dias
    // mas fecha hoje e amanhã; o segundo não fecha dia nenhum, mas só tem faixa em um dia que não é hoje nem amanhã. Faixas do primeiro
    // com os dias fechados do segundo (nenhum) = aberta, o que nenhum dos dois horários diz.
    private static (IReadOnlyList<ScheduleWindowInput> Faixas, IReadOnlyList<DateOnly> Fechados) FechadoPorDiasFechados()
    {
        var hoje = DateOnly.FromDateTime(DateTime.UtcNow);
        return (HandoffTestSupport.SempreAberto, new[] { hoje, hoje.AddDays(1) });
    }

    private static (IReadOnlyList<ScheduleWindowInput> Faixas, IReadOnlyList<DateOnly> Fechados) FechadoPorFaixas()
    {
        var diaDeFora = (DayOfWeek)(((int)DateTime.UtcNow.DayOfWeek + 3) % 7);
        return (new[] { new ScheduleWindowInput(diaDeFora, "00:00", "24:00") }, Array.Empty<DateOnly>());
    }

    [Fact]
    public async Task A_decisao_de_roteamento_e_a_leitura_do_horario_nunca_misturam_o_horario_antigo_com_o_novo()
    {
        var c = await CenarioDeConversas.CriarAsync(banco, "qssnapshot");
        var fila = await c.CriarFilaAsync();
        await c.CriarRegraAsync(fila.Id);
        var sessao = await c.NovaConversaAsync();
        var primeiro = FechadoPorDiasFechados();
        var segundo = FechadoPorFaixas();
        await c.DefinirHorarioAsync(fila.Id, "UTC", primeiro.Faixas, primeiro.Fechados);

        using var parar = new CancellationTokenSource();
        var escritor = Task.Run(async () =>
        {
            var vez = 0;
            while (!parar.IsCancellationRequested)
            {
                var horario = vez++ % 2 == 0 ? segundo : primeiro;
                await c.DefinirHorarioAsync(fila.Id, "UTC", horario.Faixas, horario.Fechados);
            }
        });

        var violacoes = new List<string>();
        var leitores = Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < 120; i++)
            {
                var decisao = await c.SimularAsync(sessao);
                var notas = Assert.Single(decisao.Candidates).Notes;
                if (!notas.Contains("closed-wait"))
                {
                    lock (violacoes)
                    {
                        violacoes.Add($"decisão com a fila aberta: {string.Join(',', notas)}");
                    }
                }

                var visao = (await c.HorarioAsync(fila.Id))!;
                var combina = (visao.Windows.Count, visao.ClosedDates.Count) is (7, 2) or (1, 0);
                if (!combina || visao.OpenNow)
                {
                    lock (violacoes)
                    {
                        violacoes.Add($"horário misturado: {visao.Windows.Count} faixas, {visao.ClosedDates.Count} dias fechados, aberta={visao.OpenNow}");
                    }
                }
            }
        })).ToList();

        await Task.WhenAll(leitores);
        await parar.CancelAsync();
        await escritor;

        Assert.Empty(violacoes);
    }
}
