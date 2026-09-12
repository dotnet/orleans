using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Core.Internal;
using Orleans.Diagnostics;
using Orleans.GrainDirectory;
using Orleans.Internal;
using Orleans.Runtime.Diagnostics;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Placement;
using Orleans.Runtime.Scheduler;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.Session;
using Orleans.Serialization.TypeSystem;

namespace Orleans.Runtime;

/// <summary>
/// Maintains additional per-activation state that is required for Orleans internal operations.
/// Concurrent mutation of activation state is synchronized internally.
/// </summary>
[DebuggerDisplay("GrainId = {GrainId}, State = {State}, Waiting = {WaitingCount}, Executing = {IsCurrentlyExecuting}")]
internal sealed partial class ActivationData :
    IGrainContext,
    ICollectibleGrainContext,
    IGrainExtensionBinder,
    IActivationWorkingSetMember,
    IGrainTimerRegistry,
    IGrainManagementExtension,
    IGrainCallCancellationExtension,
    ICallChainReentrantGrainContext,
    IAsyncDisposable,
    IDisposable
{
    private const string GrainAddressMigrationContextKey = "sys.addr";
#if NET10_0_OR_GREATER
    private readonly Lock _lock = new();
#else
    private readonly object _lock = new();
#endif
    private readonly GrainTypeSharedContext _shared;
    private readonly IServiceScope _serviceScope;
    private readonly WorkItemGroup _workItemGroup;

    // Embedded state preserves the existing per-activation storage without allocating subsystem objects.
    // Async coordinators accept ActivationData explicitly instead of executing on mutable structs.
    private RequestScheduler _requests;
    private MessagePumpState _messagePump;
    private LifecycleOperationQueue _operations;
    private GrainLifecycle? _lifecycle;
    private bool _isInWorkingSet = true;
    private CoarseStopwatch _idleDuration;
    private GrainReference? _selfReference;
    private IActivationCollectionRegistration? _collectionRegistration;

    // Values which are needed less frequently and do not warrant living directly on activation for object size reasons.
    // The values in this field are typically used to represent termination state of an activation or features which are not
    // used by all grains, such as grain timers.
    private ActivationDataExtras? _extras;

    private Activity? _activationActivity;

    /// <summary>
    /// Constants for activity error event names used during activation lifecycle.
    /// </summary>
    private static class ActivityErrorEvents
    {
        public const string InstanceCreateFailed = "instance-create-failed";
        public const string DirectoryRegisterFailed = "directory-register-failed";
        public const string ActivationCancelled = "activation-cancelled";
        public const string ActivationFailed = "activation-failed";
        public const string ActivationError = "activation-error";
        public const string OnActivateFailed = "on-activate-failed";
        public const string OnDeactivateFailed = "on-deactivate-failed";
        public const string RehydrateError = "rehydrate-error";
        public const string DehydrateError = "dehydrate-error";
    }

    public ActivationData(
        GrainAddress grainAddress,
        Func<IGrainContext, WorkItemGroup> createWorkItemGroup,
        IServiceProvider applicationServices,
        GrainTypeSharedContext shared)
    {
        ArgumentNullException.ThrowIfNull(grainAddress);
        ArgumentNullException.ThrowIfNull(createWorkItemGroup);
        ArgumentNullException.ThrowIfNull(applicationServices);
        ArgumentNullException.ThrowIfNull(shared);
        _shared = shared;
        _requests = new();
        _messagePump = new();
        Address = grainAddress;
        _serviceScope = applicationServices.CreateScope();
        Debug.Assert(_serviceScope != null, "_serviceScope must not be null.");
        _workItemGroup = createWorkItemGroup(this);
        Debug.Assert(_workItemGroup != null, "_workItemGroup must not be null.");
    }

    internal void SetActivationActivity(Activity activity)
    {
        _activationActivity = activity;
    }

    /// <summary>
    /// Gets the activity context for the activation activity, if available.
    /// This allows child activities to be properly parented during activation lifecycle operations.
    /// </summary>
    internal ActivityContext? GetActivationActivityContext()
    {
        return _activationActivity?.Context;
    }

    public void Start(IGrainActivator grainActivator)
    {
        Debug.Assert(Equals(ActivationTaskScheduler, TaskScheduler.Current));
        lock (_lock)
        {
            try
            {
                var instance = grainActivator.CreateInstance(this);
                SetGrainInstance(instance);
                _activationActivity?.AddEvent(new ActivityEvent("instance-created"));

                GrainLifecycleEvents.EmitCreated(this);
            }
            catch (Exception exception)
            {
                SetActivityError(_activationActivity, exception, ActivityErrorEvents.InstanceCreateFailed);

                Deactivate(new(DeactivationReasonCode.ActivationFailed, exception, "Error constructing grain instance."), _activationActivity?.Context, CancellationToken.None);
            }

            _messagePump.MessageLoopTask = RunMessageLoop(this);
        }
    }

    public ActivationTaskScheduler ActivationTaskScheduler => _workItemGroup.TaskScheduler;
    public IGrainRuntime GrainRuntime => _shared.Runtime;
    public object? GrainInstance { get; private set; }
    public GrainAddress Address { get; private set; }
    public GrainReference GrainReference => _selfReference ??= _shared.GrainReferenceActivator.CreateReference(GrainId, default);
    public ActivationState State { get; private set; } = ActivationState.Creating;
    public PlacementStrategy PlacementStrategy => _shared.PlacementStrategy;

    public IServiceProvider ActivationServices => _serviceScope.ServiceProvider;
    public ActivationId ActivationId => Address.ActivationId;
    public IGrainLifecycle ObservableLifecycle
    {
        get
        {
            if (_lifecycle is { } lifecycle) return lifecycle;
            lock (_lock) { return _lifecycle ??= new GrainLifecycle(_shared.Logger); }
        }
    }

    internal GrainTypeSharedContext Shared => _shared;

    public GrainId GrainId => Address.GrainId;
    public bool IsExemptFromCollection => _shared.CollectionAgeLimit == Timeout.InfiniteTimeSpan;
    IActivationCollectionRegistration? ICollectibleGrainContext.CollectionRegistration => Volatile.Read(ref _collectionRegistration);
    private DateTime KeepAliveUntil { get; set; } = DateTime.MinValue;
    public bool IsValid => State is ActivationState.Valid;

    IActivationCollectionRegistration ICollectibleGrainContext.GetOrSetCollectionRegistration(IActivationCollectionRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        return Interlocked.CompareExchange(ref _collectionRegistration, registration, null) ?? registration;
    }

    // Currently, the only supported multi-activation grain is one using the StatelessWorkerPlacement strategy.
    internal bool IsStatelessWorker => PlacementStrategy is StatelessWorkerPlacement;

    /// <summary>
    /// Returns a value indicating whether or not this placement strategy requires activations to be registered in
    /// the grain directory.
    /// </summary>
    internal bool IsUsingGrainDirectory => PlacementStrategy.IsUsingGrainDirectory;

    public int WaitingCount
    {
        get
        {
            lock (_lock)
            {
                return _requests.WaitingCount;
            }
        }
    }

    public bool IsInactive => GetRequestStatus().IsInactive;

    public bool IsCurrentlyExecuting
    {
        get
        {
            lock (_lock)
            {
                return _requests.IsRunning;
            }
        }
    }

    internal (int WaitingCount, bool IsInactive) GetRequestStatus()
    {
        lock (_lock)
        {
            var waitingCount = _requests.WaitingCount;
            return (waitingCount, waitingCount == 0 && !_requests.IsRunning);
        }
    }

    internal ValueTask WaitForActivationReadyAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (State is ActivationState.Valid or ActivationState.Invalid)
            {
                return ValueTask.CompletedTask;
            }

            _extras ??= new();
            var completion = _extras.ActivationReady ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            return new ValueTask(completion.Task.WaitAsync(cancellationToken));
        }
    }

    public IWorkItemScheduler Scheduler => _workItemGroup;
    public Task Deactivated => GetDeactivationCompletionSource().Task;

    public SiloAddress? ForwardingAddress
    {
        get => _extras?.ForwardingAddress;
        set
        {
            lock (_lock)
            {
                _extras ??= new();
                _extras.ForwardingAddress = value;
            }
        }
    }

    /// <summary>
    /// Gets the previous directory registration for this grain, if known.
    /// This is used to update the grain directory to point to the new registration during activation.
    /// </summary>
    public GrainAddress? PreviousRegistration
    {
        get => _extras?.PreviousRegistration;
        set
        {
            lock (_lock)
            {
                _extras ??= new();
                _extras.PreviousRegistration = value;
            }
        }
    }

    private Exception? DeactivationException => _extras?.DeactivationReason.Exception;

    private DeactivationReason DeactivationReason
    {
        get => _extras?.DeactivationReason ?? default;
        set
        {
            lock (_lock)
            {
                _extras ??= new();
                _extras.DeactivationReason = value;
            }
        }
    }

    private HashSet<IGrainTimer>? Timers
    {
        get => _extras?.Timers;
        set
        {
            lock (_lock)
            {
                _extras ??= new();
                _extras.Timers = value;
            }
        }
    }

    private DateTime? DeactivationStartTime
    {
        get => _extras?.DeactivationStartTime;
        set
        {
            lock (_lock)
            {
                _extras ??= new();
                _extras.DeactivationStartTime = value;
            }
        }
    }

    private bool IsStuckDeactivating
    {
        get => _extras?.IsStuckDeactivating ?? false;
        set
        {
            lock (_lock)
            {
                _extras ??= new();
                _extras.IsStuckDeactivating = value;
            }
        }
    }

    private bool IsStuckProcessingMessage
    {
        get => _extras?.IsStuckProcessingMessage ?? false;
        set
        {
            lock (_lock)
            {
                _extras ??= new();
                _extras.IsStuckProcessingMessage = value;
            }
        }
    }

    private DehydrationContextHolder? DehydrationContext
    {
        get => _extras?.DehydrationContext;
        set
        {
            lock (_lock)
            {
                _extras ??= new();
                _extras.DehydrationContext = value;
            }
        }
    }

    public TimeSpan CollectionAgeLimit => _shared.CollectionAgeLimit;


    internal void SetGrainInstance(object grainInstance)
    {
        ArgumentNullException.ThrowIfNull(grainInstance);

        lock (_lock)
        {
            if (GrainInstance is not null)
            {
                throw new InvalidOperationException("Grain instance is already set.");
            }

            if (State is not ActivationState.Creating)
            {
                throw new InvalidOperationException("Grain instance can only be set during creation.");
            }

            GrainInstance = grainInstance;

            _shared.OnCreateActivation(this);
            GetComponent<IActivationLifecycleObserver>()?.OnCreateActivation(this);

            if (grainInstance is ILifecycleParticipant<IGrainLifecycle> participant)
            {
                participant.Participate(ObservableLifecycle);
            }
        }
    }

    private void SetState(ActivationState state)
    {
#if NET10_0_OR_GREATER
        Debug.Assert(_lock.IsHeldByCurrentThread);
#else
        Debug.Assert(Monitor.IsEntered(_lock));
#endif
        State = state;
        if (state is ActivationState.Valid or ActivationState.Invalid)
        {
            var activationReady = _extras?.ActivationReady;
            if (_extras is not null)
            {
                _extras.ActivationReady = null;
            }

            activationReady?.TrySetResult();
        }
    }


    /// <summary>
    /// Returns how long this activation has been idle.
    /// </summary>
    public TimeSpan GetIdleness() => _idleDuration.Elapsed;

    /// <summary>
    /// Returns whether this activation has been idle long enough to be collected.
    /// </summary>
    public bool IsStale() => GetIdleness() >= _shared.CollectionAgeLimit;

    public void DelayDeactivation(TimeSpan timespan)
    {
        var rescheduleCollection = false;
        lock (_lock)
        {
            if (timespan == TimeSpan.MaxValue || timespan == Timeout.InfiniteTimeSpan)
            {
                // Adding these values to the current time would overflow, so use DateTime.MaxValue directly.
                KeepAliveUntil = DateTime.MaxValue;
            }
            else if (timespan <= TimeSpan.Zero)
            {
                // Cancel the previous DelayDeactivation and revert to normal collection behavior.
                // If there was an active keep-alive, reschedule collection so the grain can be collected
                // after CollectionAgeLimit rather than waiting for the previously scheduled far-future time.
                rescheduleCollection = KeepAliveUntil > GrainRuntime.TimeProvider.GetUtcNow().UtcDateTime;
                KeepAliveUntil = DateTime.MinValue;
            }
            else
            {
                KeepAliveUntil = GrainRuntime.TimeProvider.GetUtcNow().UtcDateTime + timespan;
            }
        }

        if (rescheduleCollection)
        {
            _shared.InternalRuntime.ActivationCollector.TryRescheduleCollection(this);
        }
    }

    public void ResetKeepAliveRequest()
    {
        lock (_lock)
        {
            KeepAliveUntil = DateTime.MinValue;
        }
    }

    ActivationCollectionResult ICollectibleGrainContext.TryDeactivateForCollection(
        DeactivationReason reason,
        DateTime now,
        TimeSpan ageLimit,
        bool respectKeepAlive,
        CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (State is not ActivationState.Valid)
            {
                return ActivationCollectionResult.Remove;
            }

            if (respectKeepAlive && KeepAliveUntil > now)
            {
                var keepAliveDuration = KeepAliveUntil - now;
                return ActivationCollectionResult.Reschedule(
                    TimeSpan.FromTicks(Math.Max(keepAliveDuration.Ticks, CollectionAgeLimit.Ticks)));
            }

            if (_requests.WaitingCount > 0 || _requests.RunningCount > 0 || _idleDuration.Elapsed < ageLimit)
            {
                return ActivationCollectionResult.Reschedule(CollectionAgeLimit);
            }

            Deactivate(reason, cancellationToken);
            return ActivationCollectionResult.StartedDeactivation;
        }
    }

    public void Migrate(Dictionary<string, object>? requestContext, CancellationToken cancellationToken = default) =>
        TryStartMigration(requestContext, cancellationToken);

    internal bool TryStartMigration(Dictionary<string, object>? requestContext, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (State is not (ActivationState.Activating or ActivationState.Valid or ActivationState.Deactivating))
            {
                return false;
            }

            // If migration has not already been started, set a migration context to capture any state which should be transferred.
            // Doing this signals to the deactivation process that a migration is occurring, so it is important that this happens before we begin deactivation.
            DehydrationContext ??= new(_shared.SerializerSessionPool, requestContext);

            if (State is not ActivationState.Deactivating)
            {
                // Start deactivating the grain to prepare for migration.
                Deactivate(new DeactivationReason(DeactivationReasonCode.Migrating, "Migrating to a new location."), cancellationToken);
            }

            return true;
        }
    }
    public void Deactivate(DeactivationReason reason, ActivityContext? activityContext, CancellationToken cancellationToken = default)
    {
        var currentActivity = Activity.Current;
        var deactivateActivity = activityContext is { } parent
            ? ActivitySources.LifecycleGrainSource.StartActivity(ActivityNames.DeactivateGrain, ActivityKind.Internal, parentContext: parent)
            : ActivitySources.LifecycleGrainSource.StartActivity(ActivityNames.DeactivateGrain);

        lock (_lock)
        {
            try
            {
                var state = State;
                if (deactivateActivity is { IsAllDataRequested: true })
                {
                    deactivateActivity.SetTag(ActivityTagKeys.GrainState, state);
                }

                if (state is ActivationState.Invalid)
                {
                    deactivateActivity?.Stop();
                    return;
                }

                if (DeactivationReason.ReasonCode == DeactivationReasonCode.None)
                {
                    DeactivationReason = reason;
                }

                if (deactivateActivity is { IsAllDataRequested: true })
                {
                    deactivateActivity.SetTag(ActivityTagKeys.DeactivationReason, DeactivationReason);
                }

                if (!DeactivationStartTime.HasValue)
                {
                    DeactivationStartTime = GrainRuntime.TimeProvider.GetUtcNow().UtcDateTime;
                }

                if (state is ActivationState.Creating or ActivationState.Activating or ActivationState.Valid)
                {
                    GrainLifecycleEvents.EmitDeactivating(this, DeactivationReason);

                    CancelPendingOperations();

                    _shared.InternalRuntime.ActivationWorkingSet.OnDeactivating(this);
                    SetState(ActivationState.Deactivating);
                    var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cts.CancelAfter(_shared.InternalRuntime.CollectionOptions.Value.DeactivationTimeout);
                    ScheduleOperation(new Command.Deactivate(cts, state, deactivateActivity));
                }
                else
                {
                    deactivateActivity?.Stop();
                }

                Debug.Assert(State is ActivationState.Deactivating or ActivationState.Invalid, "Deactivate should leave the activation deactivating or invalid.");
            }
            catch (Exception ex)
            {
                SetActivityError(deactivateActivity, ex, "Error deactivating grain");
                deactivateActivity?.Stop();
                throw;
            }
            finally
            {
                Activity.Current = currentActivity;
            }
        }
    }

    public void Deactivate(DeactivationReason reason, CancellationToken cancellationToken = default) => Deactivate(reason, Activity.Current?.Context, cancellationToken);

    public void Dispose()
    {
#pragma warning disable RS0030 // IDisposable requires synchronously completing activation cleanup.
        DisposeAsync().AsTask().Wait();
#pragma warning restore RS0030
    }

    public ValueTask DisposeAsync() => DisposeAsync(this);

    private static async ValueTask DisposeAsync(ActivationData activation)
    {
        activation._extras ??= new();
        if (activation._extras.IsDisposing) return;
        activation._extras.IsDisposing = true;

        activation.CancelPendingOperations();

        lock (activation._lock)
        {
            activation._shared.InternalRuntime.ActivationWorkingSet.OnDeactivated(activation);
            activation.SetState(ActivationState.Invalid);
        }

        activation.DisposeTimers();

        try
        {
            var activator = activation._shared.GetComponent(typeof(IGrainActivator)) as IGrainActivator;
            if (activator != null && activation.GrainInstance is { } instance)
            {
                await activator.DisposeInstance(activation, instance);
            }
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            activation._shared.OnDestroyActivation(activation);
            activation.GetComponent<IActivationLifecycleObserver>()?.OnDestroyActivation(activation);
        }
        catch (ObjectDisposedException)
        {
        }

        await DisposeAsync(activation._serviceScope);
    }

    private static async ValueTask DisposeAsync(object obj)
    {
        try
        {
            if (obj is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            else if (obj is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        catch
        {
            // Ignore.
        }
    }

    bool IEquatable<IGrainContext>.Equals(IGrainContext? other) => ReferenceEquals(this, other);


    public void ReceiveMessage(object message) => ReceiveMessage((Message)message);
    public void ReceiveMessage(Message message)
    {
        _shared.InternalRuntime.MessagingTrace.OnDispatcherReceiveMessage(message);

        // Don't process messages that have already timed out
        if (message.IsExpired)
        {
            _shared.MessagingProcessingInstruments.OnDispatcherMessageProcessedError(message);
            _shared.InternalRuntime.MessagingTrace.OnDropExpiredMessage(message, MessagingInstruments.Phase.Dispatch);
            return;
        }

        if (message.Direction == Message.Directions.Response)
        {
            ReceiveResponse(message);
        }
        else // Request or OneWay
        {
            ReceiveRequest(message);
        }
    }

    private void ReceiveResponse(Message message)
    {
        var state = State;
        if (state == ActivationState.Invalid)
        {
            _shared.InternalRuntime.MessagingTrace.OnDispatcherReceiveInvalidActivation(message, state);
            // Note that we always process responses, even if the activation is invalid.
        }
        else
        {
            _shared.MessagingProcessingInstruments.OnDispatcherMessageProcessedOk(message);
        }

        _shared.InternalRuntime.RuntimeClient.ReceiveResponse(message);
    }

    private void ReceiveRequest(Message message)
    {
        var overloadException = CheckOverloaded();
        if (overloadException != null && !message.IsLocalOnly)
        {
            _shared.MessagingProcessingInstruments.OnDispatcherMessageProcessedError(message);
            _shared.InternalRuntime.MessageCenter.RejectMessage(message, Message.RejectionTypes.Overloaded, overloadException, "Target activation is overloaded " + this);
            return;
        }

        lock (_lock)
        {
            _requests.Enqueue(message);
        }

        _messagePump.Signal();
    }

    internal int GetRequestCount()
    {
        lock (_lock)
        {
            return _requests.RunningCount + _requests.WaitingCount;
        }
    }

    internal List<Message> DequeueAllWaitingRequests()
    {
        lock (_lock)
        {
            return _requests.DrainWaiting();
        }
    }
}
