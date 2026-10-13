namespace Orleans.Messaging.Tests.Support;

/// <summary>
/// A Durable Jobs clock whose current time moves only when a test advances it.
/// </summary>
/// <remarks>
/// Timers and timestamps keep following real time, so Durable Jobs keeps running wake-ups that are due,
/// while tests decide exactly when scheduled messages become due.
/// </remarks>
public sealed class ManualJobClock(DateTimeOffset start) : TimeProvider
{
    private long _utcTicks = start.UtcTicks;

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);

    public void Advance(TimeSpan delta) => Interlocked.Add(ref _utcTicks, delta.Ticks);
}
