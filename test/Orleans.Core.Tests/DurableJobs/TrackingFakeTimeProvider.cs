#nullable enable

using Microsoft.Extensions.Time.Testing;

namespace NonSilo.Tests.Testing;

internal sealed class TrackingFakeTimeProvider(DateTimeOffset start) : FakeTimeProvider(start)
{
    private readonly SemaphoreSlim _timerCreated = new(0);
    private int _createdTimerCount;

    public int CreatedTimerCount => Volatile.Read(ref _createdTimerCount);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);
        Interlocked.Increment(ref _createdTimerCount);
        _timerCreated.Release();
        return timer;
    }

    public async Task WaitForCreatedTimerCountAsync(int expectedCount)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (CreatedTimerCount < expectedCount)
        {
            await _timerCreated.WaitAsync(timeout.Token);
        }
    }
}
