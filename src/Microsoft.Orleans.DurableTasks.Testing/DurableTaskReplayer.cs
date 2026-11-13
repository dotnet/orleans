using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Core.Internal;
using Orleans.DurableJobs;
using Orleans.DurableTasks.Protocol;
using Orleans.DurableTasks.Runtime;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.TestingHost;

namespace Orleans.DurableTasks.Testing;

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
/// starting it. The silo holds every outgoing message, so no other grain or service observes the replay and no reply
/// arrives. The silo's Durable Jobs clock stands before every recorded time and its wake-ups are held, so pending inbox
/// messages and outbox retries stay at their recorded state, including delays and delivery deadlines which have already
/// passed.
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

    private DurableTaskReplayer(InProcessTestCluster cluster, ReplayRecorder recorder)
    {
        _cluster = cluster;
        _recorder = recorder;
        _storage = cluster.GetSiloServiceProvider().GetRequiredService<ReplayJournalStorageProvider>();
    }

    /// <summary>Starts a replayer and its silo.</summary>
    /// <param name="configureSilo">
    /// Configures the silo, for example to register the services which the replayed grains depend on, as the tests' own
    /// silos do. Journaling, Durable Jobs, and durable tasks are already added.
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

            // The replay's own services follow the caller's configuration, which they replace.
            silo.Services.RemoveAll<IJournalStorageProvider>();
            silo.Services.RemoveAll<IJournalStorageCatalog>();
            silo.Services.AddSingleton<ReplayJournalStorageProvider>();
            silo.Services.AddSingleton<IJournalStorageProvider>(static services => services.GetRequiredService<ReplayJournalStorageProvider>());
            silo.Services.AddSingleton<IJournalStorageCatalog>(static services => services.GetRequiredService<ReplayJournalStorageProvider>());
            silo.Services.AddSingleton<IDurableTaskReplayObserver>(recorder);

            // Messaging starts its pump once the activation completes and performs the work which is due on the Durable
            // Jobs clock: inbox messages, such as a delay's resume, outbox retries, and expirations. The replay silo's
            // clock stands before every recorded time, and wake-ups are held, so recovery replays tasks while both queues
            // stay at their recorded state. Deliveries are held too, so no message reaches its receiver.
            silo.Services.RemoveAll<ILocalDurableJobManager>();
            silo.Services.AddSingleton<ILocalDurableJobManager>(new HeldJobs());
            silo.Services.RemoveAllKeyed<TimeProvider>(DurableJobTimeProviderNames.DurableJobs);
            silo.Services.AddKeyedSingleton<TimeProvider>(DurableJobTimeProviderNames.DurableJobs, HeldClock.Instance);
            silo.Services.AddSingleton<IOutgoingGrainCallFilter, HeldDelivery>();
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
    /// A replay which doesn't settle within a minute, for example because grain code blocks, throws
    /// <see cref="TimeoutException"/> with the executions' progress so far.
    /// </remarks>
    /// <exception cref="TimeoutException">The replay didn't settle.</exception>
    public async Task<DurableTaskReplayResult> ReplayAsync(DurableTaskHistory history, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(history);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ReplayTimeout);
            _storage.Seed(history);
            var session = _recorder.Begin(history.GrainId);
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
            finally
            {
                _recorder.End();
                await DeactivateAsync(history.GrainId).ConfigureAwait(false);
                _storage.Remove(history);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops the replayer's silo.</summary>
    public async ValueTask DisposeAsync()
    {
        await _cluster.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
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

    /// <summary>Deactivates the replayed grain and waits until it has no activation, so the next replay starts afresh.</summary>
    /// <exception cref="InvalidOperationException">The grain kept an activation.</exception>
    private async Task DeactivateAsync(GrainId grainId)
    {
        var grain = _cluster.Client.GetGrain(grainId);
        await grain.AsReference<IGrainManagementExtension>().DeactivateOnIdle().ConfigureAwait(false);
        var management = _cluster.Client.GetGrain<IManagementGrain>(0);
        var deadline = DateTime.UtcNow + ReplayTimeout;
        while (await management.GetActivationAddress(grain).ConfigureAwait(false) is not null)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new InvalidOperationException($"The replayed grain '{grainId}' wasn't deactivated within {ReplayTimeout}.");
            }

            await Task.Delay(SettleInterval).ConfigureAwait(false);
        }
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

    /// <summary>Holds every message which the replay silo's outbox delivers, as an inbox at capacity does.</summary>
    private sealed class HeldDelivery : IOutgoingGrainCallFilter
    {
        public Task Invoke(IOutgoingGrainCallContext context)
        {
            if (context.InterfaceMethod.DeclaringType != typeof(IMessagingExtension)
                || context.MethodName != nameof(IMessagingExtension.DeliverAsync))
            {
                return context.Invoke();
            }

            context.Result = DeliveryResult.Backpressured();
            return Task.CompletedTask;
        }
    }
}
