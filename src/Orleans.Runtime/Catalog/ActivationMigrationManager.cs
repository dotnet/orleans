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
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Invocation;

namespace Orleans.Runtime;

/// <summary>
/// Remote interface for migrating grain activations to a silo.
/// </summary>
/// <remarks>
/// Version 1 adds <see cref="AcceptMigratingGrainsWithRequests"/> and <see cref="RetargetMigratedRequests"/>.
/// A silo sends queued requests only to silos whose manifest lists version 1 or later of this interface.
/// </remarks>
[Version(ActivationMigrationManager.MigratedRequestsVersion)]
internal interface IActivationMigrationManagerSystemTarget : ISystemTarget
{
    /// <summary>
    /// Accepts migrating grains on a best-effort basis.
    /// </summary>
    [Alias("29E9E63F")]
    ValueTask AcceptMigratingGrains(
        [Immutable] List<GrainMigrationPackage> migratingGrains,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Accepts migrating grains on a best-effort basis, together with the requests which were queued for them.
    /// </summary>
    /// <param name="migratingGrains">The migrating grains.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// For each package, whether the grain's new activation on this silo took ownership of the package's requests.
    /// Each activation queues its requests before it can receive any other request. When it did not take ownership, none of the package's requests were delivered.
    /// </returns>
    [Alias("AcceptMigratingGrainsWithRequests")]
    ValueTask<bool[]> AcceptMigratingGrainsWithRequests(
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
    /// The requests which were queued for the grain when it migrated, if any, in the framing which connections use.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only <see cref="IActivationMigrationManagerSystemTarget.AcceptMigratingGrainsWithRequests"/> carries requests.
    /// </para>
    /// <para>
    /// The requests are serialized once, when the package is created, which fixes their remaining time to live and the package's size.
    /// Time which the package spends in transit does not count against the requests' time to live.
    /// The destination reads them as a connection would, so their bodies are decoded only when they are invoked.
    /// </para>
    /// </remarks>
    [Id(2)]
    public byte[]? Requests { get; set; }
}

/// <summary>
/// Functionality for migrating an activation to a new location.
/// </summary>
internal interface IActivationMigrationManager
{
    /// <summary>
    /// Starts migrating an activation to the specified target, together with the requests which were queued for it.
    /// </summary>
    /// <param name="target">The migration target.</param>
    /// <param name="source">The address of the migrating activation.</param>
    /// <param name="migrationContext">Information about the grain being migrated, which will be consumed by the new activation.</param>
    /// <param name="requests">The requests which were queued for the activation, if any. The migration takes ownership of them.</param>
    /// <param name="cancellationToken">
    /// A token which cancels the migration if it has not been sent yet. A migration which has been sent completes when the target responds,
    /// since the target may already have accepted it.
    /// </param>
    /// <returns>The migration.</returns>
    ActivationMigration StartMigration(SiloAddress target, GrainAddress source, MigrationContext migrationContext, List<Message>? requests, CancellationToken cancellationToken);
}

/// <summary>
/// The migration of an activation, which owns the requests which were queued for the activation.
/// </summary>
/// <remarks>
/// The activation transfers its queued requests when it starts the migration, and remains in the catalog until the migration completes,
/// so that requests which reach this silo for the grain reach it. It then leaves the catalog and transfers the requests which reached it meanwhile
/// through <see cref="Deliver"/>, which forwards the requests which the target did not take, followed by the later requests.
/// The activation does not access the requests afterwards.
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
    private bool _requestsRetained;

    /// <summary>
    /// Completes when the target has acknowledged the migration, and the callers of the requests which it took have been retargeted.
    /// Faults if the migration failed or was canceled before it was sent.
    /// </summary>
    public Task Completion => _completion.Task;

    internal GrainMigrationPackage Package { get; } = package;

    /// <summary>
    /// The routes for retargeting the callers of the requests which the migration carries.
    /// </summary>
    internal List<(SiloAddress CallerSilo, MigratedRequestRoute Route)>? Routes { get; } = routes;

    /// <summary>
    /// The serialized size of the package, bounded from above for its migration context.
    /// </summary>
    internal long Size { get; } = size;

    internal CancellationToken CancellationToken { get; } = cancellationToken;

    /// <summary>
    /// Forwards the requests which the target did not take, followed by <paramref name="laterRequests"/>, once the activation has left the catalog.
    /// </summary>
    /// <param name="laterRequests">The requests which reached the activation after the migration started, if any.</param>
    /// <remarks>The activation calls this once, after <see cref="Completion"/> has completed.</remarks>
    public void Deliver(List<Message>? laterRequests)
    {
        Debug.Assert(Completion.IsCompleted, "The migration delivers its requests once its outcome is known.");
        manager.DeliverRequests(source, target, Package.Requests, _requestsRetained, requests, laterRequests);
        requests = null;
    }

    /// <summary>
    /// Records the migration's outcome and completes it.
    /// </summary>
    /// <param name="requestsRetained">
    /// Whether the migration did not deliver the requests which it carried, if any, so that this silo forwards them.
    /// Otherwise, the target owns them, or may own them, and they are not resent.
    /// </param>
    /// <param name="exception">The error which prevented the migration, if any.</param>
    internal void SetOutcome(bool requestsRetained, Exception? exception = null)
    {
        _requestsRetained = requestsRetained;
        if (exception is null)
        {
            _completion.TrySetResult();
        }
        else
        {
            _completion.TrySetException(exception);
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

    private readonly ConcurrentDictionary<SiloAddress, (Task PumpTask, Channel<ActivationMigration> WorkItemChannel)> _workers = new();
    private readonly CancellationTokenSource _shuttingDownCts = new();
    private readonly ILogger<ActivationMigrationManager> _logger;
    private readonly IInternalGrainFactory _grainFactory;
    private readonly Catalog _catalog;
    private readonly InsideRuntimeClient _runtimeClient;
    private readonly IClusterMembershipService _clusterMembershipService;
    private readonly IClusterManifestProvider _clusterManifestProvider;
    private readonly GrainInterfaceType _migrationInterfaceType;
    private MessageHandlerShared? _messageHandlerShared;
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
    /// Gets the shared messaging services, which are resolved on first use, since they depend on the message center, which can depend on this migration manager.
    /// </summary>
    private MessageHandlerShared MessageHandlerShared => _messageHandlerShared ??= _runtimeClient.ServiceProvider.GetRequiredService<MessageHandlerShared>();

    public async ValueTask AcceptMigratingGrains(
        List<GrainMigrationPackage> migratingGrains,
        CancellationToken cancellationToken = default)
        => await AcceptMigratingGrainsWithRequests(migratingGrains, cancellationToken);

    public async ValueTask<bool[]> AcceptMigratingGrainsWithRequests(
        List<GrainMigrationPackage> migratingGrains,
        CancellationToken cancellationToken = default)
    {
        var requestsAccepted = new bool[migratingGrains.Count];
        var activations = new List<ActivationData>();
        var currentActivity = Activity.Current;
        for (var i = 0; i < migratingGrains.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var package = migratingGrains[i];
            // A grain which is already active here cannot take the requests, so they are not read.
            var requests = package.Requests is { Length: > 0 } frames && _activations.FindTarget(package.GrainId) is null
                ? ReadRequests(package.GrainId, frames)
                : null;

            // The new activation reads its previous address when it rehydrates, which happens after it queues the requests, so it is read here.
            GrainAddress? previousAddress = null;
            if (requests is not null)
            {
                package.MigrationContext.TryGetValue(ActivationData.GrainAddressMigrationContextKey, out previousAddress);
            }

            // If the activation does not exist, create it and provide it with the migration context and queued requests while doing so.
            // If the activation already exists or cannot be created, it is too late to perform migration, so ignore the request.
            var context = _catalog.GetOrCreateActivation(package.GrainId, requestContextData: null, package.MigrationContext, requests, previousAddress, out var accepted);
            requestsAccepted[i] = accepted;

            if (!accepted && requests is not null)
            {
                // The source delivers the requests instead, so these copies release their buffers.
                foreach (var request in requests)
                {
                    request.Dispose();
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

        return requestsAccepted;
    }

    private List<Message>? ReadRequests(GrainId grainId, byte[] frames)
    {
        try
        {
            var requests = MessageFrames.Read(MessageHandlerShared, frames);
            foreach (var request in requests)
            {
                // The migration counts as a forward.
                request.TargetSilo = Silo;
                request.ForwardCount++;
            }

            return requests;
        }
        catch (Exception exception)
        {
            // The source delivers the requests instead.
            LogDebugMigratedRequestsNotReadable(exception, grainId);
            return null;
        }
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
        var package = new GrainMigrationPackage { GrainId = source.GrainId, MigrationContext = migrationContext };
        long size = 0;
        List<(SiloAddress CallerSilo, MigratedRequestRoute Route)>? routes = null;
        if (requests is { Count: > 0 } && AcceptsMigratedRequests(targetSilo))
        {
            PackageRequests(targetSilo, ref package, requests, out routes, out size);
        }

        var migration = new ActivationMigration(this, source, targetSilo, package, requests, routes, size, cancellationToken);
        if (!GetOrCreateWorker(targetSilo).TryWrite(migration))
        {
            migration.SetOutcome(requestsRetained: true, new SiloUnavailableException($"Silo {targetSilo} is no longer active"));
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
    /// Serializes queued requests into the form which a migration carries, and records the routes for retargeting their callers.
    /// </summary>
    /// <param name="target">The migration target.</param>
    /// <param name="package">The migration package, which carries the requests unless they do not fit in a message or none can be serialized.</param>
    /// <param name="requests">The queued requests.</param>
    /// <param name="routes">The routes for retargeting the requests' callers.</param>
    /// <param name="size">The serialized size of the complete package, including its framing within a batch.</param>
    private void PackageRequests(
        SiloAddress target,
        ref GrainMigrationPackage package,
        List<Message> requests,
        out List<(SiloAddress CallerSilo, MigratedRequestRoute Route)>? routes,
        out long size)
    {
        var grainId = package.GrainId;
        routes = null;
        size = 0;

        // Serializing the migration context releases its buffer, so the package's size is bounded using the context's size bound rather than measured.
        var fixedSize = PackageFramingAllowance + package.MigrationContext.GetMaxSerializedSize() + grainId.Type.AsSpan().Length + grainId.Key.AsSpan().Length;
        var maxSize = MaxBatchBodySize - fixedSize;
        using var buffer = new ArcBufferWriter();
        var written = 0;
        while (written < requests.Count)
        {
            var request = requests[written];
            try
            {
                MessageFrames.Write(MessageHandlerShared, buffer, request);
            }
            catch (Exception exception)
            {
                // Serializing the request may have consumed some of its values. Forwarding it would serialize the same values with the same codecs,
                // so it could not be delivered either way, and it fails instead.
                LogDebugMigratedRequestNotSerializable(exception, request.Id, grainId);
                requests.RemoveAt(written);
                FailRequest(request, exception);
                continue;
            }

            written++;
            if (buffer.Length > maxSize)
            {
                // Requests which fit in their own messages can exceed the message size limit together, or with the migration context and its keys.
                LogDebugMigratedRequestsTooLarge(requests.Count, grainId, target);
                ReplaceWithCopies(requests, ReadFrames(buffer));
                routes = null;
                return;
            }

            // Each request's sending silo holds its callback, unless the caller is an outside client, which tracks the call against its gateway.
            // Routes are taken from the sealed requests now: a caller on this silo shares its request message with its callback,
            // which a later migration of the same request can retarget while this one awaits acceptance.
            // One-way requests have no pending callback to retarget.
            if (request.Direction is not Message.Directions.OneWay && request.SendingSilo is { } callerSilo)
            {
                (routes ??= []).Add((callerSilo, new(request.SendingGrain, request.Id, target, request.ForwardCount + 1)));
            }
        }

        if (written == 0)
        {
            return;
        }

        // The requests are serialized once, here, which fixes their size and their remaining time to live.
        package.Requests = ReadFrames(buffer);
        size = fixedSize + package.Requests.Length;

        static byte[] ReadFrames(ArcBufferWriter buffer)
        {
            var frames = new byte[buffer.Length];
            buffer.Peek(frames);
            return frames;
        }
    }

    private void FailRequest(Message request, Exception exception)
    {
        if (request.Direction is Message.Directions.Request)
        {
            try
            {
                _runtimeClient.SendResponse(request, Response.FromException(exception));
            }
            finally
            {
                // A request which this silo received still holds its serialized body. A request from a caller on this silo shares its body with the caller's pending call, so it is left intact.
                request.ReleaseBodyBuffer();
            }
        }
        else
        {
            request.Dispose();
        }
    }

    /// <summary>
    /// Replaces the first requests with equivalent requests read from the frames which serializing them produced.
    /// </summary>
    /// <remarks>
    /// Serializing a request can release the storage of some of its values, such as pooled buffers, so a request which was serialized but is not delivered
    /// by the migration is forwarded as a request read back from its frame, which holds its serialized body, as a received request does.
    /// </remarks>
    private void ReplaceWithCopies(List<Message> requests, byte[] frames)
    {
        List<Message> copies;
        try
        {
            copies = MessageFrames.Read(MessageHandlerShared, frames);
        }
        catch (Exception exception)
        {
            // This silo wrote the frames, so this is not expected. The requests may have lost the storage of their values, so they fail.
            LogWarningMigratedRequestsNotRestorable(exception, requests.Count);
            foreach (var request in requests)
            {
                FailRequest(request, exception);
            }

            requests.Clear();
            return;
        }

        Debug.Assert(copies.Count <= requests.Count);
        for (var i = 0; i < copies.Count; i++)
        {
            // A request which this silo received still holds its serialized body, which the copy duplicates.
            requests[i].ReleaseBodyBuffer();
            requests[i] = copies[i];
        }
    }

    /// <summary>
    /// Disposes of a migration's requests once its outcome is known and its source activation has left the catalog.
    /// </summary>
    /// <param name="source">The migrating activation's address.</param>
    /// <param name="target">The migration target.</param>
    /// <param name="frames">The serialized requests which the migration carried, if any.</param>
    /// <param name="requestsRetained">Whether the migration did not deliver the requests which it carried, if any.</param>
    /// <param name="requests">The requests which were queued when the migration started.</param>
    /// <param name="laterRequests">The requests which reached the activation after the migration started.</param>
    internal void DeliverRequests(GrainAddress source, SiloAddress target, byte[]? frames, bool requestsRetained, List<Message>? requests, List<Message>? laterRequests)
    {
        if (requests is not null)
        {
            if (requestsRetained)
            {
                if (frames is not null)
                {
                    ReplaceWithCopies(requests, frames);
                }
            }
            else
            {
                // The target owns the requests, or may own them. Requests which this silo received still hold their serialized bodies, which the migration copied.
                // Requests from callers on this silo share their body with the caller's pending call, so they are left intact.
                foreach (var request in requests)
                {
                    request.ReleaseBodyBuffer();
                }

                requests = null;
            }
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
            _runtimeClient.MessageCenter.ProcessRequestsToInvalidActivation(requests, source, target, "The activation migrated.");
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
            var batch = new List<GrainMigrationPackage>();
            var reader = workItems.Reader;
            while (await reader.WaitToReadAsync())
            {
                try
                {
                    // Collect a batch of work items. Packages which carry requests travel together, within the message size limit, which their measured sizes respect.
                    // Other packages travel together as before.
                    long batchSize = 0;
                    var carriesRequests = reader.TryPeek(out var first) && first.Package.Requests is not null;
                    while (batch.Count < MaxBatchSize
                        && reader.TryPeek(out var next)
                        && (next.Package.Requests is not null) == carriesRequests
                        && (!carriesRequests || batchSize + next.Size <= maxBatchSize)
                        && reader.TryRead(out var workItem))
                    {
                        if (workItem.CancellationToken.IsCancellationRequested)
                        {
                            // The migration has not been sent, so its requests remain with this silo.
                            workItem.SetOutcome(requestsRetained: true, new OperationCanceledException(workItem.CancellationToken));
                            continue;
                        }

                        items.Add(workItem);
                        batch.Add(workItem.Package);
                        batchSize += workItem.Size;
                    }

                    if (items.Count == 0)
                    {
                        continue;
                    }

                    // Attempt to migrate the batch.
                    if (!carriesRequests)
                    {
                        await remote.AcceptMigratingGrains(batch, _shuttingDownCts.Token);
                        foreach (var item in items)
                        {
                            item.SetOutcome(requestsRetained: true);
                        }
                    }
                    else
                    {
                        var requestsAccepted = await remote.AcceptMigratingGrainsWithRequests(batch, _shuttingDownCts.Token);
                        if (requestsAccepted.Length != items.Count)
                        {
                            throw new InvalidOperationException($"Expected {items.Count} results from {targetSilo} but received {requestsAccepted.Length}.");
                        }

                        CompleteAfterRetargeting(items, requestsAccepted);
                    }

                    LogDebugMigratedActivations(items.Count, targetSilo);
                }
                catch (Exception exception)
                {
                    if (!_shuttingDownCts.IsCancellationRequested)
                    {
                        LogErrorMigratingActivations(exception, items.Count, targetSilo);
                    }

                    // The target may have accepted the batch even though this silo did not observe it.
                    foreach (var item in items)
                    {
                        // The target may have accepted the batch even though this silo did not observe it, so requests which it carried are not resent.
                        item.SetOutcome(requestsRetained: item.Package.Requests is null, exception);
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
                    batch.Clear();
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
    /// A work item whose requests were accepted completes only after its callers acknowledge, so a retiring source departs only after its callers follow their requests.
    /// Other work items complete immediately, so that declined requests are forwarded without waiting for unrelated callers.
    /// The pump continues with the next batch meanwhile.
    /// </remarks>
    private void CompleteAfterRetargeting(List<ActivationMigration> items, bool[] requestsAccepted)
    {
        Dictionary<SiloAddress, List<MigratedRequestRoute>>? routesBySilo = null;
        List<ActivationMigration>? awaitingRetarget = null;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (!requestsAccepted[i])
            {
                item.SetOutcome(requestsRetained: true);
                continue;
            }

            if (item.Routes is not { } routes)
            {
                item.SetOutcome(requestsRetained: false);
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
                    item.SetOutcome(requestsRetained: false);
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
                item.SetOutcome(requestsRetained: true, exception);
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
        Message = "{Count} queued requests for migrating grain {GrainId} do not fit in a message, so they are forwarded instead of migrating with it to {SiloAddress}."
    )]
    private partial void LogDebugMigratedRequestsTooLarge(int count, GrainId grainId, SiloAddress siloAddress);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Request {MessageId} for migrating grain {GrainId} cannot be serialized, so it fails."
    )]
    private partial void LogDebugMigratedRequestNotSerializable(Exception exception, CorrelationId messageId, GrainId grainId);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Declined the queued requests of migrating grain {GrainId} because they cannot be read. The source forwards them instead."
    )]
    private partial void LogDebugMigratedRequestsNotReadable(Exception exception, GrainId grainId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "{Count} requests which stayed with this silo when their activation migrated cannot be read back, so they fail."
    )]
    private partial void LogWarningMigratedRequestsNotRestorable(Exception exception, int count);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Some callers of {Count} migrated grains did not acknowledge their retargeted requests"
    )]
    private partial void LogDebugRetargetMigratedRequestsFailed(Exception exception, int count);
}
