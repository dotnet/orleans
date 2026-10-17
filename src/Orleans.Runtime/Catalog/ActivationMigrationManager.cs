using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Diagnostics;
using Orleans.Internal;
using Orleans.Metadata;
using Orleans.Runtime.Internal;
using Orleans.Runtime.Scheduler;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Session;

namespace Orleans.Runtime;

/// <summary>
/// Remote interface for migrating grain activations to a silo.
/// </summary>
internal interface IActivationMigrationManagerSystemTarget : ISystemTarget
{
    /// <summary>
    /// Accepts migrating grains on a best-effort basis.
    /// </summary>
    [Alias("29E9E63F")]
    ValueTask AcceptMigratingGrains(
        [Immutable] List<GrainMigrationPackage> migratingGrains,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Remote interface for migrating grain activations to a silo together with the requests which were queued for them.
/// </summary>
/// <remarks>
/// A silo which implements this interface lists it in its manifest, so other silos send queued requests only to silos which accept them.
/// </remarks>
internal interface IMigratingRequestsSystemTarget : ISystemTarget
{
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
/// A request which was queued for an activation when it migrated.
/// </summary>
[GenerateSerializer]
internal sealed class MigratedRequest
{
    [Id(0)]
    public uint Headers { get; set; }

    [Id(1)]
    public CorrelationId Id { get; set; }

    [Id(2)]
    public GrainId SendingGrain { get; set; }

    [Id(3)]
    public SiloAddress? SendingSilo { get; set; }

    // Ages until the request is serialized, so the destination receives the remaining budget, as it does for forwarded messages.
    [NonSerialized]
    private CoarseStopwatch _timeToExpiry;

    [Id(4)]
    public long? TimeToLiveMilliseconds
    {
        get => _timeToExpiry.IsDefault ? null : -_timeToExpiry.ElapsedMilliseconds;
        set => _timeToExpiry = value is { } timeToLive ? CoarseStopwatch.StartNew(-timeToLive) : default;
    }

    [Id(5)]
    public GrainInterfaceType InterfaceType { get; set; }

    [Id(6)]
    public ushort InterfaceVersion { get; set; }

    [Id(7)]
    public List<GrainAddressCacheUpdate>? CacheInvalidationHeader { get; set; }

    /// <summary>
    /// The serialized request context.
    /// </summary>
    [Id(8)]
    public byte[]? RequestContext { get; set; }

    /// <summary>
    /// The serialized request body, in the format which messages carry their bodies.
    /// </summary>
    [Id(9)]
    public byte[]? Body { get; set; }

    /// <summary>
    /// Captures a queued request for migration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A body which this silo received and has not decoded is carried as its original bytes, as ordinary forwarding does,
    /// so that fields which only the destination understands are preserved.
    /// </para>
    /// <para>
    /// The request context and any decoded body are serialized once, here. Serializing some values, such as <see cref="PooledBuffer"/>,
    /// releases their storage, so the captured request holds only serialized data, which can be measured and sent repeatedly.
    /// If the request is not migrated, <see cref="RestoreTo"/> restores the message from that data.
    /// </para>
    /// </remarks>
    public static MigratedRequest FromMessage(Message message, SerializerSessionPool sessionPool) => new()
    {
        Headers = (uint)message.Headers,
        Id = message.Id,
        SendingGrain = message.SendingGrain,
        SendingSilo = message.SendingSilo,
        _timeToExpiry = message._timeToExpiry,
        InterfaceType = message.InterfaceType,
        InterfaceVersion = message.InterfaceVersion,
        CacheInvalidationHeader = message.CacheInvalidationHeader,
        RequestContext = message.RequestContextData is { } requestContext ? SerializeBody(requestContext, sessionPool) : null,
        Body = message._bodyObject switch
        {
            Orleans.Runtime.Messaging.MessageReadRequest readRequest => readRequest.Body.ToArray(),
            null => null,
            var body => SerializeBody(body, sessionPool),
        },
    };

    /// <summary>
    /// Restores the values which <see cref="FromMessage"/> serialized to <paramref name="message"/>, so that the message can be delivered in another way.
    /// </summary>
    /// <remarks>
    /// Serialization may have released the storage of the message's values, so the message receives copies decoded from the serialized data.
    /// A body which this silo received is unchanged, since it was copied without being decoded.
    /// </remarks>
    public void RestoreTo(Message message, SerializerSessionPool sessionPool)
    {
        if (RequestContext is not null)
        {
            message.RequestContextData = (Dictionary<string, object>?)DeserializeBody(RequestContext, sessionPool);
        }

        if (Body is not null && message._bodyObject is not Orleans.Runtime.Messaging.MessageReadRequest)
        {
            message.BodyObject = DeserializeBody(Body, sessionPool);
        }
    }

    private static byte[] SerializeBody(object body, SerializerSessionPool sessionPool)
    {
        using var session = sessionPool.GetSession();
        var writer = Writer.CreatePooled(session);
        try
        {
            session.CodecProvider.GetCodec(body.GetType()).WriteField(ref writer, 0, null, body);
            writer.Commit();
            return writer.Output.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static object? DeserializeBody(byte[]? body, SerializerSessionPool sessionPool)
    {
        if (body is null)
        {
            return null;
        }

        using var session = sessionPool.GetSession();
        var reader = Reader.Create(body, session);
        var field = reader.ReadFieldHeader();
        return session.CodecProvider.GetCodec(field.FieldType!).ReadValue(ref reader, field);
    }

    /// <summary>
    /// Recreates the request for delivery on the silo which accepted it, counting the migration as a forward.
    /// </summary>
    public Message ToMessage(GrainId targetGrain, SiloAddress targetSilo, SerializerSessionPool sessionPool)
    {
        var message = new Message
        {
            Headers = (Message.PackedHeaders)Headers,
            Id = Id,
            SendingGrain = SendingGrain,
            SendingSilo = SendingSilo,
            TargetGrain = targetGrain,
            TargetSilo = targetSilo,
            InterfaceType = InterfaceType,
            InterfaceVersion = InterfaceVersion,
            CacheInvalidationHeader = CacheInvalidationHeader,
            RequestContextData = (Dictionary<string, object>?)DeserializeBody(RequestContext, sessionPool),
            BodyObject = DeserializeBody(Body, sessionPool),
        };

        if (TimeToLiveMilliseconds is { } timeToLive)
        {
            message.SetTimeToLiveMilliseconds(timeToLive);
        }
        else
        {
            message.SetInfiniteTimeToLive();
        }

        message.ForwardCount++;
        return message;
    }
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
    /// Only <see cref="IMigratingRequestsSystemTarget"/> carries requests.
    /// </remarks>
    [Id(2)]
    public List<MigratedRequest>? Requests { get; set; }
}

/// <summary>
/// What became of the requests which a migration carried.
/// </summary>
internal enum MigratedRequestsOutcome : byte
{
    /// <summary>
    /// The requests were not delivered, so the source retains them.
    /// </summary>
    Retained,

    /// <summary>
    /// The grain's new activation took ownership of the requests, and their callers were retargeted to it.
    /// </summary>
    Accepted,

    /// <summary>
    /// The target may have taken ownership of the requests. They are not resent, since the target may be executing them.
    /// </summary>
    Unknown,
}

/// <summary>
/// The outcome of migrating an activation.
/// </summary>
/// <param name="Migrated">Whether the target accepted the activation.</param>
/// <param name="Requests">What became of the requests which the migration carried.</param>
/// <param name="Exception">The error which prevented the migration, if any.</param>
internal readonly record struct MigrationResult(bool Migrated, MigratedRequestsOutcome Requests, Exception? Exception);

/// <summary>
/// Functionality for migrating an activation to a new location.
/// </summary>
internal interface IActivationMigrationManager
{
    /// <summary>
    /// Attempts to migrate a grain to the specified target, together with the requests which were queued for it.
    /// </summary>
    /// <param name="target">The migration target.</param>
    /// <param name="grainId">The grain being migrated.</param>
    /// <param name="migrationContext">Information about the grain being migrated, which will be consumed by the new activation.</param>
    /// <param name="requests">The requests which were queued for the grain, if any.</param>
    /// <param name="carriesRequests">
    /// Whether the migration carries <paramref name="requests"/>. A migration carries them only when <paramref name="target"/> accepts queued requests
    /// and they fit in a message. Otherwise, the caller retains them.
    /// </param>
    /// <returns>The outcome of the migration.</returns>
    /// <remarks>
    /// When the target accepts the requests, their callers are retargeted to it before this completes, so that their calls survive the source's departure.
    /// </remarks>
    ValueTask<MigrationResult> MigrateAsync(SiloAddress target, GrainId grainId, MigrationContext migrationContext, List<Message>? requests, out bool carriesRequests);
}

/// <summary>
/// Migrates grain activations to target hosts and handles migration requests from other hosts.
/// </summary>
internal sealed partial class ActivationMigrationManager : SystemTarget, IActivationMigrationManagerSystemTarget, IMigratingRequestsSystemTarget, IActivationMigrationManager, ILifecycleParticipant<ISiloLifecycle>
{
    private const int MaxBatchSize = 1_000;
    private const int MessageBodyFramingAllowance = 1_024;
    private const int PackageFramingAllowance = 64;
    private readonly ConcurrentDictionary<SiloAddress, (Task PumpTask, Channel<MigrationWorkItem> WorkItemChannel)> _workers = new();
    private readonly ObjectPool<MigrationWorkItem> _workItemPool = ObjectPool.Create(new MigrationWorkItem.ObjectPoolPolicy());
    private readonly CancellationTokenSource _shuttingDownCts = new();
    private readonly ILogger<ActivationMigrationManager> _logger;
    private readonly IInternalGrainFactory _grainFactory;
    private readonly Catalog _catalog;
    private readonly InsideRuntimeClient _runtimeClient;
    private readonly IClusterMembershipService _clusterMembershipService;
    private readonly IClusterManifestProvider _clusterManifestProvider;
    private readonly GrainInterfaceType _migratingRequestsInterfaceType;
    private readonly Serializer _serializer;
    private readonly int _maxMessageBodySize;
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
        Serializer serializer,
        IOptions<SiloMessagingOptions> messagingOptions,
        IClusterManifestProvider clusterManifestProvider,
        GrainInterfaceTypeResolver interfaceTypeResolver) : base(Constants.ActivationMigratorType, shared)
    {
        _clusterManifestProvider = clusterManifestProvider;
        _migratingRequestsInterfaceType = interfaceTypeResolver.GetGrainInterfaceType(typeof(IMigratingRequestsSystemTarget));
        _serializer = serializer;
        _maxMessageBodySize = messagingOptions.Value.MaxMessageBodySize;
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

    public ValueTask AcceptMigratingGrains(
        List<GrainMigrationPackage> migratingGrains,
        CancellationToken cancellationToken = default)
        => new(AcceptMigratingGrainsCoreAsync(migratingGrains, requestsAccepted: null, cancellationToken));

    public async ValueTask<bool[]> AcceptMigratingGrainsWithRequests(
        List<GrainMigrationPackage> migratingGrains,
        CancellationToken cancellationToken = default)
    {
        var requestsAccepted = new bool[migratingGrains.Count];
        await AcceptMigratingGrainsCoreAsync(migratingGrains, requestsAccepted, cancellationToken);
        return requestsAccepted;
    }

    private async Task AcceptMigratingGrainsCoreAsync(
        List<GrainMigrationPackage> migratingGrains,
        bool[]? requestsAccepted,
        CancellationToken cancellationToken)
    {
        var activations = new List<ActivationData>();
        var currentActivity = Activity.Current;
        for (var i = 0; i < migratingGrains.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var package = migratingGrains[i];
            var requests = package.Requests is { Count: > 0 } migratedRequests ? ToMessages(package.GrainId, migratedRequests) : null;

            // The new activation reads its previous address when it rehydrates, which happens after it queues the requests, so it is read here.
            GrainAddress? previousAddress = null;
            if (requests is not null)
            {
                package.MigrationContext.TryGetValue(ActivationData.GrainAddressMigrationContextKey, out previousAddress);
            }

            // If the activation does not exist, create it and provide it with the migration context and queued requests while doing so.
            // If the activation already exists or cannot be created, it is too late to perform migration, so ignore the request.
            var context = _catalog.GetOrCreateActivation(package.GrainId, requestContextData: null, package.MigrationContext, requests, previousAddress, out var accepted);
            if (requestsAccepted is not null)
            {
                requestsAccepted[i] = accepted;
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

    private List<Message>? ToMessages(GrainId grainId, List<MigratedRequest> requests)
    {
        var messages = new List<Message>(requests.Count);
        try
        {
            foreach (var request in requests)
            {
                messages.Add(request.ToMessage(grainId, Silo, _serializer.SessionPool));
            }

            return messages;
        }
        catch (Exception exception)
        {
            // The source forwards the requests with their original bodies instead.
            LogDebugMigratedRequestsNotDeserializable(exception, requests.Count, grainId);
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

    public ValueTask<MigrationResult> MigrateAsync(SiloAddress targetSilo, GrainId grainId, MigrationContext migrationContext, List<Message>? requests, out bool carriesRequests)
    {
        var package = new GrainMigrationPackage { GrainId = grainId, MigrationContext = migrationContext };
        long size = 0;
        List<(SiloAddress CallerSilo, MigratedRequestRoute Route)>? routes = null;
        carriesRequests = requests is { Count: > 0 }
            && AcceptsMigratedRequests(targetSilo)
            && TryPackageRequests(targetSilo, ref package, requests, out routes, out size);

        var workItem = _workItemPool.Get();
        workItem.Initialize(package, carriesRequests ? requests : null, routes, size, _serializer.SessionPool);
        var workItemWriter = GetOrCreateWorker(targetSilo);
        if (!workItemWriter.TryWrite(workItem))
        {
            workItem.SetResult(new(Migrated: false, MigratedRequestsOutcome.Retained, new SiloUnavailableException($"Silo {targetSilo} is no longer active")));
        }

        return workItem.AsValueTask();
    }

    /// <summary>
    /// Gets whether <paramref name="silo"/> accepts requests together with migrating grains, as silos running an earlier version do not.
    /// </summary>
    private bool AcceptsMigratedRequests(SiloAddress silo)
        => _clusterManifestProvider.Current.Silos.TryGetValue(silo, out var manifest)
            && manifest.Interfaces.ContainsKey(_migratingRequestsInterfaceType);

    /// <summary>
    /// Copies queued requests into the form which a migration carries, and records the routes for retargeting their callers.
    /// </summary>
    /// <param name="target">The migration target.</param>
    /// <param name="package">The migration package, which carries the requests when this returns <see langword="true"/>.</param>
    /// <param name="requests">The queued requests.</param>
    /// <param name="routes">The routes for retargeting the requests' callers.</param>
    /// <param name="size">The serialized size of the complete package, including its framing within a batch.</param>
    /// <returns><see langword="false"/> if the requests cannot be serialized or the package does not fit in a message.</returns>
    private bool TryPackageRequests(
        SiloAddress target,
        ref GrainMigrationPackage package,
        List<Message> requests,
        out List<(SiloAddress CallerSilo, MigratedRequestRoute Route)>? routes,
        out long size)
    {
        var grainId = package.GrainId;
        // Each request's sending silo holds its callback, unless the caller is an outside client, which tracks the call against its gateway.
        // Routes are taken from the sealed requests now: a caller on this silo shares its request message with its callback,
        // which a later migration of the same request can retarget while this one awaits acceptance.
        routes = null;
        size = 0;
        var packaged = new List<MigratedRequest>(requests.Count);

        // Serializing the migration context releases its buffer, so the package's size is bounded using the context's size bound rather than measured.
        var fixedSize = PackageFramingAllowance + package.MigrationContext.GetMaxSerializedSize() + grainId.Type.AsSpan().Length + grainId.Key.AsSpan().Length;
        var maxSize = MaxBatchBodySize - fixedSize;
        long bodyBytes = 0;
        foreach (var request in requests)
        {
            try
            {
                var migratedRequest = MigratedRequest.FromMessage(request, _serializer.SessionPool);
                packaged.Add(migratedRequest);

                // The bodies alone bound the requests' size from below, so stop copying them once they exceed the limit.
                bodyBytes += migratedRequest.Body?.Length ?? 0;
                if (bodyBytes > maxSize)
                {
                    LogDebugMigratedRequestsTooLarge(requests.Count, grainId, target);
                    return Decline(requests, packaged, out routes);
                }
            }
            catch (Exception exception)
            {
                LogDebugMigratedRequestsNotSerializable(exception, requests.Count, grainId);
                return Decline(requests, packaged, out routes);
            }

            // One-way requests have no pending callback to retarget.
            if (request.Direction is Message.Directions.OneWay || request.SendingSilo is not { } callerSilo)
            {
                continue;
            }

            (routes ??= []).Add((callerSilo, new(request.SendingGrain, request.Id, target, request.ForwardCount + 1)));
        }

        // Requests which fit in their own messages can exceed the message size limit together, or with the migration context and its keys.
        var requestsSize = MeasureRequests(grainId, packaged, maxSize);
        if (requestsSize < 0)
        {
            LogDebugMigratedRequestsTooLarge(requests.Count, grainId, target);
            return Decline(requests, packaged, out routes);
        }

        package.Requests = packaged;
        size = fixedSize + requestsSize;
        return true;

        bool Decline(List<Message> requests, List<MigratedRequest> packaged, out List<(SiloAddress CallerSilo, MigratedRequestRoute Route)>? routes)
        {
            RestoreRequests(requests, packaged, _serializer.SessionPool);
            routes = null;
            return false;
        }
    }

    /// <summary>
    /// Restores requests which were captured for migration but remain with this silo, since capturing them may have released the storage of their values.
    /// </summary>
    private static void RestoreRequests(List<Message> requests, List<MigratedRequest> packaged, SerializerSessionPool sessionPool)
    {
        for (var i = 0; i < packaged.Count; i++)
        {
            packaged[i].RestoreTo(requests[i], sessionPool);
        }
    }

    private int MaxBatchBodySize => _maxMessageBodySize - MessageBodyFramingAllowance;

    /// <summary>
    /// Returns the serialized size of <paramref name="requests"/>, or -1 if it exceeds <paramref name="limit"/> or they cannot be serialized.
    /// </summary>
    private long MeasureRequests(GrainId grainId, List<MigratedRequest> requests, long limit)
    {
        // Count the serialized bytes without retaining them, stopping once they exceed the limit.
        using var counter = new SizeLimitedCountingStream(limit);
        using var session = _serializer.SessionPool.GetSession();
        var writer = Writer.CreatePooled(counter, session);
        try
        {
            session.CodecProvider.GetCodec<List<MigratedRequest>>().WriteField(ref writer, 0, typeof(List<MigratedRequest>), requests);
            writer.Commit();
            return counter.Length;
        }
        catch (Exception exception)
        {
            if (!counter.LimitExceeded)
            {
                LogDebugMigratedRequestsNotSerializable(exception, requests.Count, grainId);
            }

            return -1;
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>
    /// Counts the bytes written to it, failing the write which exceeds its limit.
    /// </summary>
    private sealed class SizeLimitedCountingStream(long limit) : Stream
    {
        private long _length;

        public bool LimitExceeded { get; private set; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _length; set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _length += buffer.Length;
            if (_length > limit)
            {
                LimitExceeded = true;
                throw new InvalidOperationException("The requests exceed the message size limit.");
            }
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

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

    private async Task PumpMigrationQueue(SiloAddress targetSilo, Channel<MigrationWorkItem> workItems)
    {
        try
        {
            var remote = _grainFactory.GetSystemTarget<IActivationMigrationManagerSystemTarget>(Constants.ActivationMigratorType, targetSilo);
            var requestsRemote = _grainFactory.GetSystemTarget<IMigratingRequestsSystemTarget>(Constants.ActivationMigratorType, targetSilo);
            await Task.Yield();

            LogDebugStartingWorker(targetSilo);

            var maxBatchSize = MaxBatchBodySize;
            var items = new List<MigrationWorkItem>();
            var batch = new List<GrainMigrationPackage>();
            var reader = workItems.Reader;
            while (await reader.WaitToReadAsync())
            {
                try
                {
                    // Collect a batch of work items. Packages which carry requests travel together, within the message size limit, which their measured sizes respect.
                    // Other packages travel together as before.
                    long batchSize = 0;
                    var carriesRequests = reader.TryPeek(out var first) && first.Value.Requests is not null;
                    while (batch.Count < MaxBatchSize
                        && reader.TryPeek(out var next)
                        && (next.Value.Requests is not null) == carriesRequests
                        && (!carriesRequests || batchSize + next.Size <= maxBatchSize)
                        && reader.TryRead(out var workItem))
                    {
                        items.Add(workItem);
                        batch.Add(workItem.Value);
                        batchSize += workItem.Size;
                    }

                    // Attempt to migrate the batch.
                    if (!carriesRequests)
                    {
                        await remote.AcceptMigratingGrains(batch, _shuttingDownCts.Token);
                        foreach (var item in items)
                        {
                            item.SetResult(new(Migrated: true, MigratedRequestsOutcome.Retained, Exception: null));
                        }
                    }
                    else
                    {
                        var requestsAccepted = await requestsRemote.AcceptMigratingGrainsWithRequests(batch, _shuttingDownCts.Token);
                        if (requestsAccepted.Length != items.Count)
                        {
                            throw new InvalidOperationException($"Expected {items.Count} results from {targetSilo} but received {requestsAccepted.Length}.");
                        }

                        CompleteAfterRetargeting([.. items], requestsAccepted);
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
                        var requests = item.Value.Requests is null ? MigratedRequestsOutcome.Retained : MigratedRequestsOutcome.Unknown;
                        item.SetResult(new(Migrated: false, requests, exception));
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
    /// Retargets the callers of the requests which the target accepted, then completes the work items.
    /// </summary>
    /// <remarks>
    /// Each work item completes only after its callers acknowledge, so a retiring source departs only after its callers follow their requests.
    /// The pump continues with the next batch meanwhile.
    /// </remarks>
    private void CompleteAfterRetargeting(List<MigrationWorkItem> items, bool[] requestsAccepted)
    {
        Dictionary<SiloAddress, List<MigratedRequestRoute>>? routesBySilo = null;
        for (var i = 0; i < items.Count; i++)
        {
            if (requestsAccepted[i] && items[i].Routes is { } routes)
            {
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
        }

        if (routesBySilo is null)
        {
            Complete(items, requestsAccepted);
            return;
        }

        RetargetThenCompleteAsync(items, requestsAccepted, routesBySilo).Ignore();

        async Task RetargetThenCompleteAsync(List<MigrationWorkItem> items, bool[] requestsAccepted, Dictionary<SiloAddress, List<MigratedRequestRoute>> routesBySilo)
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
                        var remote = _grainFactory.GetSystemTarget<IMigratingRequestsSystemTarget>(Constants.ActivationMigratorType, callerSilo);
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
                Complete(items, requestsAccepted);
            }
        }

        static void Complete(List<MigrationWorkItem> items, bool[] requestsAccepted)
        {
            for (var i = 0; i < items.Count; i++)
            {
                var requests = items[i].Value.Requests is null || !requestsAccepted[i] ? MigratedRequestsOutcome.Retained : MigratedRequestsOutcome.Accepted;
                items[i].SetResult(new(Migrated: true, requests, Exception: null));
            }
        }
    }

    private ChannelWriter<MigrationWorkItem> GetOrCreateWorker(SiloAddress targetSilo)
    {
        if (!_workers.TryGetValue(targetSilo, out var entry))
        {
            lock (_lock)
            {
                if (!_workers.TryGetValue(targetSilo, out entry))
                {
                    using var _ = new ExecutionContextSuppressor();
                    var channel = Channel.CreateUnbounded<MigrationWorkItem>();
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
                item.SetResult(new(Migrated: false, MigratedRequestsOutcome.Retained, exception));
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

    private class MigrationWorkItem : IValueTaskSource<MigrationResult>
    {
        private ManualResetValueTaskSourceCore<MigrationResult> _core = new() { RunContinuationsAsynchronously = true };
        private GrainMigrationPackage _migrationPackage;

        private List<Message>? _requests;
        private SerializerSessionPool? _sessionPool;

        public void Initialize(
            GrainMigrationPackage package,
            List<Message>? requests,
            List<(SiloAddress CallerSilo, MigratedRequestRoute Route)>? routes,
            long size,
            SerializerSessionPool sessionPool)
        {
            _migrationPackage = package;
            _requests = requests;
            _sessionPool = sessionPool;
            Routes = routes;
            Size = size;
        }

        public void Reset()
        {
            _core.Reset();
            _migrationPackage = default;
            _requests = null;
            _sessionPool = null;
            Routes = null;
            Size = 0;
        }

        public GrainMigrationPackage Value => _migrationPackage;

        /// <summary>
        /// The routes for retargeting the callers of the requests which the migration carries.
        /// </summary>
        public List<(SiloAddress CallerSilo, MigratedRequestRoute Route)>? Routes { get; private set; }

        /// <summary>
        /// The approximate serialized size of the package.
        /// </summary>
        public long Size { get; private set; }

        public void SetResult(MigrationResult result)
        {
            // Requests which the migration carried but which remain with the source are restored before the source forwards them.
            if (result.Requests is MigratedRequestsOutcome.Retained && _requests is not null && _migrationPackage.Requests is { } packaged)
            {
                RestoreRequests(_requests, packaged, _sessionPool!);
            }

            _core.SetResult(result);
        }
        public ValueTask<MigrationResult> AsValueTask() => new(this, _core.Version);

        public MigrationResult GetResult(short token)
        {
            try
            {
                return _core.GetResult(token);
            }
            finally
            {
                Reset();
            }
        }

        public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) => _core.OnCompleted(continuation, state, token, flags);

        public sealed class ObjectPoolPolicy : IPooledObjectPolicy<MigrationWorkItem>
        {
            public MigrationWorkItem Create() => new();
            public bool Return(MigrationWorkItem obj)
            {
                obj.Reset();
                return true;
            }
        }
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
        Message = "{Count} queued requests for migrating grain {GrainId} cannot be serialized together, so they are forwarded instead of migrating with it."
    )]
    private partial void LogDebugMigratedRequestsNotSerializable(Exception exception, int count, GrainId grainId);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Declined {Count} migrated requests for grain {GrainId} because a request body cannot be deserialized. The source forwards them instead."
    )]
    private partial void LogDebugMigratedRequestsNotDeserializable(Exception exception, int count, GrainId grainId);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Some callers of {Count} migrated grains did not acknowledge their retargeted requests"
    )]
    private partial void LogDebugRetargetMigratedRequestsFailed(Exception exception, int count);
}
