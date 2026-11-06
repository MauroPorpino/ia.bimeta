using IaBimeta.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace IaBimeta.Messaging;

public sealed record DeadLetterSummary(string Outbox, int Count, DateTimeOffset? Oldest);

public sealed record RetentionRunReport(
    IReadOnlyList<RetentionPass> Passes,
    IReadOnlyList<RetentionTaskResult> Results,
    IReadOnlyList<DeadLetterSummary> DeadLetters);

/// <summary>
/// Orquestra as tarefas de expurgo técnico: para cada categoria, as passadas da política (uma por empresa com prazo próprio e uma para as demais) e, dentro delas, as tarefas dos módulos. Em <c>dryRun</c> só conta.
/// A ordem é a da categoria (eventos processados antes das marcas do Inbox). O expurgo das marcas é BLOQUEADO enquanto houver evento morto mais antigo que o corte da passada: reenfileirar um morto depois de a
/// marca sumir poderia repetir o efeito dele.
/// </summary>
public static class RetentionRunner
{
    public static async Task<RetentionRunReport> RunAsync(IServiceProvider root, bool dryRun, CancellationToken cancellationToken = default)
    {
        var politica = root.GetRequiredService<IRetentionPolicy>();
        var tarefas = root.GetServices<IRetentionTask>().ToList();
        var sondas = root.GetServices<IDeadLetterProbe>().ToList();
        var agora = root.GetRequiredService<TimeProvider>().GetUtcNow();

        var passadasVistas = new List<RetentionPass>();
        var resultados = new List<RetentionTaskResult>();
        foreach (var categoria in Enum.GetValues<RetentionCategory>())
        {
            var passadas = await politica.PassesAsync(categoria, agora, cancellationToken);
            passadasVistas.AddRange(passadas);

            foreach (var passada in passadas)
            {
                foreach (var tarefa in tarefas.Where(t => t.Category == categoria).OrderBy(t => t.Name, StringComparer.Ordinal))
                {
                    if (categoria == RetentionCategory.InboxMarkers)
                    {
                        var bloqueio = await DeadLetterBlockAsync(sondas, passada, cancellationToken);
                        if (bloqueio is not null)
                        {
                            resultados.Add(new RetentionTaskResult(tarefa.Name, categoria, passada.Label, passada.Days, 0, 0, bloqueio));
                            continue;
                        }
                    }

                    resultados.Add(await tarefa.RunAsync(passada, dryRun, cancellationToken));
                }
            }
        }

        // Os mortos de cada outbox, no total (informativo: nunca são expurgados por esta rotina).
        var todos = new RetentionPass(null, Array.Empty<Guid>(), 0, agora, "todas");
        var mortos = new List<DeadLetterSummary>();
        foreach (var sonda in sondas.OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            var (quantos, maisAntigo) = await sonda.DeadAsync(todos, cancellationToken);
            mortos.Add(new DeadLetterSummary(sonda.Name, quantos, maisAntigo));
        }

        return new RetentionRunReport(passadasVistas, resultados, mortos);
    }

    private static async Task<string?> DeadLetterBlockAsync(IReadOnlyList<IDeadLetterProbe> probes, RetentionPass pass, CancellationToken cancellationToken)
    {
        foreach (var sonda in probes)
        {
            var (quantos, maisAntigo) = await sonda.DeadAsync(pass, cancellationToken);
            if (quantos > 0 && maisAntigo is { } data && data < pass.Cutoff)
            {
                return $"BLOQUEADO: há {quantos} evento(s) morto(s) em {sonda.Name}, o mais antigo de {data:yyyy-MM-dd}, anterior ao corte; trate-os (reenfileirar ou descartar com autorização) antes.";
            }
        }

        return null;
    }
}
