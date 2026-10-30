namespace IaBimeta.IntegrationTests.Messaging;

/// <summary>
/// Relógio controlado pelo teste: o tempo só anda quando o teste manda. Serve para provar a espera entre tentativas e o vencimento do
/// lease sem esperar de verdade. Os provedores de teste o usam em lugar do relógio do sistema.
/// </summary>
public sealed class RelogioDeTeste : TimeProvider
{
    private long _ticks = DateTimeOffset.UtcNow.UtcTicks;

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

    public void Avancar(TimeSpan tempo) => Interlocked.Add(ref _ticks, tempo.Ticks);

    /// <summary>Volta a seguir o relógio real (para um teste não herdar o adiantamento do anterior).</summary>
    public void Sincronizar() => Interlocked.Exchange(ref _ticks, DateTimeOffset.UtcNow.UtcTicks);
}
