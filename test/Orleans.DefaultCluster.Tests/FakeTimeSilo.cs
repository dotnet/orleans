using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.TestingHost;

namespace DefaultCluster.Tests;

/// <summary>
/// Helpers for tests which drive grain timers deterministically using a <see cref="FakeTimeProvider"/>
/// installed as the silo's <see cref="TimeProvider"/>.
/// </summary>
internal static class FakeTimeSilo
{
    /// <summary>
    /// Installs <paramref name="timeProvider"/> as the silo's <see cref="TimeProvider"/> so that grain
    /// timers and grain-side delays can be advanced deterministically, while keeping the silo's own
    /// background infrastructure timers on real wall-clock time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This isolation is essential. <see cref="FakeTimeProvider.Advance"/> invokes every due timer
    /// callback synchronously and inline on the thread which calls it. The silo's infrastructure
    /// maintenance loops (cluster membership, gateway and grain-directory maintenance, activation
    /// working-set monitoring, incoming-request monitoring, reminders and health checks) resolve their
    /// <see cref="TimeProvider"/> from keyed DI using the per-area keys in
    /// <see cref="TimeProviderNames"/>, and each of those keys defaults to the unkeyed default
    /// <see cref="TimeProvider"/>.
    /// </para>
    /// <para>
    /// If those loops were driven by the fake clock, a single <see cref="FakeTimeProvider.Advance"/>
    /// call would resume them inline; each loop would observe that its deadline has passed, immediately
    /// re-arm a fresh <c>Task.Delay</c> and run its body again, all synchronously on the advancing
    /// thread. The result is a CPU-bound spin that never returns from <c>Advance</c>, hanging the test
    /// until CI's blame-hang timeout (observed as <c>TimerOrleansTest_*</c> hanging for 10 minutes).
    /// Calling <see cref="TimeProviderTestingExtensions.UseTimeProviderForBackgroundAreas"/> pins every
    /// keyed area except <see cref="TimeProviderNames.Grains"/> to
    /// <see cref="TimeProvider.System"/>, keeping those loops off the fake clock entirely, so advancing
    /// the fake clock only ever fires the grain timers under test. Grain timers and grain-side delays
    /// continue to use the unkeyed (fake) <see cref="TimeProvider"/>, so tests retain full control over
    /// them.
    /// </para>
    /// </remarks>
    public static ISiloBuilder UseFakeTimeProviderForGrainTimers(this ISiloBuilder siloBuilder, FakeTimeProvider timeProvider)
    {
        siloBuilder.Services.AddSingleton(timeProvider);
        siloBuilder.Services.Replace(ServiceDescriptor.Singleton<TimeProvider>(sp => sp.GetRequiredService<FakeTimeProvider>()));

        // Keep the silo's own background timers on real time so they are never fired inline by Advance.
        siloBuilder.Services.UseTimeProviderForBackgroundAreas(TimeProvider.System);

        return siloBuilder;
    }

    /// <summary>
    /// Advances the fake clock, guarded by a watchdog which fails fast if the call does not return
    /// promptly. Advancing the fake clock should complete near-instantaneously; if infrastructure timers
    /// are ever driven by the fake clock again (see <see cref="UseFakeTimeProviderForGrainTimers"/>),
    /// they spin the CPU inline and never return. The watchdog converts that regression into an
    /// immediate, diagnosable failure instead of a multi-minute CI hang.
    /// </summary>
    public static void Advance(FakeTimeProvider timeProvider, TimeSpan duration)
    {
        using var watchdog = new AdvanceWatchdog(duration);
        timeProvider.Advance(duration);
    }

    private sealed class AdvanceWatchdog : IDisposable
    {
        // Advancing the fake clock is a synchronous, in-memory operation and completes in well under a
        // second in practice. This limit is generous while remaining far below CI's 10-minute blame-hang
        // timeout, so a genuine spin surfaces quickly.
        private static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

        private readonly TimeSpan _duration;

        // Uses the real system timer queue (not the fake provider), so it still fires while the calling
        // thread is spinning inside Advance.
        private readonly Timer _timer;

        public AdvanceWatchdog(TimeSpan duration)
        {
            _duration = duration;
            _timer = new Timer(static state => ((AdvanceWatchdog)state!).OnElapsed(), this, Limit, Timeout.InfiniteTimeSpan);
        }

        private void OnElapsed() => Environment.FailFast(
            $"FakeTimeProvider.Advance({_duration}) did not return within {Limit}. This indicates that silo " +
            "infrastructure timers are being driven by the test's fake clock and are spinning inline during " +
            "Advance. Ensure infrastructure timers use TimeProvider.System (see FakeTimeSilo.UseFakeTimeProviderForGrainTimers).");

        public void Dispose() => _timer.Dispose();
    }
}
