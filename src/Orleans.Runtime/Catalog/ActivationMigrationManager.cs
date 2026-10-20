using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.CodeGeneration;
using Orleans.Configuration;
using Orleans.Diagnostics;
using Orleans.Internal;
using Orleans.Metadata;
using Orleans.Runtime.Internal;
using Orleans.Runtime.Messaging;
using Orleans.Runtime.Scheduler;

namespace Orleans.Runtime;

/// <summary>
/// Remote interface for migrating grain activations to a silo.
/// </summary>
/// <remarks>
/// Version 1 accepts queued requests with migrating grains and adds <see cref="RetargetMigratedRequests"/>.
/// A silo sends queued requests only to silos whose manifest lists version 1 or later of this interface.
/// </remarks>
[Version(ActivationMigrationManager.MigratedRequestsVersion)]
internal interface IActivationMigrationManagerSystemTarget : ISystemTarget
{
    /// <summary>
    /// Accepts migrating grains on a best-effort basis, together with the requests which were queued for them.
    /// </summary>
    /// <param name="migratingGrains">The migrating grains.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <remarks>
    /// This silo takes ownership of the requests when it accepts the call. The activation receives them through ordinary FIFO admission.
    /// A new activation receives the requests before it registers in the grain directory.
    /// </remarks>
    [Alias("29E9E63F")]
    ValueTask AcceptMigratingGrains(
        [Immutable] List<GrainMigrationPackage> migratingGrains,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves pending calls on this silo to the silos which accepted their requests.
    /// </summary>
    /// <param name="routes">The calls to retarget.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    [Alias("RetargetMigratedRequests")]
    ValueTask RetargetMigratedRequests(
        [Immutable] List<MigratedRequestRoute> routes,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Identifies a pending call and the silo which now owns its request.
/// </summary>
[GenerateSerializer, Immutable]
internal readonly record struct MigratedRequestRoute(
    [property: Id(0)] GrainId SendingGrain,
    [property: Id(1)] CorrelationId Id,
    [property: Id(2)] SiloAddress TargetSilo,
    [property: Id(3)] int ForwardCount);

[GenerateSerializer, Immutable]
internal struct GrainMigrationPackage
{
    [Id(0)]
    public GrainId GrainId { get; set; }

    [Id(1)]
    public MigrationContext MigrationContext { get; set; }

    /// <summary>
    /// The requests which were queued for the grain when it migrated, if any.
    /// </summary>
    /// <remarks>
    /// The source owns this list until it snapshots the frames. The destination populates it when unpacking the snapshot.
    /// </remarks>
    [field: NonSerialized]
    public List<Message>? Requests { get; set; }

    [Id(2)]
    public byte[]? RequestFrames { get; set; }

    // Each deadline is relative to the batch's header clock, independent of when its frame is written or read.
    [Id(3)]
    public long?[]? RequestDeadlinesMilliseconds { get; set; }

    internal void UnpackRequests(MessageHandlerShared shared, MigrationClock? clock)
    {
        if (RequestFrames is not { } frames)
        {
            return;
        }

        if (clock is null || RequestDeadlinesMilliseconds is not { } deadlines)
        {
            throw new InvalidOperationException("Migrated request frames require a clock and per-request deadlines.");
        }

        var requests = MessageFrames.ReadRequests(shared, frames);
        try
        {
            if (deadlines.Length != requests.Count)
            {
                throw new InvalidOperationException("The number of migrated request deadlines must match the number of frames.");
            }

            for (var i = 0; i < requests.Count; i++)
            {
                if (deadlines[i] is { } deadline)
                {
                    requests[i].SetTimeToLiveMilliseconds(deadline, clock.TimestampMilliseconds);
                }
                else
                {
                    requests[i].SetInfiniteTimeToLive();
                }
            }
        }
        catch
        {
            foreach (var request in requests)
            {
                request.Dispose();
            }

            throw;
        }

        Requests = requests;
        RequestFrames = null;
        RequestDeadlinesMilliseconds = null;
    }
}

/// <summary>
/// Functionality for migrating an activation to a new location.
/// </summary>
internal interface IActivationMigrationManager
{
    /// <summary>
    /// Starts migrating an activation to the specified target, together with the requests which were queued for it.
    /// </summary>
    /// <param name="target">The other silo which receives the migration.</param>
    /// <param name="source">The address of the migrating activation.</param>
    /// <param name="migrationContext">Information about the grain being migrated, which will be consumed by the new activation.</param>
    /// <param name="requests">The requests which were queued for the activation, if any. The migration takes ownership of them.</param>
    /// <param name="cancellationToken">
    /// A token which ends the migration if it has not been sent yet, or if the target has taken its requests and their callers are being retargeted.
    /// A migration which has been sent and awaits the target's response completes when the target responds.
    /// </param>
    /// <returns>The migration.</returns>
    ActivationMigration StartMigration(SiloAddress target, GrainAddress source, MigrationContext migrationContext, List<Message>? requests, CancellationToken cancellationToken);
}

/// <summary>
/// The migration of an activation, which owns the requests which were queued for the activation.
/// </summary>
/// <remarks>
/// <para>
/// The activation transfers its queued requests when it starts the migration, and remains in the catalog until the migration completes,
/// so that requests which reach this silo for the grain reach it. It then leaves the catalog and transfers the requests which reached it meanwhile
/// through <see cref="Deliver"/>. The activation does not access the requests afterwards.
/// </para>
/// <para>
/// Sending snapshots the payload into managed memory and releases the source's pooled request bodies before invoking the RPC.
/// Requests are rejected if the migration fails or its outcome is unknown,
/// so their callers can retry them. Requests which the migration did not send are forwarded, followed by the later requests.
/// </para>
/// </remarks>
internal sealed class ActivationMigration(
    ActivationMigrationManager manager,
    GrainAddress source,
    SiloAddress target,
    GrainMigrationPackage package,
    List<Message>? requests,
    List<(SiloAddress CallerSilo, MigratedRequestRoute Route)>? routes,
    long size,
    CancellationToken cancellationToken)
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenRegistration _cancellation;
    private Stage _stage;

    private enum Stage { Queued, Sent, Accepted }

    /// <summary>
    /// Completes when the target has responded and, if it took the migration's requests, their callers' retargeting has completed or the activation's deadline has passed.
    /// Faults if the migration failed, or was canceled before it was sent.
    /// </summary>
    public Task Completion => _completion.Task;

    internal GrainMigrationPackage Package { get; } = package;

    /// <summary>
    /// The routes for retargeting the callers of the requests which the migration carries.
    /// </summary>
    internal List<(SiloAddress CallerSilo, MigratedRequestRoute Route)>? Routes { get; } = routes;

    /// <summary>
    /// An estimate of the serialized size of the package.
    /// </summary>
    internal long Size { get; } = size;

    internal void RegisterCancellation()
    {
        lock (_completion)
        {
            _cancellation = cancellationToken.UnsafeRegister(static state => ((ActivationMigration)state!).OnCanceled(), this);
            if (Completion.IsCompleted)
            {
                // Register can invoke the callback synchronously before assigning the registration.
                _cancellation.Unregister();
            }
        }
    }

    /// <summary>
    /// Commits the migration to being sent, unless it has been canceled or completed.
    /// </summary>
    internal bool TrySend()
    {
        lock (_completion)
        {
            if (Completion.IsCompleted)
            {
                return false;
            }

            Debug.Assert(_stage == Stage.Queued);
            _stage = Stage.Sent;
            return true;
        }
    }

    /// <summary>
    /// Records acceptance and completes migrations which have no pending retargets or have reached their deadline.
    /// </summary>
    internal void SetAccepted()
    {
        lock (_completion)
        {
            if (Completion.IsCompleted)
            {
                return;
            }

            Debug.Assert(_stage == Stage.Sent);
            _stage = Stage.Accepted;
            if (Routes is null || cancellationToken.IsCancellationRequested)
            {
                Complete();
            }
        }
    }

    /// <summary>
    /// Completes the migration after acceptance, retargeting, or failure.
    /// </summary>
    /// <param name="exception">The error which prevented the migration, if any.</param>
    internal void Complete(Exception? exception = null)
    {
        lock (_completion)
        {
            if (Completion.IsCompleted)
            {
                return;
            }

            Package.MigrationContext.Dispose();
            if (_stage != Stage.Queued && Package.Requests is { } carried)
            {
                foreach (var request in carried)
                {
                    request.ReleaseBodyBuffer();
                }
            }

            // Unregister without waiting for a cancellation callback which may be waiting for this lock.
            _cancellation.Unregister();
            if (exception is null)
            {
                _completion.SetResult();
            }
            else
            {
                _completion.SetException(exception);
            }
        }
    }

    internal GrainMigrationPackage CreateSnapshot(MessageHandlerShared shared, MigrationClock clock)
    {
        Debug.Assert(_stage == Stage.Sent);
        try
        {
            long?[]? deadlines = null;
            if (Package.Requests is { } requests)
            {
                deadlines = new long?[requests.Count];
                for (var i = 0; i < requests.Count; i++)
                {
                    deadlines[i] = requests[i].GetTimeToLiveMilliseconds(clock.TimestampMilliseconds);
                }
            }

            return new()
            {
                GrainId = Package.GrainId,
                MigrationContext = Package.MigrationContext.CreateSnapshot(),
                RequestDeadlinesMilliseconds = deadlines,
                RequestFrames = Package.Requests is { } carried ? MessageFrames.WriteRequests(shared, carried) : null,
            };
        }
        finally
        {
            Package.MigrationContext.Dispose();
            if (Package.Requests is { } carried)
            {
                foreach (var request in carried)
                {
                    request.ReleaseBodyBuffer();
                }
            }
        }
    }

    /// <summary>
    /// Rejects or forwards the migration's requests, followed by <paramref name="laterRequests"/>, once the activation has left the catalog.
    /// </summary>
    /// <param name="laterRequests">The requests which reached the activation after the migration started, if any.</param>
    /// <remarks>The activation calls this once, after <see cref="Completion"/> has completed.</remarks>
    public void Deliver(List<Message>? laterRequests)
    {
        Debug.Assert(Completion.IsCompleted, "The migration delivers its requests once its outcome is known.");
        manager.DeliverRequests(source, target, carried: _stage != Stage.Queued && Package.Requests is not null, _stage == Stage.Accepted, requests, laterRequests);
        requests = null;
    }

    private void OnCanceled()
    {
        lock (_completion)
        {
            // A sent migration waits for its outcome before disposing of its requests.
            if (_stage != Stage.Sent)
            {
                Complete(_stage == Stage.Queued ? new OperationCanceledException(cancellationToken) : null);
            }
        }
    }
}

/// <summary>
/// Migrates grain activations to target hosts and handles migration requests from other hosts.
/// </summary>
internal sealed partial class ActivationMigrationManager : SystemTarget, IActivationMigrationManagerSystemTarget, IActivationMigrationManager, ILifecycleParticipant<ISiloLifecycle>
{
    /// <summary>
    /// The version of <see cref="IActivationMigrationManagerSystemTarget"/> which accepts queued requests with migrating grains.
    /// </summary>
    internal const ushort MigratedRequestsVersion = 1;

    private const int MaxBatchSize = 1_000;
    private const int MessageBodyFramingAllowance = 1_024;
    private const int PackageFramingAllowance = 64;
    private const int RequestDeadlineFramingAllowance = 32;

    private readonly ConcurrentDictionary<SiloAddress, (Task PumpTask, Channel<ActivationMigration> WorkItemChannel)> _workers = new();
    private readonly CancellationTokenSource _shuttingDownCts = new();
    private readonly ILogger<ActivationMigrationManager> _logger;
    private readonly IInternalGrainFactory _grainFactory;
    private readonly Catalog _catalog;
    private readonly InsideRuntimeClient _runtimeClient;
    private readonly IClusterMembershipService _clusterMembershipService;
    private readonly IClusterManifestProvider _clusterManifestProvider;
    private readonly GrainInterfaceType _migrationInterfaceType;
    private MessageCenter? _messageCenter;
    private readonly int _maxMessageBodySize;
    private readonly ActivationDirectory _activations;
#if NET9_0_OR_GREATER
    private readonly Lock _lock = new();
#else
    private readonly object _lock = new();
#endif

#pragma warning disable IDE0052 // Remove unread private members. Justification: this field is only for diagnostic purposes.
    private readonly Task? _membershipUpdatesTask;
#pragma warning restore IDE0052 // Remove unread private members

    public ActivationMigrationManager(
        ILocalSiloDetails localSiloDetails,
        ILoggerFactory loggerFactory,
        IInternalGrainFactory grainFactory,
        Catalog catalog,
        SystemTargetShared shared,
        IClusterMembershipService clusterMembershipService,
        IOptions<SiloMessagingOptions> messagingOptions,
        IClusterManifestProvider clusterManifestProvider,
        GrainInterfaceTypeResolver interfaceTypeResolver) : base(Constants.ActivationMigratorType, shared)
    {
        _clusterManifestProvider = clusterManifestProvider;
        _migrationInterfaceType = interfaceTypeResolver.GetGrainInterfaceType(typeof(IActivationMigrationManagerSystemTarget));
        _maxMessageBodySize = messagingOptions.Value.MaxMessageBodySize;
        _activations = shared.ActivationDirectory;
        _grainFactory = grainFactory;
        _logger = loggerFactory.CreateLogger<ActivationMigrationManager>();
        _catalog = catalog;
        _runtimeClient = shared.RuntimeClient;
        _clusterMembershipService = clusterMembershipService;
        shared.ActivationDirectory.RecordNewTarget(this);

        {
            using var _ = new ExecutionContextSuppressor();
            _membershipUpdatesTask = Task.Factory.StartNew(
                state => ((ActivationMigrationManager)state!).ProcessMembershipUpdates(),
                this,
                CancellationToken.None,
                TaskCreationOptions.None,
                WorkItemGroup.TaskScheduler).Unwrap();
            _membershipUpdatesTask.Ignore();
        }
    }

    /// <summary>
    /// Gets the message center, which is resolved on first use, since it can depend on this migration manager.
    /// </summary>
    private MessageCenter MessageCenter => _messageCenter ??= _runtimeClient.ServiceProvider.GetRequiredService<MessageCenter>();

    public async ValueTask AcceptMigratingGrains(
        List<GrainMigrationPackage> migratingGrains,
        CancellationToken cancellationToken = default)
    {
        var nextPackage = 0;
        try
        {
            var clock = (MigrationClock?)RequestContext.Get(MigrationClock.RequestContextKey);
            RequestContext.Remove(MigrationClock.RequestContextKey);
            for (var i = 0; i < migratingGrains.Count; i++)
            {
                var package = migratingGrains[i];
                package.UnpackRequests(_runtimeClient.ServiceProvider.GetRequiredService<MessageHandlerShared>(), clock);
                migratingGrains[i] = package;

                ValidateRequests(package);
            }

            var activations = new List<ActivationData>();
            var currentActivity = Activity.Current;
            while (nextPackage < migratingGrains.Count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var package = migratingGrains[nextPackage];
                var requests = TakeRequests(package, Silo);

                // The catalog owns the context and cleans up unadmitted requests on failure.
                nextPackage++;
                var context = _catalog.GetOrCreateActivation(package.GrainId, requestContextData: null, package);
                if (context is null && requests is not null)
                {
                    var delivered = 0;
                    try
                    {
                        foreach (var request in requests)
                        {
                            delivered++;
                            MessageCenter.ReceiveMessage(request);
                        }
                    }
                    finally
                    {
                        for (var i = delivered; i < requests.Count; i++)
                        {
                            requests[i].Dispose();
                        }

                        requests.Clear();
                    }
                }

                if (context is ActivationData activation)
                {
                    activations.Add(activation);
                }

                Activity.Current = currentActivity;
            }

            // Wait for all activations to become active or reach a terminal state.
            // This ensures that the activation has completed registration in the directory (or is abandoned) before we return.
            // Otherwise, there could be a race where the original silo removes the activation from its catalog, receives a new message for that activation,
            // and re-activates it before the new activation on this silo has been registered with the directory.
            using var waitActivity = ActivitySources.LifecycleGrainSource.StartActivity(ActivityNames.WaitMigration);
            foreach (var activation in activations)
            {
                await activation.WaitForActivationReadyAsync(cancellationToken);
            }
        }
        finally
        {
            // Validation, cancellation, or a failed earlier package leaves the remainder owned by this invocation.
            for (; nextPackage < migratingGrains.Count; nextPackage++)
            {
                var package = migratingGrains[nextPackage];
                package.MigrationContext.Dispose();
                if (package.Requests is { } requests)
                {
                    foreach (var request in requests)
                    {
                        request.Dispose();
                    }

                    requests.Clear();
                }
            }
        }
    }

    /// <summary>
    /// Ensures that a package carries only requests which target its grain.
    /// </summary>
    private static void ValidateRequests(GrainMigrationPackage package)
    {
        var grainId = package.GrainId;
        if (package.Requests?.Find(request => request.TargetGrain != grainId || request.IsLocalOnly || request.Direction is not (Message.Directions.Request or Message.Directions.OneWay)) is { } invalid)
        {
            throw new ArgumentException($"The migration package for grain {grainId} carries a message which is not a request for the grain: {invalid}", nameof(package));
        }
    }

    /// <summary>
    /// Takes the requests which a package carries, which this silo now owns.
    /// </summary>
    /// <returns>The requests, or <see langword="null"/> if the package carries none.</returns>
    private static List<Message>? TakeRequests(GrainMigrationPackage package, SiloAddress silo)
    {
        if (package.Requests is not { Count: > 0 } requests)
        {
            return null;
        }

        // Requests which expired in transit are dropped, as they are when received.
        requests.RemoveAll(static request =>
        {
            if (!request.IsExpired)
            {
                return false;
            }

            request.Dispose();
            return true;
        });

        foreach (var request in requests)
        {
            // The migration counts as a forward.
            request.TargetSilo = silo;
            request.ForwardCount++;
        }

        return requests;
    }

    public ValueTask RetargetMigratedRequests(List<MigratedRequestRoute> routes, CancellationToken cancellationToken = default)
    {
        foreach (var route in routes)
        {
            _runtimeClient.RetargetCallback(route.SendingGrain, route.Id, route.TargetSilo, route.ForwardCount);
        }

        return default;
    }

    public ActivationMigration StartMigration(SiloAddress targetSilo, GrainAddress source, MigrationContext migrationContext, List<Message>? requests, CancellationToken cancellationToken)
    {
        Debug.Assert(!targetSilo.Equals(source.SiloAddress));
        var grainId = source.GrainId;
        var package = new GrainMigrationPackage { GrainId = grainId, MigrationContext = migrationContext };

        // Bound the context without serializing its values a second time.
        var size = PackageFramingAllowance + migrationContext.GetMaxSerializedSize() + grainId.Type.AsSpan().Length + grainId.Key.AsSpan().Length;
        List<(SiloAddress CallerSilo, MigratedRequestRoute Route)>? routes = null;
        Exception? preparationError = null;
        if (requests is { Count: > 0 } && AcceptsMigratedRequests(targetSilo))
        {
            package.Requests = requests;
            routes = GetRoutes(targetSilo, requests);
            if (!cancellationToken.IsCancellationRequested)
            {
                var shared = _runtimeClient.ServiceProvider.GetRequiredService<MessageHandlerShared>();
                var serializer = shared.GetMessageSerializer();
                try
                {
                    using var buffer = new Orleans.Serialization.Buffers.ArcBufferWriter();
                    var requestsSize = 0L;
                    foreach (var request in requests)
                    {
                        requestsSize += serializer.PrepareRequestBody(shared, buffer, request)
                            + serializer.GetHeaderSizeBound(buffer, request) + Message.LENGTH_HEADER_SIZE + RequestDeadlineFramingAllowance;
                        if (size + requestsSize > MaxBatchBodySize)
                        {
                            break;
                        }
                    }

                    if (size + requestsSize <= MaxBatchBodySize)
                    {
                        size += requestsSize;
                    }
                    else
                    {
                        package.Requests = null;
                        routes = null;
                        LogDebugMigratedRequestsTooLarge(requests.Count, grainId, targetSilo);
                    }
                }
                catch (Exception exception)
                {
                    preparationError = exception;
                }
                finally
                {
                    shared.Return(serializer);
                }
            }
        }

        var migration = new ActivationMigration(this, source, targetSilo, package, requests, routes, size, cancellationToken);
        if (preparationError is not null)
        {
            // Serialization has consumed values. Fail this migration and reject its requests instead of forwarding partially serialized objects.
            migration.TrySend();
            migration.Complete(preparationError);
            return migration;
        }

        migration.RegisterCancellation();
        if (!GetOrCreateWorker(targetSilo).TryWrite(migration))
        {
            migration.Complete(new SiloUnavailableException($"Silo {targetSilo} is no longer active"));
        }

        return migration;
    }

    /// <summary>
    /// Gets whether <paramref name="silo"/> accepts requests together with migrating grains, as silos running an earlier version do not.
    /// </summary>
    private bool AcceptsMigratedRequests(SiloAddress silo)
        => _clusterManifestProvider.Current.Silos.TryGetValue(silo, out var manifest)
            && manifest.Interfaces.TryGetValue(_migrationInterfaceType, out var properties)
            && properties.Properties.TryGetValue(WellKnownGrainInterfaceProperties.Version, out var value)
            && ushort.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            && version >= MigratedRequestsVersion;

    /// <summary>
    /// Gets the routes for retargeting the callers of <paramref name="requests"/> to <paramref name="target"/>.
    /// </summary>
    /// <remarks>
    /// Each request's sending silo holds its callback, unless the caller is an outside client, which tracks the call against its gateway.
    /// Routes are taken now: a caller on this silo shares its request message with its callback, which a later migration of the same request can retarget
    /// while this one awaits acceptance. One-way requests have no pending callback to retarget.
    /// </remarks>
    private static List<(SiloAddress CallerSilo, MigratedRequestRoute Route)>? GetRoutes(SiloAddress target, List<Message> requests)
    {
        List<(SiloAddress CallerSilo, MigratedRequestRoute Route)>? routes = null;
        foreach (var request in requests)
        {
            if (request.Direction is not Message.Directions.OneWay && request.SendingSilo is { } callerSilo)
            {
                (routes ??= []).Add((callerSilo, new(request.SendingGrain, request.Id, target, request.ForwardCount + 1)));
            }
        }

        return routes;
    }

    /// <summary>
    /// Rejects or forwards a migration's requests once its outcome is known and its source activation has left the catalog.
    /// </summary>
    /// <param name="source">The migrating activation's address.</param>
    /// <param name="target">The migration target.</param>
    /// <param name="carried">Whether the migration sent the requests.</param>
    /// <param name="accepted">Whether the target accepted the requests which the migration sent.</param>
    /// <param name="requests">The requests which were queued when the migration started.</param>
    /// <param name="laterRequests">The requests which reached the activation after the migration started.</param>
    internal void DeliverRequests(GrainAddress source, SiloAddress target, bool carried, bool accepted, List<Message>? requests, List<Message>? laterRequests)
    {
        if (requests is not null && carried)
        {
            foreach (var request in requests)
            {
                if (!accepted && request.Direction is Message.Directions.Request)
                {
                    // The target may have taken the request, and sending it may consume it, so it is rejected and its caller can retry it.
                    // The RPC reads its independent managed snapshot, so rejection is independent of a late writer.
                    MessageCenter.SendRejectionResponse(request, Message.RejectionTypes.Transient, "The activation's migration did not deliver the request.", exception: null);
                }

                // A one-way request has no caller to reject, so it is dropped, as one-way requests are when their delivery fails.
            }

            requests = null;
        }

        if (laterRequests is { Count: > 0 })
        {
            if (requests is null)
            {
                requests = laterRequests;
            }
            else
            {
                requests.AddRange(laterRequests);
            }
        }

        if (requests is { Count: > 0 })
        {
            MessageCenter.ProcessRequestsToInvalidActivation(requests, source, target, "The activation migrated.");
        }
    }

    private int MaxBatchBodySize => _maxMessageBodySize - MessageBodyFramingAllowance;

    private async Task ProcessMembershipUpdates()
    {
        await Task.Yield();

        try
        {
            LogDebugMonitoringUpdates();

            var previousSnapshot = _clusterMembershipService.CurrentSnapshot;
            await foreach (var snapshot in _clusterMembershipService.MembershipUpdates)
            {
                try
                {
                    var diff = snapshot.CreateUpdate(previousSnapshot);
                    previousSnapshot = snapshot;
                    foreach (var change in diff.Changes)
                    {
                        if (change.Status.IsTerminating())
                        {
                            RemoveWorker(change.SiloAddress);
                        }
                    }
                }
                catch (Exception exception)
                {
                    LogErrorProcessingMembershipUpdates(exception);
                }
            }
        }
        finally
        {
            LogDebugNoLongerMonitoring();
        }
    }

    private async Task PumpMigrationQueue(SiloAddress targetSilo, Channel<ActivationMigration> workItems)
    {
        try
        {
            var remote = _grainFactory.GetSystemTarget<IActivationMigrationManagerSystemTarget>(Constants.ActivationMigratorType, targetSilo);
            await Task.Yield();

            LogDebugStartingWorker(targetSilo);

            var maxBatchSize = MaxBatchBodySize;
            var items = new List<ActivationMigration>();
            var reader = workItems.Reader;
            while (await reader.WaitToReadAsync())
            {
                // The invocation owns its batch even if it times out before the transport serializes it.
                var batch = new List<GrainMigrationPackage>();
                var clock = MigrationClock.Start();
                var carriesRequests = false;
                try
                {
                    // Collect a batch of work items within the message size limit.
                    long batchSize = 0;
                    while (batch.Count < MaxBatchSize
                        && reader.TryPeek(out var next)
                        && (batch.Count == 0 || batchSize + next.Size <= maxBatchSize)
                        && reader.TryRead(out var workItem))
                    {
                        if (!workItem.TrySend())
                        {
                            // The migration was canceled before it was sent.
                            continue;
                        }

                        items.Add(workItem);
                        var snapshot = workItem.CreateSnapshot(_runtimeClient.ServiceProvider.GetRequiredService<MessageHandlerShared>(), clock);
                        batch.Add(snapshot);
                        carriesRequests |= snapshot.RequestFrames is not null;
                        batchSize += workItem.Size;
                    }

                    if (items.Count == 0)
                    {
                        continue;
                    }

                    if (carriesRequests)
                    {
                        RequestContext.Set(MigrationClock.RequestContextKey, clock);
                    }
                    else
                    {
                        // Request-free batches can target older silos, so inherited migration metadata stays local.
                        RequestContext.Remove(MigrationClock.RequestContextKey);
                    }

                    try
                    {
                        await remote.AcceptMigratingGrains(batch, _shuttingDownCts.Token);
                    }
                    finally
                    {
                        RequestContext.Remove(MigrationClock.RequestContextKey);
                    }

                    CompleteAfterRetargeting(items);

                    LogDebugMigratedActivations(items.Count, targetSilo);
                }
                catch (Exception exception)
                {
                    if (!_shuttingDownCts.IsCancellationRequested)
                    {
                        LogErrorMigratingActivations(exception, items.Count, targetSilo);
                    }

                    // The target may have taken the requests which the batch carried, so they are rejected rather than resent.
                    foreach (var item in items)
                    {
                        item.Complete(exception);
                    }

                    // If the silo is terminating, we should stop trying to migrate activations to it.
                    if (_clusterMembershipService.CurrentSnapshot.GetSiloStatus(targetSilo).IsTerminating())
                    {
                        break;
                    }
                }
                finally
                {
                    items.Clear();
                }
            }

            // Remove ourselves and clean up.
            RemoveWorker(targetSilo);
        }
        finally
        {
            LogDebugExitingWorker(targetSilo);
        }
    }

    /// <summary>
    /// Completes the work items, after retargeting the callers of the requests which the target accepted.
    /// </summary>
    /// <remarks>
    /// Retargeting is best-effort. A work item whose requests were accepted completes once its callers' retargeting completes, fails, or its deadline passes,
    /// so a retiring source normally departs after its callers follow their requests. A caller which was not retargeted fails its call when the source departs.
    /// Other work items complete immediately, without waiting for unrelated callers. The pump continues with the next batch meanwhile.
    /// </remarks>
    private void CompleteAfterRetargeting(List<ActivationMigration> items)
    {
        Dictionary<SiloAddress, List<MigratedRequestRoute>>? routesBySilo = null;
        List<ActivationMigration>? awaitingRetarget = null;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            item.SetAccepted();
            if (item.Routes is not { } routes)
            {
                continue;
            }

            (awaitingRetarget ??= []).Add(item);
            foreach (var (callerSilo, route) in routes)
            {
                routesBySilo ??= [];
                if (!routesBySilo.TryGetValue(callerSilo, out var siloRoutes))
                {
                    routesBySilo[callerSilo] = siloRoutes = [];
                }

                siloRoutes.Add(route);
            }
        }

        if (awaitingRetarget is not null)
        {
            RetargetThenCompleteAsync(awaitingRetarget, routesBySilo!).Ignore();
        }

        async Task RetargetThenCompleteAsync(List<ActivationMigration> items, Dictionary<SiloAddress, List<MigratedRequestRoute>> routesBySilo)
        {
            try
            {
                var retargets = new List<Task>(routesBySilo.Count);
                foreach (var (callerSilo, routes) in routesBySilo)
                {
                    if (callerSilo.Equals(Silo))
                    {
                        retargets.Add(RetargetMigratedRequests(routes).AsTask());
                    }
                    else
                    {
                        var remote = _grainFactory.GetSystemTarget<IActivationMigrationManagerSystemTarget>(Constants.ActivationMigratorType, callerSilo);
                        retargets.Add(remote.RetargetMigratedRequests(routes, _shuttingDownCts.Token).AsTask());
                    }
                }

                await Task.WhenAll(retargets);
            }
            catch (Exception exception)
            {
                // A caller which was not retargeted, such as one running an earlier version, fails its call when the source departs.
                LogDebugRetargetMigratedRequestsFailed(exception, items.Count);
            }
            finally
            {
                foreach (var item in items)
                {
                    item.Complete();
                }
            }
        }
    }

    private ChannelWriter<ActivationMigration> GetOrCreateWorker(SiloAddress targetSilo)
    {
        if (!_workers.TryGetValue(targetSilo, out var entry))
        {
            lock (_lock)
            {
                if (!_workers.TryGetValue(targetSilo, out entry))
                {
                    using var _ = new ExecutionContextSuppressor();
                    var channel = Channel.CreateUnbounded<ActivationMigration>();
                    var pumpTask = Task.Factory.StartNew(
                        () => PumpMigrationQueue(targetSilo, channel),
                        CancellationToken.None,
                        TaskCreationOptions.None,
                        WorkItemGroup.TaskScheduler).Unwrap();
                    pumpTask.Ignore();

                    entry = (pumpTask, channel);
                    var didAdd = _workers.TryAdd(targetSilo, entry);
                    Debug.Assert(didAdd);
                }
            }
        }

        return entry.WorkItemChannel.Writer;
    }

    private void RemoveWorker(SiloAddress targetSilo)
    {
        if (_workers.TryRemove(targetSilo, out var entry))
        {
            LogDebugTargetSilo(targetSilo);

            entry.WorkItemChannel.Writer.TryComplete();

            var exception = new SiloUnavailableException($"Silo {targetSilo} is no longer active");
            while (entry.WorkItemChannel.Reader.TryRead(out var item))
            {
                item.Complete(exception);
            }
        }
    }

    private Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    private async Task StopAsync(CancellationToken cancellationToken)
    {
        var workerTasks = new List<Task>();
        foreach (var (_, value) in _workers)
        {
            value.WorkItemChannel.Writer.TryComplete();
            workerTasks.Add(value.PumpTask);
        }

        try
        {
            _shuttingDownCts.Cancel();
        }
        catch (Exception exception)
        {
            LogWarningSignalShutdownError(exception);
        }

        await Task.WhenAll(workerTasks).WaitAsync(cancellationToken).SuppressThrowing();
    }

    void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
    {
        lifecycle.Subscribe(
            nameof(ActivationMigrationManager),
            ServiceLifecycleStage.RuntimeGrainServices,
            QueuedStart,
            QueuedStop);

        Task QueuedStart(CancellationToken ct) => this.RunOrQueueTask(() => StartAsync(ct));
        Task QueuedStop(CancellationToken ct) => this.RunOrQueueTask(() => StopAsync(ct));
    }

    // Log value types
    private readonly struct SiloAddressLogValue
    {
        private readonly SiloAddress _silo;
        public SiloAddressLogValue(SiloAddress silo) => _silo = silo;
        public override string ToString() => _silo.ToStringWithHashCode();
    }

    // Logger methods at end of class
    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Monitoring cluster membership updates")]
    private partial void LogDebugMonitoringUpdates();

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error processing cluster membership updates"
    )]
    private partial void LogErrorProcessingMembershipUpdates(Exception exception);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "No longer monitoring cluster membership updates")]
    private partial void LogDebugNoLongerMonitoring();

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Starting migration worker for target silo {SiloAddress}")]
    private partial void LogDebugStartingWorker(SiloAddress siloAddress);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Migrated {Count} activations to target silo {SiloAddress}")]
    private partial void LogDebugMigratedActivations(int count, SiloAddress siloAddress);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error while migrating {Count} grain activations to {SiloAddress}")]
    private partial void LogErrorMigratingActivations(Exception exception, int count, SiloAddress siloAddress);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Exiting migration worker for target silo {SiloAddress}")]
    private partial void LogDebugExitingWorker(SiloAddress siloAddress);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Target silo {SiloAddress} is no longer active, so this migration activation worker is terminating"
    )]
    private partial void LogDebugTargetSilo(SiloAddress siloAddress);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Error signaling shutdown"
    )]
    private partial void LogWarningSignalShutdownError(Exception exception);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "The size bound for {Count} queued requests for migrating grain {GrainId} exceeds the migration message budget, so they are forwarded to {SiloAddress}."
    )]
    private partial void LogDebugMigratedRequestsTooLarge(int count, GrainId grainId, SiloAddress siloAddress);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Retargeting the callers of {Count} migrated grains failed. Callers which were not retargeted fail their calls when this silo departs."
    )]
    private partial void LogDebugRetargetMigratedRequestsFailed(Exception exception, int count);
}
