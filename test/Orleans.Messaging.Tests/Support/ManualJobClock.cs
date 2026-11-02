namespace Orleans.Messaging.Tests.Support;

/// <summary>
/// A Durable Jobs clock whose current time moves only when a test advances it.
/// </summary>
/// <remarks>
/// Timers follow real time and fire at least every <see cref="PollInterval"/>, so Durable Jobs re-reads the clock,
/// starts shards, and runs wake-ups promptly once a test advances it, while tests decide exactly when work becomes due.
/// </remarks>
public sealed class ManualJobClock(DateTimeOffset start) : TimeProvider
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    private long _utcTicks = start.UtcTicks;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        new PollingTimer(base.CreateTimer(callback, state, Clamp(dueTime), Clamp(period)));

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);

    public void Advance(TimeSpan delta) => Interlocked.Add(ref _utcTicks, delta.Ticks);

    private static TimeSpan Clamp(TimeSpan dueTime) => dueTime > PollInterval ? PollInterval : dueTime;

    // Task.Delay creates its timer disarmed and arms it through Change, so both paths clamp.
    private sealed class PollingTimer(ITimer timer) : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => timer.Change(Clamp(dueTime), Clamp(period));

        public void Dispose() => timer.Dispose();

        public ValueTask DisposeAsync() => timer.DisposeAsync();
    }
}
