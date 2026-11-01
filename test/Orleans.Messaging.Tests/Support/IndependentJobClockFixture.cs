using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableJobs;
using Orleans.Runtime;

namespace Orleans.Messaging.Tests.Support;

/// <summary>Separates actor job clocks without advancing the background scheduler's clock or timers.</summary>
internal sealed class IndependentJobClockFixture : FaultInjectionClusterFixture
{
    public IndependentJobClockFixture() => Clocks = new(JobClock);

    public PerGrainJobClock Clocks { get; }

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        services.AddKeyedSingleton<TimeProvider>(DurableJobTimeProviderNames.DurableJobs, Clocks);
    }
}

internal sealed class PerGrainJobClock(TimeProvider backgroundClock) : TimeProvider
{
    private readonly ConcurrentDictionary<GrainId, TimeProvider> _clocks = new();

    public void Set(IAddressable grain, TimeProvider clock) => _clocks[grain.GetGrainId()] = clock;

    public override DateTimeOffset GetUtcNow() =>
        ReceiverTestServices.CurrentGrainContext is { } context && _clocks.TryGetValue(context.GrainId, out var clock)
            ? clock.GetUtcNow()
            : backgroundClock.GetUtcNow();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        System.CreateTimer(callback, state, dueTime, period);
}
