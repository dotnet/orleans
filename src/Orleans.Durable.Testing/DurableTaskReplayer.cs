using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Durable.Jobs;
using Orleans.Durable.Journaling;
using Orleans.Durable.Messaging;
using Orleans.Durable.Tasks.Protocol;
using Orleans.Durable.Tasks.Runtime;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.TestingHost;

namespace Orleans.Durable.Tasks.Testing;

/// <summary>
/// Replays recorded durable task histories against the current code, in a dedicated in-process silo which advances no
/// history, and reports how far each execution went.
/// </summary>
/// <remarks>
/// <para>
/// A replayer hosts one silo, which runs the grain classes that the test process loads, with the services which
/// <c>configureSilo</c> registers. Each <see cref="ReplayAsync"/> seeds the silo's journal storage with a history,
/// activates its grain, whose recovery replays the grain's pending tasks, and reports each execution once none can run.
/// Replays run one at a time, and each replay starts from its history: the grain is deactivated and its journal removed
/// afterwards.
/// </para>
/// <para>
/// The replay reads the recorded history only. An execution which reaches an operation that the history doesn't record,
/// such as a child, a <c>DurableTask.RunInline</c> effect, a delay, a detached start, or a signal's send, stops before
/// starting it. The silo's outboxes deliver nothing, and the silo rejects every grain call from a grain to another grain,
/// such as an ordinary call from replayed code or from an activation callback, so no other grain observes the replay and
/// no reply arrives. Services which <c>configureSilo</c> registers run as registered, so replace those which reach outside
/// the test, such as clients of external systems, with test doubles. The silo's Durable Jobs clock stands before every
/// recorded time, its wake-ups are held, and its inboxes and outboxes start no attempt, so pending inbox messages and
/// outbox deliveries stay at their recorded state, including delays, delivery deadlines, and messages which were already
/// due.
/// Decisions which the replay derives from recorded outcomes, such as
/// <see cref="DurableTask.GetVersion(string, int, int)"/>'s, are made as in a live run and reported.
/// </para>
/// </remarks>
public sealed class DurableTaskReplayer : IAsyncDisposable
{
    // Bounds a replay which never settles, such as one whose grain code blocks, so a test fails instead of hanging.
    private static readonly TimeSpan ReplayTimeout = TimeSpan.FromMinutes(1);

    // How often, and for how many consecutive checks, the replayer confirms that no execution can run.
    private static readonly TimeSpan SettleInterval = TimeSpan.FromMilliseconds(10);
    private const int SettleChecks = 3;

    private readonly InProcessTestCluster _cluster;
    private readonly ReplayRecorder _recorder;
    private readonly ReplayJournalStorageProvider _storage;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Completes once every activation of the latest replay has stopped; the next replay starts only after it completes.
    private Task? _stopping;

    /// <summary>Gets or sets how long the replayer waits for the replayed grain to deactivate after a replay.</summary>
    internal TimeSpan DeactivationTimeout { get; set; } = TimeSpan.FromMinutes(1);

    private DurableTaskReplayer(InProcessTestCluster cluster, ReplayRecorder recorder)
    {
        _cluster = cluster;
        _recorder = recorder;
        _storage = cluster.GetSiloServiceProvider().GetRequiredService<ReplayJournalStorageProvider>();
    }

