using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Diagnostics;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Orleans.Messaging.Configuration;
using Orleans.Runtime;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.IntegrationTests.FaultInjection;

/// <summary>
/// Hosts an in-process cluster whose journal storage, outbox deliveries, inbox participants, and messaging wake-ups run
/// through a <see cref="DurableTaskFaultInjector"/>, and drives workflows to completion through injected faults.
/// </summary>
/// <remarks>
/// <para>
/// The Durable Jobs clock leads the grains' clock by <see cref="JobClockLead"/>, so the workflows' delays, which are
/// shorter, and Durable Jobs retries, which follow the grains' clock, are due as soon as they are scheduled. Outbox
/// retries follow <see cref="RetryInterval"/>. The harness doesn't advance the clock while it waits.
/// </para>
/// <para>
/// The harness synchronizes on events only: a task's completion through the client's wait for its response, and
/// quiescence through the Durable Jobs activities of the grains' messaging wake-ups; see <see cref="SettleAsync"/>.
/// <see cref="RunTimeout"/> bounds each wait to detect a stall.
/// </para>
/// </remarks>
internal sealed class DurableTaskFaultHarness : IAsyncLifetime
{
    public static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan JobClockLead = TimeSpan.FromHours(1);
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(100);
    private const int MaxClientAttempts = 20;
    private readonly MessagingWakeupTracker _wakeups = new();

    public DurableTaskFaultHarness(string journalFormatKey, Action<ISiloBuilder>? configureSilo = null)
    {
        Injector = new();
        Gates = new();
        Fixture = new(journalFormatKey, silo =>
        {
            Injector.Configure(silo);
            silo.Services.AddSingleton(Gates);
            silo.Services.Configure<DurableMessagingOptions>(options => options.RetryInitialInterval = RetryInterval);
            configureSilo?.Invoke(silo);
        });
    }

    public DurableTaskFaultInjector Injector { get; }
    public FaultTestGates Gates { get; }
    public DurableTaskRecoveryFixture Fixture { get; }
    public IClusterClient Client => Fixture.Client;

    public async ValueTask InitializeAsync()
    {
        Fixture.JobClock.Advance(JobClockLead);
        await Fixture.InitializeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        Injector.Reset();
        await Fixture.DisposeAsync();
        _wakeups.Dispose();
    }

    /// <summary>Schedules a root task and waits for its terminal response.</summary>
    /// <remarks>
    /// The client's calls are retried at once when an infrastructure failure fails them, for example when the grain's
    /// activation fails to read its journal, its write fails, or it deactivates while the client waits. The task ID
    /// deduplicates the schedule, and only the task's response is the outcome.
    /// </remarks>
    public async Task<DurableTaskResponse> RunAsync<T>(
        IAddressable grain,
        string rootId,
        Func<CancellationToken, Task> schedule,
        CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        try
        {
            await RetryAsync(schedule, failures, cancellationToken).WaitAsync(RunTimeout, cancellationToken);
            var reference = grain.GetDurableTask<T>(rootId);
            DurableTaskResponse response = null!;
            await RetryAsync(async token => response = await reference.GetResponseAsync(token), failures, cancellationToken)
                .WaitAsync(RunTimeout, cancellationToken);
            return response;
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException(
                $"Timed out after {RunTimeout} waiting for task '{rootId}' on {grain.GetGrainId()}. Client call failures "
                + $"({failures.Count}): {string.Join(" | ", failures.Distinct().Take(5))}",
                exception);
        }
    }

    /// <summary>Waits until each of <paramref name="grains"/> holds no messaging work.</summary>
    /// <remarks>
    /// A grain holds messaging work while its inbox, its outbox, or the durable task runtime's work source, which reports
    /// runnable executions, does. The inbox schedules the grain's messaging wake-up before the journal write which makes
    /// work pending can become durable, and the wake-up job completes only once none remains. A grain therefore holds no
    /// work once each wake-up job which was scheduled for it has completed. Call this after an outcome which follows the
    /// grains' last work, such as a result which their messages produced.
    /// </remarks>
    public Task SettleAsync(IEnumerable<GrainId> grains, CancellationToken cancellationToken) =>
        _wakeups.WaitForIdleAsync(grains).WaitAsync(RunTimeout, cancellationToken);

