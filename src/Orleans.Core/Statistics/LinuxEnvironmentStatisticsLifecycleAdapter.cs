#define LOG_MEMORY_PERF_COUNTERS

using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Statistics
{
    internal class LinuxEnvironmentStatisticsLifecycleAdapter<TLifecycle>
        : ILifecycleParticipant<TLifecycle>, ILifecycleObserver where TLifecycle : ILifecycleObservable
    {
        private readonly LinuxEnvironmentStatistics _statistics;

        public LinuxEnvironmentStatisticsLifecycleAdapter(LinuxEnvironmentStatistics statistics)
        {
            _statistics = statistics;
        }

        public Task OnStart(CancellationToken ct) => _statistics.OnStart(ct);

        public Task OnStop(CancellationToken ct) => _statistics.OnStop(ct);

        public void Participate(TLifecycle lifecycle)
        {
            lifecycle.Subscribe(
                nameof(LinuxEnvironmentStatistics),
                ServiceLifecycleStage.RuntimeInitialize,
                this);
        }
    }
}