    /// <summary>Starts a replayer and its silo.</summary>
    /// <param name="configureSilo">
    /// Configures the silo, for example to register the services which the replayed grains depend on, as the tests' own
    /// silos do. Journaling, Durable Jobs, and durable tasks are already added. The replay storage replaces every journal
    /// storage provider, including those which grain types name with <see cref="JournalStorageProviderAttribute"/>.
    /// </param>
    /// <param name="cancellationToken">A token which cancels the start.</param>
    /// <returns>The started replayer, which <see cref="DisposeAsync"/> stops.</returns>
    public static async Task<DurableTaskReplayer> StartAsync(
        Action<ISiloBuilder>? configureSilo = null,
        CancellationToken cancellationToken = default)
    {
        var recorder = new ReplayRecorder();
        var builder = new InProcessTestClusterBuilder(1);
        builder.Options.ConfigureFileLogging = false;
        builder.ConfigureClient(static client => client.AddDurableTasks());
        builder.ConfigureSilo((_, silo) =>
        {
            silo.AddJournaling();
            silo.UseInMemoryDurableJobs();
            silo.AddDurableTasks();
            configureSilo?.Invoke(silo);

            // The replay's own services follow the caller's configuration, which they replace. Every journal storage
            // selection, including a provider which a grain type names, reads and writes the replay storage.
            silo.Services.RemoveAll<IJournalStorageProvider>();
            silo.Services.RemoveAll<IJournalStorageCatalog>();
            RemoveKeyedJournalStorage(silo.Services);
            silo.Services.AddSingleton<ReplayJournalStorageProvider>();
            silo.Services.AddSingleton<IJournalStorageProvider>(static services => services.GetRequiredService<ReplayJournalStorageProvider>());
            silo.Services.AddSingleton<IJournalStorageCatalog>(static services => services.GetRequiredService<ReplayJournalStorageProvider>());
            silo.Services.AddKeyedSingleton<IJournalStorageProvider>(KeyedService.AnyKey, static (services, _) => services.GetRequiredService<ReplayJournalStorageProvider>());
            silo.Services.AddKeyedSingleton<IJournalStorageCatalog>(KeyedService.AnyKey, static (services, _) => services.GetRequiredService<ReplayJournalStorageProvider>());
            silo.Services.AddSingleton<IDurableTaskReplayObserver>(recorder);

            // Messaging starts its pump once the activation completes and performs the work which is due on the Durable
            // Jobs clock: inbox messages, such as a delay's resume, outbox deliveries and retries, and expirations. The
            // replay silo's clock stands before every recorded time, wake-ups are held, and the messaging hold keeps inboxes
            // and outboxes at their recorded state, including messages which are already due, such as those with no
            // delivery time and outbox entries which no attempt has delivered yet.
            silo.Services.AddSingleton<MessagingReplayHold>();
            silo.Services.RemoveAll<ILocalDurableJobManager>();
            silo.Services.AddSingleton<ILocalDurableJobManager>(new HeldJobs());
            silo.Services.RemoveAllKeyed<TimeProvider>(DurableJobTimeProviderNames.DurableJobs);
            silo.Services.AddKeyedSingleton<TimeProvider>(DurableJobTimeProviderNames.DurableJobs, HeldClock.Instance);
            silo.Services.AddSingleton<IOutgoingGrainCallFilter, IsolatingCallFilter>();
        });

        var cluster = builder.Build();
        try
        {
            await cluster.DeployAsync(cancellationToken).ConfigureAwait(false);
            return new(cluster, recorder);
        }
        catch
        {
            await cluster.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Replays <paramref name="history"/> against the current code.</summary>
    /// <param name="history">The recorded history to replay.</param>
    /// <param name="cancellationToken">A token which cancels the replay.</param>
    /// <returns>How far each of the grain's pending executions went.</returns>
    /// <remarks>
    /// <para>
    /// A replay which doesn't settle within a minute, for example because grain code blocks, throws
    /// <see cref="TimeoutException"/> with the executions' progress so far.
    /// </para>
    /// <para>
    /// The replayer deactivates the grain after each replay. A replay whose outcome is already a failure, such as a
    /// timeout or a cancellation, reports that failure even when the deactivation fails. Until every activation of an
    /// earlier replay has stopped, later replays throw <see cref="InvalidOperationException"/>, so a replay never overlaps
    /// an activation which still runs earlier code.
    /// </para>
    /// </remarks>
    /// <exception cref="TimeoutException">The replay didn't settle.</exception>
    /// <exception cref="InvalidOperationException">
    /// The replayed grain didn't deactivate after the replay, or an activation of an earlier replay still runs.
    /// </exception>
    public async Task<DurableTaskReplayResult> ReplayAsync(DurableTaskHistory history, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(history);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stopping is { IsCompleted: false })
            {
                throw new InvalidOperationException(
                    "An activation of an earlier replay hasn't stopped, so the replayer can't start another replay. Code which "
                    + "ignores its attempt's cancellation keeps it running; dispose the replayer to stop its silo.");
            }

            _storage.Seed(history);
            var session = _recorder.Begin(history.GrainId);
            DurableTaskReplayResult result;
            try
            {
                result = await ReplayCoreAsync(history, session, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // The replay's own failure is its outcome; a failed deactivation only keeps later replays from starting.
                await EndAsync(history, session, throwOnFailure: false).ConfigureAwait(false);
                throw;
            }

            await EndAsync(history, session, throwOnFailure: true).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<DurableTaskReplayResult> ReplayCoreAsync(
        DurableTaskHistory history,
        ReplayRecorder.Session session,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReplayTimeout);
        try
        {
            // A call to the grain activates it, and its recovery starts the pending executions.
            var grain = _cluster.Client.GetGrain(history.GrainId);
            await foreach (var _ in grain.AsReference<IDurableTaskGrainExtension>().GetRunningTasksAsync(timeout.Token).ConfigureAwait(false))
            {
            }

            var runtime = await session.Recovered.WaitAsync(timeout.Token).ConfigureAwait(false);
            await WaitUntilSettledAsync(runtime, timeout.Token).ConfigureAwait(false);
            return session.Complete();
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The replay of grain '{history.GrainId}' didn't settle within {ReplayTimeout}.{Environment.NewLine}{session.Complete()}");
        }
    }

    /// <summary>
    /// Ends the recording of <paramref name="session"/>, deactivates the replayed grain, and removes its journal.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The grain didn't deactivate within <see cref="DeactivationTimeout"/>, and <paramref name="throwOnFailure"/> is set.
    /// </exception>
    private async Task EndAsync(DurableTaskHistory history, ReplayRecorder.Session session, bool throwOnFailure)
    {
        _recorder.End();
        var activations = session.Activations;
        foreach (var activation in activations)
        {
            activation.Deactivate(new(DeactivationReasonCode.ApplicationRequested, "The replay ended."));
        }

        // Each activation's Deactivated completes once the activation has unregistered and left the catalog, so the wait
        // observes the activations themselves rather than a directory lookup, which the silo's location cache can answer
        // from before the unregistration.
        _stopping = Task.WhenAll(activations.Select(static activation => activation.Deactivated));
        try
        {
            await _stopping.WaitAsync(DeactivationTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException exception) when (throwOnFailure)
        {
            throw new InvalidOperationException(
                $"The replayed grain '{session.GrainId}' wasn't deactivated within {DeactivationTimeout}.", exception);
        }
        catch (TimeoutException)
        {
        }
        finally
        {
            _storage.Remove(history);
        }
    }

    /// <summary>Stops the replayer's silo.</summary>
    public async ValueTask DisposeAsync()
    {
        await _cluster.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    /// <summary>Removes the named journal storage registrations, which the replay storage replaces.</summary>
    private static void RemoveKeyedJournalStorage(IServiceCollection services)
    {
        for (var index = services.Count - 1; index >= 0; index--)
        {
            var descriptor = services[index];
            if (descriptor.IsKeyedService
                && (descriptor.ServiceType == typeof(IJournalStorageProvider) || descriptor.ServiceType == typeof(IJournalStorageCatalog)))
            {
                services.RemoveAt(index);
            }
        }
    }

    /// <summary>Waits until no execution of <paramref name="runtime"/> can run, which no message changes in a replay.</summary>
    private static async Task WaitUntilSettledAsync(DurableTaskGrainRuntime runtime, CancellationToken cancellationToken)
    {
        for (var settled = 0; settled < SettleChecks;)
        {
            await Task.Delay(SettleInterval, cancellationToken).ConfigureAwait(false);
            settled = await HasRunnableWorkAsync(runtime).WaitAsync(cancellationToken).ConfigureAwait(false) ? 0 : settled + 1;
        }
    }

    /// <summary>Reads whether <paramref name="runtime"/> has runnable work on its activation's scheduler, which owns its state.</summary>
    private static Task<bool> HasRunnableWorkAsync(DurableTaskGrainRuntime runtime)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.GrainContext.Scheduler.QueueAction(() => result.SetResult(runtime.HasRunnableWork));
        return result.Task;
    }

    /// <summary>Acknowledges scheduled wake-ups and holds them for the lifetime of the replay silo.</summary>
    private sealed class HeldJobs : ILocalDurableJobManager
    {
        public Task<DurableJob> ScheduleJobAsync(ScheduleJobRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new DurableJob
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = request.JobName,
                ShardId = "replay",
                TargetGrainId = request.Target,
                DueTime = request.DueTime,
                Metadata = request.Metadata,
                TraceParent = request.TraceParent,
                TraceState = request.TraceState
            });
        }

        public Task<bool> CancelAsync(DurableJob job, CancellationToken requestCancellationToken)
        {
            requestCancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(true);
        }
    }

    /// <summary>A Durable Jobs clock which stands before every recorded time, so no recorded message or deadline is due.</summary>
    private sealed class HeldClock : TimeProvider
    {
        public static HeldClock Instance { get; } = new();

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;
    }

    /// <summary>Rejects grain calls from the replay silo to grains other than the caller, so a replay reaches no other grain.</summary>
    /// <remarks>
    /// The runtime's calls to system targets, such as membership and the grain directory, and a grain's calls to itself
    /// proceed. A call to another grain, such as an ordinary grain call from replayed code or an activation callback,
    /// fails without activating its target.
    /// </remarks>
    private sealed class IsolatingCallFilter : IOutgoingGrainCallFilter
    {
        public Task Invoke(IOutgoingGrainCallContext context)
        {
            if (context.TargetId.IsSystemTarget() || context.SourceId == context.TargetId)
            {
                return context.Invoke();
            }

            throw new InvalidOperationException(
                $"The replay silo delivers no grain call to another grain, so a replay reaches nothing outside its history: "
                + $"'{context.InterfaceName}.{context.MethodName}' from '{context.SourceId}' to '{context.TargetId}' was rejected. "
                + "Replace the dependency which makes the call with a test double in the replayer's silo configuration.");
        }
    }
}