    /// <summary>Calls a grain until the call succeeds, retrying infrastructure failures at once.</summary>
    public async Task<TResult> CallAsync<TResult>(Func<CancellationToken, Task<TResult>> call, CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        TResult result = default!;
        await RetryAsync(async token => result = await call(token), failures, cancellationToken);
        return result;
    }

    private static async Task RetryAsync(Func<CancellationToken, Task> call, List<string> failures, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await call(cancellationToken);
                return;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested
                && exception is not (DurableTaskConflictException or TimeoutException)
                && attempt < MaxClientAttempts)
            {
                failures.Add(exception.GetType().Name + ": " + exception.Message);
            }
        }
    }

    /// <summary>
    /// Tracks the messaging wake-up jobs which Durable Jobs holds for each grain, through the Durable Jobs activities.
    /// </summary>
    /// <remarks>
    /// A job is outstanding from the end of the activity which scheduled it until an execution of it ends with the job
    /// removed: completed, failed without a retry, or canceled. Rescheduled and retried runs keep it outstanding.
    /// </remarks>
    private sealed class MessagingWakeupTracker : IDisposable
    {
        // The names of the Durable Jobs activities and tags which the tracker reads.
        private const string ScheduleActivity = "schedule durable job";
        private const string ExecuteActivity = "execute durable job";
        private const string JobIdTag = "orleans.durable_job.id";
        private const string JobNameTag = "orleans.durable_job.name";
        private const string TargetTag = "orleans.durable_job.target_grain_id";
        private const string StatusTag = "orleans.durable_job.status";
        private const string MessagingPumpJobName = "orleans.messaging.pump";

        private readonly ActivityListener _listener;
        private readonly Dictionary<string, HashSet<string>> _outstanding = new(StringComparer.Ordinal);
        private readonly List<(HashSet<string> Grains, TaskCompletionSource Idle)> _waiters = [];

        public MessagingWakeupTracker()
        {
            _listener = new()
            {
                ShouldListenTo = static source => source.Name == ActivitySources.DurableJobsActivitySourceName,
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = OnActivityStopped,
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public Task WaitForIdleAsync(IEnumerable<GrainId> grains)
        {
            var keys = grains.Select(static grain => grain.ToString()).ToHashSet(StringComparer.Ordinal);
            lock (_outstanding)
            {
                if (IsIdle(keys))
                {
                    return Task.CompletedTask;
                }

                var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((keys, idle));
                return idle.Task;
            }
        }

        private void OnActivityStopped(Activity activity)
        {
            if (activity.GetTagItem(JobNameTag) as string != MessagingPumpJobName
                || activity.GetTagItem(TargetTag) is not string target
                || activity.GetTagItem(JobIdTag) is not string jobId)
            {
                return;
            }

            lock (_outstanding)
            {
                if (activity.OperationName == ScheduleActivity)
                {
                    if (!_outstanding.TryGetValue(target, out var jobs))
                    {
                        _outstanding[target] = jobs = new(StringComparer.Ordinal);
                    }

                    jobs.Add(jobId);
                }
                else if (activity.OperationName == ExecuteActivity
                    && activity.GetTagItem(StatusTag) is "completed" or "failed" or "cancellation_requested"
                    && _outstanding.TryGetValue(target, out var jobs)
                    && jobs.Remove(jobId))
                {
                    foreach (var waiter in _waiters.Where(waiter => IsIdle(waiter.Grains)).ToList())
                    {
                        _waiters.Remove(waiter);
                        waiter.Idle.TrySetResult();
                    }
                }
            }
        }

        private bool IsIdle(HashSet<string> grains) =>
            grains.All(grain => !_outstanding.TryGetValue(grain, out var jobs) || jobs.Count == 0);

        public void Dispose() => _listener.Dispose();
    }
}
