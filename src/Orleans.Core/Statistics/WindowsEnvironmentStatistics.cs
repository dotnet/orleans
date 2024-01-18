using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;

namespace Orleans.Statistics;

[SuppressMessage("Interoperability", "CA1416:Validate platform compatibility", Justification = "Won't be registered unless the host is Windows")]
internal sealed class WindowsEnvironmentStatistics(ILoggerFactory loggerFactory)
    : EnvironmentStatisticsBase<WindowsEnvironmentStatistics>(loggerFactory)
{
    private readonly PerformanceCounter _memoryCounter = new("Memory", "Available Bytes");

    protected override ValueTask<long?> GetAvailableMemory(CancellationToken cancellationToken)
        => ValueTask.FromResult((long?)_memoryCounter.NextValue());
}

internal sealed class WindowsEnvironmentStatisticsLifecycleAdapter<TLifecycle> : ILifecycleParticipant<TLifecycle>, ILifecycleObserver
    where TLifecycle : ILifecycleObservable
{
    private readonly WindowsEnvironmentStatistics statistics;

    public WindowsEnvironmentStatisticsLifecycleAdapter(WindowsEnvironmentStatistics statistics)
        => this.statistics = statistics;

    public Task OnStart(CancellationToken ct) => statistics.OnStart(ct);
    public Task OnStop(CancellationToken ct) => statistics.OnStop(ct);

    public void Participate(TLifecycle lifecycle) =>
        lifecycle.Subscribe(
            nameof(WindowsEnvironmentStatistics),
            ServiceLifecycleStage.RuntimeInitialize,
            this);
}