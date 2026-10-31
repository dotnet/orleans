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
    IActivationKeepAlive,
    IGrainExtensionBinder,
    IActivationWorkingSetMember,
    IGrainTimerRegistry,
    IGrainManagementExtension,
    IGrainCallCancellationExtension,
    ICallChainReentrantGrainContext,
    IAsyncDisposable,
    IDisposable
{
    internal const string GrainAddressMigrationContextKey = "sys.addr";
#if NET10_0_OR_GREATER
    private readonly Lock _lock = new();
#else
    private readonly object _lock = new();
#endif
    private readonly GrainTypeSharedContext _shared;
    private readonly IServiceScope _serviceScope;
    private readonly WorkItemGroup _workItemGroup;
    private readonly List<(Message Message, CoarseStopwatch QueuedTime)> _waitingRequests = new();
    private readonly Dictionary<Message, CoarseStopwatch> _runningRequests = new();
    private readonly SingleWaiterAutoResetEvent _workSignal = new() { RunContinuationsAsynchronously = true };
    private GrainLifecycle? _lifecycle;
    private Queue<object>? _pendingOperations;
    private Message? _blockingRequest;
    private int _runningNonAlwaysInterleaveCount;
    private int _runningNonAlwaysInterleaveWritableCount;
    private bool _isInWorkingSet = true;
    private CoarseStopwatch _busyDuration;
    private CoarseStopwatch _idleDuration;
    private GrainReference? _selfReference;
    private IActivationCollectionRegistration? _collectionRegistration;

    // Values which are needed less frequently and do not warrant living directly on activation for object size reasons.
    // The values in this field are typically used to represent termination state of an activation or features which are not
    // used by all grains, such as grain timers.
    private ActivationDataExtra? _extras;

    // The task representing this activation's message loop.
    // This field is assigned and never read and exists only for debugging purposes (eg, in memory dumps, to associate a loop task with an activation).
#pragma warning disable IDE0052 // Remove unread private members
    private Task? _messageLoopTask;
#pragma warning restore IDE0052 // Remove unread private members

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
            // An activation which was deactivated or disposed before it started has nothing to construct. Its teardown starts at Disposing.
            if (State is ActivationState.None && !IsDeactivationRequested)
            {
                SetState(ActivationState.Constructing);
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
                    FailSetup(
                        new(DeactivationReasonCode.ActivationFailed, exception, "Error constructing grain instance."),
                        _activationActivity?.Context,
                        CancellationToken.None);
                }
            }

            _messageLoopTask = RunMessageLoop();
        }
    }

    public ActivationTaskScheduler ActivationTaskScheduler => _workItemGroup.TaskScheduler;
    public IGrainRuntime GrainRuntime => _shared.Runtime;
    public object? GrainInstance { get; private set; }
    public GrainAddress Address { get; private set; }
    public GrainReference GrainReference => _selfReference ??= _shared.GrainReferenceActivator.CreateReference(GrainId, default);
    public ActivationState State { get; private set; }
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
    public bool IsValid => State is ActivationState.Active;

    /// <summary>
    /// Gets whether deactivation has been requested.
    /// </summary>
    /// <remarks>
    /// During setup, a request is recorded intent which leaves the setup state unchanged. Setup stops at its next phase boundary, and the
    /// deactivation operation then enters teardown at the mirror of the last completed phase.
    /// Every teardown state before <see cref="ActivationState.Disposing"/> follows a request.
    /// </remarks>
    internal bool IsDeactivationRequested => _extras?.IsDeactivationRequested ?? false;

    /// <summary>
    /// Gets whether this activation is going away: deactivation was requested or it is disposing.
    /// </summary>
    internal bool IsTerminating => IsDeactivationRequested || State.IsDisposingOrDestroyed();

    private void MarkDeactivationRequested()
    {
        lock (_lock)
        {
            _extras ??= new();
            _extras.IsDeactivationRequested = true;
        }
    }

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
                return _waitingRequests.Count;
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
                return _runningRequests.Count > 0;
            }
        }
    }

    internal (int WaitingCount, bool IsInactive) GetRequestStatus()
    {
        lock (_lock)
        {
            var waitingCount = _waitingRequests.Count;
            return (waitingCount, waitingCount == 0 && _runningRequests.Count == 0);
        }
    }

    internal ValueTask WaitForActivationReadyAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (IsReady(State))
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

    public object? GetTarget() => GrainInstance;

    object? ITargetHolder.GetComponent(Type componentType)
    {
        var result = GetComponent(componentType);
        if (result is null && typeof(IGrainExtension).IsAssignableFrom(componentType))
        {
            var implementation = ActivationServices.GetKeyedService<IGrainExtension>(componentType);
            if (implementation is not { } typedResult)
            {
                throw new GrainExtensionNotInstalledException($"No extension of type {componentType} is installed on this instance and no implementations are registered for automated install");
            }

            SetComponent(componentType, typedResult);
            result = typedResult;
        }

        return result;
    }

    public TComponent? GetComponent<TComponent>() where TComponent : class => GetComponent(typeof(TComponent)) as TComponent;

    public object? GetComponent(Type componentType)
    {
        object? result;
        if (componentType.IsAssignableFrom(GrainInstance?.GetType()))
        {
            result = GrainInstance;
        }
        else if (componentType.IsAssignableFrom(GetType()))
        {
            result = this;
        }
        else if (_extras is { } components && components.TryGetValue(componentType, out var resultObj))
        {
            result = resultObj;
        }
        else if (_shared.GetComponent(componentType) is { } sharedComponent)
        {
            result = sharedComponent;
        }
        else if (ActivationServices.GetService(componentType) is { } component)
        {
            SetComponent(componentType, component);
            result = component;
        }
        else
        {
            result = null;
        }

        return result;
    }

    public void SetComponent<TComponent>(TComponent? instance) where TComponent : class => SetComponent(typeof(TComponent), instance);

    public void SetComponent(Type componentType, object? instance)
    {
        if (componentType.IsAssignableFrom(GrainInstance?.GetType()))
        {
            throw new ArgumentException("Cannot override a component which is implemented by this grain");
        }
        else if (componentType.IsAssignableFrom(GetType()))
        {
            throw new ArgumentException("Cannot override a component which is implemented by this grain context");
        }

        lock (_lock)
        {
            if (instance == null)
            {
                _extras?.Remove(componentType);
                return;
            }

            _extras ??= new();
            _extras[componentType] = instance;
        }
    }

    internal void SetGrainInstance(object grainInstance)
    {
        ArgumentNullException.ThrowIfNull(grainInstance);

        lock (_lock)
        {
            if (GrainInstance is not null)
            {
                throw new InvalidOperationException("Grain instance is already set.");
            }

            if (State is not ActivationState.Constructing)
            {
                throw new InvalidOperationException("Grain instance can only be set during construction.");
            }

            GrainInstance = grainInstance;

            _shared.OnCreateActivation(this);
            GetComponent<IActivationLifecycleObserver>()?.OnCreateActivation(this);

            _shared.SetupActivation(this);

            if (grainInstance is ILifecycleParticipant<IGrainLifecycle> participant)
            {
                participant.Participate(ObservableLifecycle);
            }
        }
    }

    /// <summary>
    /// Transitions to <paramref name="state"/>.
    /// </summary>
    /// <param name="state">The next state.</param>
    /// <param name="forced">Whether disposal preempts the remaining phases, moving the activation directly to <see cref="ActivationState.Disposing"/>.</param>
    private void SetState(ActivationState state, bool forced = false)
    {
#if NET10_0_OR_GREATER
        Debug.Assert(_lock.IsHeldByCurrentThread);
#else
        Debug.Assert(Monitor.IsEntered(_lock));
#endif
        Debug.Assert(
            forced ? state is ActivationState.Disposing && State < ActivationState.Disposing : ActivationStateExtensions.IsValidTransition(State, state),
            $"Invalid activation state transition from {State} to {state} (forced: {forced}).");
        State = state;
        if (IsReady(state))
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
    /// Gets whether an activation in <paramref name="state"/> has either completed setup or been abandoned, so that it has a final directory registration outcome.
    /// </summary>
    private static bool IsReady(ActivationState state) => state is ActivationState.Active || state.IsDisposingOrDestroyed();

    /// <summary>
    /// Advances setup past the completed phase <paramref name="completed"/>.
    /// </summary>
    /// <remarks>
    /// When deactivation has been requested, setup stops and the activation remains in its last completed phase. The deactivation operation then
    /// enters teardown at that phase's mirror.
    /// </remarks>
    /// <returns><see langword="true"/> if setup continues; otherwise, <see langword="false"/>.</returns>
    private bool TryAdvanceSetup(ActivationState completed)
    {
        Debug.Assert(completed is > ActivationState.None and < ActivationState.Active);
        lock (_lock)
        {
            // Only forced disposal changes the state while setup runs. A deactivation requested before the activation starts skips construction,
            // so an activation operation which was queued before that request finds the activation in None.
            Debug.Assert(State == completed || State.IsDisposingOrDestroyed() || State is ActivationState.None && IsDeactivationRequested);
            if (State != completed || IsDeactivationRequested)
            {
                return false;
            }

            SetState(completed + 1);
            if (State is ActivationState.Active)
            {
                _idleDuration = CoarseStopwatch.StartNew();
                _shared.InternalRuntime.ActivationWorkingSet.OnActivated(this);
            }

            return true;
        }
    }

    /// <summary>
    /// Stops setup because its current phase failed, requesting deactivation and entering teardown at the mirror of the phase before it.
    /// </summary>
    private void FailSetup(DeactivationReason reason, ActivityContext? activityContext, CancellationToken cancellationToken)
    {
        // An active activation enters teardown here.
        Deactivate(reason, activityContext, cancellationToken);
        lock (_lock)
        {
            // Forced disposal moves the activation out of setup, leaving nothing to stop.
            if (State.IsSettingUp())
            {
                SetState(State.GetFailureTeardownEntry());
            }
        }
    }

    /// <inheritdoc cref="FailSetup(DeactivationReason, ActivityContext?, CancellationToken)"/>
    private void FailSetup(DeactivationReason reason, CancellationToken cancellationToken) =>
        FailSetup(reason, Activity.Current?.Context, cancellationToken);

    /// <summary>
    /// Advances teardown past the completed phase <paramref name="completed"/>.
    /// </summary>
    private void AdvanceTeardown(ActivationState completed)
    {
        Debug.Assert(completed is > ActivationState.Active and < ActivationState.Disposing);
        lock (_lock)
        {
            if (State == completed)
            {
                SetState(completed + 1);
            }
            else
            {
                // Only forced disposal changes the state while teardown runs.
                Debug.Assert(State.IsDisposingOrDestroyed());
            }
        }
    }

    /// <summary>
    /// Claims disposal of this activation's resources, entering <see cref="ActivationState.Disposing"/> if the activation has not reached it.
    /// </summary>
    /// <returns><see langword="true"/> if the caller disposes the activation; <see langword="false"/> if disposal was already claimed.</returns>
    private bool TryClaimDisposal()
    {
        lock (_lock)
        {
            _extras ??= new();
            if (_extras.IsDisposing)
            {
                return false;
            }

            _extras.IsDisposing = true;
            if (State < ActivationState.Disposing)
            {
                // Disposal preempts the phases which have not run: setup, or teardown which an error interrupted.
                SetState(ActivationState.Disposing, forced: true);
            }

            return true;
        }
    }

    /// <summary>
    /// Check whether this activation is overloaded.
    /// Returns LimitExceededException if overloaded, otherwise <c>null</c>c>
    /// </summary>
    /// <returns>Returns LimitExceededException if overloaded, otherwise <c>null</c>c></returns>
    public LimitExceededException? CheckOverloaded()
    {
        string limitName = nameof(SiloMessagingOptions.MaxEnqueuedRequestsHardLimit);
        int maxRequestsHardLimit = _shared.MessagingOptions.MaxEnqueuedRequestsHardLimit;
        int maxRequestsSoftLimit = _shared.MessagingOptions.MaxEnqueuedRequestsSoftLimit;
        if (IsStatelessWorker)
        {
            limitName = nameof(SiloMessagingOptions.MaxEnqueuedRequestsHardLimit_StatelessWorker);
            maxRequestsHardLimit = _shared.MessagingOptions.MaxEnqueuedRequestsHardLimit_StatelessWorker;
            maxRequestsSoftLimit = _shared.MessagingOptions.MaxEnqueuedRequestsSoftLimit_StatelessWorker;
        }

        if (maxRequestsHardLimit <= 0 && maxRequestsSoftLimit <= 0) return null; // No limits are set

        int count = GetRequestCount();

        if (maxRequestsHardLimit > 0 && count > maxRequestsHardLimit) // Hard limit
        {
            LogRejectActivationTooManyRequests(_shared.Logger, count, this, maxRequestsHardLimit);
            return new LimitExceededException(limitName, count, maxRequestsHardLimit, ToString());
        }

        if (maxRequestsSoftLimit > 0 && count > maxRequestsSoftLimit) // Soft limit
        {
            LogWarnActivationTooManyRequests(_shared.Logger, count, this, maxRequestsSoftLimit);
            return null;
        }

        return null;
    }

    internal int GetRequestCount()
    {
        lock (_lock)
        {
            return _runningRequests.Count + _waitingRequests.Count;
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

    void IActivationKeepAlive.AcquireKeepAlive()
    {
        lock (_lock)
        {
            _extras ??= new();
            ++_extras.KeepAliveHolds;
        }
    }

    void IActivationKeepAlive.ReleaseKeepAlive()
    {
        lock (_lock)
        {
            Debug.Assert(_extras is { KeepAliveHolds: > 0 });

            // A deactivating or disposed activation must not re-enter the working set.
            if (--_extras!.KeepAliveHolds == 0 && State is ActivationState.Active)
            {
                MarkActive();
            }
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
            if (State is not ActivationState.Active)
            {
                return ActivationCollectionResult.Remove;
            }

            if (respectKeepAlive && KeepAliveUntil > now)
            {
                var keepAliveDuration = KeepAliveUntil - now;
                return ActivationCollectionResult.Reschedule(
                    TimeSpan.FromTicks(Math.Max(keepAliveDuration.Ticks, CollectionAgeLimit.Ticks)));
            }

            if (_waitingRequests.Count > 0
                || _runningRequests.Count > 0
                || _idleDuration.Elapsed < ageLimit
                || respectKeepAlive && _extras is { KeepAliveHolds: > 0 })
            {
                return ActivationCollectionResult.Reschedule(CollectionAgeLimit);
            }

            Deactivate(reason, cancellationToken);
            return ActivationCollectionResult.StartedDeactivation;
        }
    }

    private void ScheduleOperation(object operation)
    {
        lock (_lock)
        {
            _pendingOperations ??= new();
            _pendingOperations.Enqueue(operation);
        }

        _workSignal.Signal();
    }

    private void CancelPendingOperations()
    {
        lock (_lock)
        {
            // If the grain is currently activating, cancel that operation.
            if (_pendingOperations is not { Count: > 0 } operations)
            {
                return;
            }

            var opCount = operations.Count;
            // We could be using an ArrayPool<Command> here, but we would need to filter out the
            // non-command types, which means we would need to loop over _pendingOperations, which
            // defeats the purpose of making a snapshot.
            // Note: CopyTo does not use the queue's enumerator, so its safe to take a snapshot this way.
            var array = ArrayPool<object>.Shared.Rent(opCount);

            operations.CopyTo(array, 0);

            var snapshot = new Span<object>(array, 0, opCount);

            try
            {
                foreach (var op in snapshot)
                {
                    if (op is Command cmd)
                    {
                        try
                        {
                            cmd.Cancel();
                        }
                        catch (Exception exception)
                        {
                            if (exception is not ObjectDisposedException)
                            {
                                LogErrorCancellingOperation(_shared.Logger, exception, cmd);
                            }
                        }
                    }
                }
            }
            finally
            {
                ArrayPool<object>.Shared.Return(array, true);
            }
        }
    }

    public void Migrate(Dictionary<string, object>? requestContext, CancellationToken cancellationToken = default) =>
        TryStartMigration(requestContext, cancellationToken);

    internal bool TryStartMigration(Dictionary<string, object>? requestContext, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            // Migration can be requested once the grain lifecycle starts, until teardown reaches Dehydrating, which decides whether the activation migrates.
            if (State is < ActivationState.Starting or >= ActivationState.Dehydrating)
            {
                return false;
            }

            // If migration has not already been started, set a migration context to capture any state which should be transferred.
            // Doing this signals to the deactivation process that a migration is occurring, so it is important that this happens before we begin deactivation.
            DehydrationContext ??= new(_shared.SerializerSessionPool, requestContext);

            if (!IsDeactivationRequested)
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

                if (state.IsDisposingOrDestroyed())
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

                if (!IsDeactivationRequested)
                {
                    Debug.Assert(!state.IsTearingDown(), "Teardown before Disposing follows a deactivation request.");

                    // Cancel setup and any earlier operations before scheduling teardown, which must not be canceled with them.
                    CancelPendingOperations();
                    var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cts.CancelAfter(_shared.InternalRuntime.CollectionOptions.Value.DeactivationTimeout);
                    var command = new Command.Deactivate(cts, deactivateActivity);

                    // The request is recorded together with the operation which carries it out, so that a recorded request always completes.
                    // An active activation enters teardown now. During setup, the request is recorded intent: setup stops at its next
                    // phase boundary, and the deactivation operation, which runs after setup, enters teardown.
                    MarkDeactivationRequested();
                    if (state is ActivationState.Active)
                    {
                        SetState(ActivationState.Deactivating);
                    }

                    ScheduleOperation(command);

                    _shared.InternalRuntime.ActivationWorkingSet.OnDeactivating(this);
                    GrainLifecycleEvents.EmitDeactivating(this, DeactivationReason);
                }
                else
                {
                    deactivateActivity?.Stop();
                }

                // Lifecycle observers and diagnostic listeners run synchronously above and may force disposal, so the state is not asserted.
                Debug.Assert(IsDeactivationRequested, "Deactivate should leave a deactivation request.");
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

    private void DeactivateStuckActivation()
    {
        IsStuckProcessingMessage = true;
        var msg = $"Activation {this} has been processing request {_blockingRequest} since {_busyDuration} and is likely stuck.";
        var reason = new DeactivationReason(DeactivationReasonCode.ActivationUnresponsive, msg);

        // Mark the grain as deactivating so that messages are forwarded instead of being invoked
        Deactivate(reason, cancellationToken: default);

        // Try to remove this activation from the catalog and directory
        // This leaves this activation dangling, stuck processing the current request until it eventually completes
        // (which likely will never happen at this point, since if the grain was deemed stuck then there is probably some kind of
        // application bug, perhaps a deadlock)
        UnregisterMessageTarget();
        _shared.InternalRuntime.GrainLocator.Unregister(Address, UnregistrationCause.Force).Ignore();
    }

    void IGrainTimerRegistry.OnTimerCreated(IGrainTimer timer)
    {
        lock (_lock)
        {
            Timers ??= new HashSet<IGrainTimer>();
            Timers.Add(timer);
        }
    }

    void IGrainTimerRegistry.OnTimerDisposed(IGrainTimer timer)
    {
        lock (_lock) // need to lock since dispose can be called on finalizer thread, outside grain context (not single threaded).
        {
            if (Timers is null)
            {
                return;
            }

            Timers.Remove(timer);
        }
    }

    private void DisposeTimers()
    {
        lock (_lock)
        {
            if (Timers is null)
            {
                return;
            }

            // Need to set Timers to null since OnTimerDisposed mutates the timers set if it is not null.
            var timers = Timers;
            Timers = null;

            // Dispose all timers.
            foreach (var timer in timers)
            {
                timer.Dispose();
            }
        }
    }

    public void AnalyzeWorkload(DateTime now, IMessageCenter messageCenter, MessageFactory messageFactory, SiloMessagingOptions options)
    {
        var slowRunningRequestDuration = options.RequestProcessingWarningTime;
        var longQueueTimeDuration = options.RequestQueueDelayWarningTime;

        List<string>? diagnostics = null;
        lock (_lock)
        {
            if (State != ActivationState.Active)
            {
                return;
            }

            if (_blockingRequest is not null)
            {
                var message = _blockingRequest;
                TimeSpan? timeSinceQueued = default;
                if (_runningRequests.TryGetValue(message, out var waitTime))
                {
                    timeSinceQueued = waitTime.Elapsed;
                }

                var executionTime = _busyDuration.Elapsed;
                if (executionTime >= slowRunningRequestDuration && !message.IsLocalOnly)
                {
                    GetStatusList(ref diagnostics);
                    if (timeSinceQueued.HasValue)
                    {
                        diagnostics.Add($"Message {message} was enqueued {timeSinceQueued} ago and has now been executing for {executionTime}.");
                    }
                    else
                    {
                        diagnostics.Add($"Message {message} has been executing for {executionTime}.");
                    }

                    var response = messageFactory.CreateDiagnosticResponseMessage(message, isExecuting: true, isWaiting: false, diagnostics);
                    messageCenter.SendMessage(response);
                }
            }

            foreach (var running in _runningRequests)
            {
                var message = running.Key;
                var runDuration = running.Value;
                if (ReferenceEquals(message, _blockingRequest) || message.IsLocalOnly)
                {
                    continue;
                }

                // Check how long they've been executing.
                var executionTime = runDuration.Elapsed;
                if (executionTime >= slowRunningRequestDuration)
                {
                    // Interleaving message X has been executing for a long time
                    GetStatusList(ref diagnostics);
                    var messageDiagnostics = new List<string>(diagnostics)
                    {
                        $"Interleaving message {message} has been executing for {executionTime}."
                    };

                    var response = messageFactory.CreateDiagnosticResponseMessage(message, isExecuting: true, isWaiting: false, messageDiagnostics);
                    messageCenter.SendMessage(response);
                }
            }

            var queueLength = 1;
            foreach (var pair in _waitingRequests)
            {
                var message = pair.Message;
                if (message.IsLocalOnly)
                {
                    continue;
                }

                var queuedTime = pair.QueuedTime.Elapsed;
                if (queuedTime >= longQueueTimeDuration)
                {
                    // Message X has been enqueued on the target grain for Y and is currently position QueueLength in queue for processing.
                    GetStatusList(ref diagnostics);
                    var messageDiagnostics = new List<string>(diagnostics)
                    {
                       $"Message {message} has been enqueued on the target grain for {queuedTime} and is currently position {queueLength} in queue for processing."
                    };

                    var response = messageFactory.CreateDiagnosticResponseMessage(message, isExecuting: false, isWaiting: true, messageDiagnostics);
                    messageCenter.SendMessage(response);
                }

                queueLength++;
            }
        }

        void GetStatusList([NotNull] ref List<string>? diagnostics)
        {
            if (diagnostics is not null) return;

            diagnostics = new List<string>
            {
                ToDetailedString(),
                $"TaskScheduler status: {_workItemGroup.DumpStatus()}"
            };
        }
    }

    public override string ToString() => $"[Activation: {Address.SiloAddress}/{GrainId}{ActivationId}{GetActivationInfoString()} State={State}]";

    internal string ToDetailedString(bool includeExtraDetails = false)
    {
        lock (_lock)
        {
            var currentlyExecuting = includeExtraDetails ? _blockingRequest : null;
            return @$"[Activation: {Address.SiloAddress}/{GrainId}{ActivationId} {GetActivationInfoString()} State={State} NonReentrancyQueueSize={WaitingCount} NumRunning={_runningRequests.Count} IdlenessTimeSpan={GetIdleness()} CollectionAgeLimit={_shared.CollectionAgeLimit}{(currentlyExecuting != null ? " CurrentlyExecuting=" : null)}{currentlyExecuting}]";
        }
    }

    private string GetActivationInfoString()
    {
        var placement = PlacementStrategy?.GetType().Name;
        var grainTypeName = TryGetGrainTypeName();
        return grainTypeName is null ? $"#Placement={placement}" : $"#GrainType={grainTypeName} Placement={placement}";
    }

    private string? TryGetGrainTypeName()
    {
        return _shared.GrainTypeName ?? GrainInstance switch
        {
            { } grainInstance => RuntimeTypeNameFormatter.Format(grainInstance.GetType()),
            _ => null
        };
    }

    public void Dispose()
    {
#pragma warning disable RS0030 // IDisposable requires synchronously completing activation cleanup.
        DisposeAsync().AsTask().Wait();
#pragma warning restore RS0030
    }

    /// <summary>
    /// Disposes this activation, forcing it into <see cref="ActivationState.Disposing"/> if it has not reached that state.
    /// </summary>
    /// <remarks>
    /// Forced disposal, for example when the activation directory is disposed, preempts setup and teardown. Phases which have not run are skipped.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (!TryClaimDisposal())
        {
            return;
        }

        try
        {
            await DisposeResourcesAsync();
        }
        finally
        {
            lock (_lock)
            {
                SetState(ActivationState.Destroyed);
            }
        }
    }

    /// <summary>
    /// Disposes the grain instance and activation services. The caller has claimed disposal using <see cref="TryClaimDisposal"/>.
    /// </summary>
    private async ValueTask DisposeResourcesAsync()
    {
        Debug.Assert(State is ActivationState.Disposing);
        CancelPendingOperations();

        lock (_lock)
        {
            _shared.InternalRuntime.ActivationWorkingSet.OnDeactivated(this);
        }

        DisposeTimers();

        try
        {
            try
            {
                var activator = _shared.GetComponent(typeof(IGrainActivator)) as IGrainActivator;
                if (activator != null && GrainInstance is { } instance)
                {
                    await activator.DisposeInstance(this, instance);
                }
            }
            catch (ObjectDisposedException)
            {
            }
        }
        finally
        {
            try
            {
                if (GrainInstance is not null)
                {
                    _shared.OnDestroyActivation(this);
                }

                GetComponent<IActivationLifecycleObserver>()?.OnDestroyActivation(this);
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                // Setup stopped before rehydration consumed the migration context. Its disposal, like the scope's, cannot fail disposal.
                if (TakeRehydrationContext() is { } rehydrationContext)
                {
                    await DisposeAsync(rehydrationContext);
                }

                await DisposeAsync(_serviceScope);
            }
        }
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

    public (TExtension, TExtensionInterface) GetOrSetExtension<TExtension, TExtensionInterface>(Func<TExtension> newExtensionFunc)
        where TExtension : class, TExtensionInterface
        where TExtensionInterface : class, IGrainExtension
    {
        TExtension implementation;
        if (GetComponent<TExtensionInterface>() is object existing)
        {
            if (existing is TExtension typedResult)
            {
                implementation = typedResult;
            }
            else
            {
                throw new InvalidCastException($"Cannot cast existing extension of type {existing.GetType()} to target type {typeof(TExtension)}");
            }
        }
        else
        {
            implementation = newExtensionFunc();
            SetComponent<TExtensionInterface>(implementation);
        }

        var reference = GrainReference.Cast<TExtensionInterface>();
        return (implementation, reference);
    }

    public TExtensionInterface GetExtension<TExtensionInterface>()
        where TExtensionInterface : class, IGrainExtension
    {
        if (GetComponent<TExtensionInterface>() is TExtensionInterface result)
        {
            return result;
        }

        var implementation = ActivationServices.GetKeyedService<IGrainExtension>(typeof(TExtensionInterface));
        if (implementation is not TExtensionInterface typedResult)
        {
            throw new GrainExtensionNotInstalledException($"No extension of type {typeof(TExtensionInterface)} is installed on this instance and no implementations are registered for automated install");
        }

        SetComponent(typedResult);
        return typedResult;
    }

    bool IActivationWorkingSetMember.IsCandidateForRemoval(bool wouldRemove)
    {
        const int IdlenessLowerBound = 10_000;
        lock (_lock)
        {
            var inactive = IsInactive && _idleDuration.ElapsedMilliseconds > IdlenessLowerBound && _extras is not { KeepAliveHolds: > 0 };

            // This instance will remain in the working set if it is either not pending removal or if it is currently active.
            _isInWorkingSet = !wouldRemove || !inactive;
            return inactive;
        }
    }

    private async Task RunMessageLoop()
    {
        // Note that this loop never terminates. That might look strange, but there is a reason for it:
        // a grain must always accept and process any incoming messages. How a grain processes
        // those messages is up to the grain's state to determine. If the grain has not yet
        // completed activating, it will let the messages continue to queue up until it completes activation.
        // If the grain failed to activate, messages will be responded to with a rejection.
        // If the grain has terminated, messages will be forwarded on to a new instance of this grain.
        // The loop will eventually be garbage collected when the grain gets deactivated and there are no
        // rooted references to it.
        while (true)
        {
            try
            {
                if (!IsCurrentlyExecuting)
                {
                    bool hasPendingOperations;
                    lock (_lock)
                    {
                        hasPendingOperations = _pendingOperations is { Count: > 0 };
                    }

                    if (hasPendingOperations)
                    {
                        await ProcessOperationsAsync();
                    }
                }

                ProcessPendingRequests();

                await _workSignal.WaitAsync();
            }
            catch (Exception exception)
            {
                LogErrorInGrainMessageLoop(_shared.Logger, exception);
            }
        }

        void ProcessPendingRequests()
        {
            // Requests which this activation will not process are removed from the queue under the lock and rejected or forwarded
            // after it is released, since that can deliver messages to other activations and complete local requests, which can
            // queue further requests on this activation.
            List<Message>? unprocessableRequests = null;
            var i = 0;

            do
            {
                Message? message = null;
                Exception? rejection = null;
                lock (_lock)
                {
                    if (_waitingRequests.Count <= i)
                    {
                        break;
                    }

                    message = _waitingRequests[i].Message;

                    // If the activation is not active, reject all pending messages except for local-only messages.
                    // Local-only messages are used for internal system operations and are processed while the activation tears down, until it disposes.
                    if (State != ActivationState.Active && !(message.IsLocalOnly && State is > ActivationState.Active and < ActivationState.Disposing))
                    {
                        unprocessableRequests = DequeueUnprocessableRequests();
                        break;
                    }

                    try
                    {
                        if (!MayInvokeRequest(message))
                        {
                            // The activation is not able to process this message right now, so try the next message.
                            ++i;

                            if (_blockingRequest != null)
                            {
                                var currentRequestActiveTime = _busyDuration.Elapsed;
                                if (currentRequestActiveTime > _shared.MaxRequestProcessingTime && !IsStuckProcessingMessage)
                                {
                                    DeactivateStuckActivation();
                                }
                                else if (currentRequestActiveTime > _shared.MaxWarningRequestProcessingTime)
                                {
                                    // Consider: Handle long request detection for reentrant activations -- this logic only works for non-reentrant activations
                                    LogWarningDispatcher_ExtendedMessageProcessing(
                                        _shared.Logger,
                                        currentRequestActiveTime,
                                        new(this),
                                        _blockingRequest,
                                        message);
                                }
                            }

                            continue;
                        }

                        // If the current message is incompatible, deactivate this activation and eventually forward the message to a new incarnation.
                        if (message.InterfaceVersion > 0)
                        {
                            var compatibilityDirector = _shared.InternalRuntime.CompatibilityDirectorManager.GetDirector(message.InterfaceType);
                            var currentVersion = _shared.InternalRuntime.GrainVersionManifest.GetLocalVersion(message.InterfaceType);
                            if (!compatibilityDirector.IsCompatible(message.InterfaceVersion, currentVersion))
                            {
                                // Add this activation to cache invalidation headers.
                                message.AddToCacheInvalidationHeader(Address, validAddress: null);

                                var reason = new DeactivationReason(
                                    DeactivationReasonCode.IncompatibleRequest,
                                    $"Received incompatible request for interface {message.InterfaceType} version {message.InterfaceVersion}. This activation supports interface version {currentVersion}.");

                                var activityContext = message.RequestContextData.TryGetActivityContext();

                                Deactivate(reason, activityContext, cancellationToken: default);
                                return;
                            }
                        }
                    }
                    catch (Exception exception)
                    {
                        rejection = exception;
                    }

                    // Remove the message from the queue to either process or reject it.
                    _waitingRequests.RemoveAt(i);
                    if (rejection is null)
                    {
                        Debug.Assert(State == ActivationState.Active || message.IsLocalOnly);
                        RecordRunning(message, message.IsAlwaysInterleave);
                    }
                }

                if (rejection is not null)
                {
                    RejectRequest(message, rejection);
                    continue;
                }

                // Start invoking the message outside of the lock
                InvokeIncomingRequest(message);
            }
            while (true);

            if (unprocessableRequests is not null)
            {
                RejectOrForwardRequests(unprocessableRequests);
            }
        }

        void RecordRunning(Message message, bool isAlwaysInterleave)
        {
            var stopwatch = CoarseStopwatch.StartNew();
            _runningRequests.Add(message, stopwatch);

            if (isAlwaysInterleave) return;

            ++_runningNonAlwaysInterleaveCount;
            if (!message.IsReadOnly)
            {
                ++_runningNonAlwaysInterleaveWritableCount;
            }

            if (_blockingRequest != null) return;

            // This logic only works for non-reentrant activations
            // Consider: Handle long request detection for reentrant activations.
            _blockingRequest = message;
            _busyDuration = stopwatch;
        }

        // Called with the lock held. Dequeues the waiting requests once this activation can no longer process them,
        // or returns null while it may still process them.
        List<Message>? DequeueUnprocessableRequests()
        {
            if (State.IsSettingUp())
            {
                // Do nothing until the activation either becomes active or tears down.
                return null;
            }

            if (State < ActivationState.Disposing)
            {
                // Determine whether to declare this activation as stuck
                var deactivatingTime = GrainRuntime.TimeProvider.GetUtcNow().UtcDateTime - DeactivationStartTime!.Value;
                if (deactivatingTime > _shared.MaxRequestProcessingTime && !IsStuckDeactivating)
                {
                    IsStuckDeactivating = true;
                    if (DeactivationReason.Description is { Length: > 0 } && DeactivationReason.ReasonCode != DeactivationReasonCode.ActivationUnresponsive)
                    {
                        DeactivationReason = new(DeactivationReasonCode.ActivationUnresponsive,
                            $"{DeactivationReason.Description}. Activation {this} has been deactivating since {DeactivationStartTime.Value} and is likely stuck");
                    }

                    AbandonStuckDeactivatingActivation();
                }

                if (!IsStuckDeactivating && !IsStuckProcessingMessage)
                {
                    // Do not forward messages while the grain is still deactivating and has not been declared stuck, since they
                    // will be forwarded to the same grain instance.
                    return null;
                }
            }

            return TakeWaitingRequests();
        }

        void AbandonStuckDeactivatingActivation()
        {
            var forwardingAddress = ForwardingAddress;
            LogWarningAbandoningStuckDeactivatingActivation(_shared.Logger, this, forwardingAddress);

            // The migration target is not proven until deactivation reaches StartMigrationAsync.
            // If deactivation is stuck before then, re-address messages instead of forwarding to a
            // target which may not have a valid replacement activation.
            ForwardingAddress = null;
            UnregisterMessageTarget();
            _shared.InternalRuntime.GrainLocator.Unregister(Address, UnregistrationCause.Force).Ignore();
            GetDeactivationCompletionSource().TrySetResult(true);
        }

        bool MayInvokeRequest(Message incoming)
        {
            if (!IsCurrentlyExecuting)
            {
                return true;
            }

            // Otherwise, allow request invocation if the grain is reentrant or the message can be interleaved
            if (incoming.IsAlwaysInterleave)
            {
                return true;
            }

            if (_runningNonAlwaysInterleaveCount == 0
                || (incoming.IsReadOnly && _runningNonAlwaysInterleaveWritableCount == 0))
            {
                return true;
            }

            // Handle call-chain reentrancy
            if (incoming.GetReentrancyId() is Guid id
                && IsReentrantSection(id))
            {
                return true;
            }

            var canInterleave = GetComponent<GrainCanInterleave>();
            bool? incomingMayInterleave = null;
            foreach (var runningRequest in _runningRequests)
            {
                var runningMessage = runningRequest.Key;
                if (runningMessage.IsAlwaysInterleave
                    || (runningMessage.IsReadOnly && incoming.IsReadOnly))
                {
                    continue;
                }

                if (canInterleave is not null)
                {
                    try
                    {
                        incomingMayInterleave ??= canInterleave.MayInterleave(GrainInstance, incoming);
                        if (incomingMayInterleave.Value)
                        {
                            return true;
                        }

                        if (canInterleave.MayInterleave(GrainInstance, runningMessage))
                        {
                            continue;
                        }
                    }
                    catch (Exception exception)
                    {
                        LogErrorInvokingMayInterleavePredicate(_shared.Logger, exception, this, incoming);
                        throw;
                    }
                }

                _blockingRequest = runningMessage;
                _busyDuration = runningRequest.Value;
                return false;
            }

            return true;
        }

        async Task ProcessOperationsAsync()
        {
            object? op = null;
            while (true)
            {
                lock (_lock)
                {
                    Debug.Assert(_pendingOperations is not null);

                    // Remove the previous operation.
                    // Operations are not removed until they are completed, allowing for them to see each other.
                    // Eg, a deactivation request can see any on-going activation request and cancel it.
                    if (op is not null)
                    {
                        _pendingOperations.Dequeue();
                    }

                    // Try to get the next operation.
                    if (!_pendingOperations.TryPeek(out op))
                    {
                        _pendingOperations = null;
                        return;
                    }
                }

                try
                {
                    switch (op)
                    {
                        case Command.Rehydrate command:
                            AcceptRehydrationContext(command);
                            break;
                        case Command.Activate command:
                            await ActivateAsync(command.RequestContext, command.Metrics, command.CancellationToken).SuppressThrowing();
                            break;
                        case Command.Deactivate command:
                            await FinishDeactivating(command, command.CancellationToken).SuppressThrowing();
                            break;
                        case Command.Delay command:
                            await Task.Delay(command.Duration, GrainRuntime.TimeProvider, command.CancellationToken).SuppressThrowing();
                            break;
                        default:
                            throw new NotSupportedException($"Encountered unknown operation of type {op?.GetType().ToString() ?? "null"} {op}.");
                    }
                }
                catch (Exception exception)
                {
                    LogErrorInProcessOperationsAsync(_shared.Logger, exception, this);
                }
                finally
                {
                    if (op is not null)
                    {
                        await DisposeAsync(op);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Accepts the migration context which this activation rehydrates from. This runs while the activation is <see cref="ActivationState.Constructing"/>.
    /// </summary>
    /// <remarks>
    /// The previous registration is read now, so that directory registration replaces it and queued requests carry cache updates.
    /// Migration participants rehydrate from the context once the activation is registered, during <see cref="ActivationState.Rehydrating"/>.
    /// </remarks>
    private void AcceptRehydrationContext(Command.Rehydrate command)
    {
        var context = command.Context;
        try
        {
            lock (_lock)
            {
                if (State != ActivationState.Constructing || IsDeactivationRequested)
                {
                    LogIgnoringRehydrateAttempt(_shared.Logger, this, State);
                    if (GrainInstance is IGrainMigrationParticipant)
                    {
                        using var rehydrateSpan = StartRehydrateSpan();
                        rehydrateSpan?.SetTag(ActivityTagKeys.RehydrateIgnored, true);
                        rehydrateSpan?.SetTag(ActivityTagKeys.RehydrateIgnoredReason, $"State is {State}");
                    }

                    return;
                }

                if (context.TryGetValue(GrainAddressMigrationContextKey, out GrainAddress? previousRegistration) &&
                    previousRegistration is not null)
                {
                    if (previousRegistration.GrainId != GrainId)
                    {
                        throw new ArgumentException("The previous registration must identify the grain being rehydrated.", nameof(command));
                    }

                    PreviousRegistration = previousRegistration;
                    LogPreviousActivationAddress(_shared.Logger, previousRegistration);

                    foreach (var (message, _) in _waitingRequests)
                    {
                        message.AddToCacheInvalidationHeader(previousRegistration, Address);
                    }
                }

                // The activation owns the context until it rehydrates or disposes.
                _extras ??= new();
                _extras.RehydrationContext = command.TakeContext();
            }
        }
        catch (Exception exception)
        {
            LogErrorRehydratingActivation(_shared.Logger, exception);
        }
    }

    /// <summary>
    /// Restores migration participants from the accepted migration context, if there is one.
    /// </summary>
    private void RunRehydration()
    {
        // Forced disposal may have preempted rehydration, in which case disposal releases the context.
        if (State is not ActivationState.Rehydrating || TakeRehydrationContext() is not { } context)
        {
            return;
        }

        Activity? rehydrateSpan = null;
        try
        {
            LogRehydratingGrain(_shared.Logger, this);

            var grainMigrationParticipant = GrainInstance as IGrainMigrationParticipant;
            if (grainMigrationParticipant is not null)
            {
                rehydrateSpan = StartRehydrateSpan();
                if (PreviousRegistration is { } previousRegistration)
                {
                    rehydrateSpan?.SetTag(ActivityTagKeys.RehydratePreviousRegistration, previousRegistration.ToFullString());
                }
            }

            lock (_lock)
            {
                // Forced disposal enters Disposing under this lock before it disposes the grain and its services, so participants
                // rehydrate only while the activation is still Rehydrating. The context is released below either way.
                if (State is not ActivationState.Rehydrating)
                {
                    return;
                }

                if (_lifecycle is { } lifecycle)
                {
                    foreach (var participant in lifecycle.GetMigrationParticipants())
                    {
                        participant.OnRehydrate(context);
                    }
                }

                grainMigrationParticipant?.OnRehydrate(context);
            }

            LogRehydratedGrain(_shared.Logger);
            rehydrateSpan?.AddEvent(new ActivityEvent("rehydrated"));
        }
        catch (Exception exception)
        {
            LogErrorRehydratingActivation(_shared.Logger, exception);
            SetActivityError(rehydrateSpan, exception, ActivityErrorEvents.RehydrateError);
        }
        finally
        {
            rehydrateSpan?.Dispose();
            (context as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Takes ownership of the accepted migration context, if there is one.
    /// </summary>
    private IRehydrationContext? TakeRehydrationContext()
    {
        lock (_lock)
        {
            if (_extras?.RehydrationContext is not { } context)
            {
                return null;
            }

            _extras.RehydrationContext = null;
            return context;
        }
    }

    private Activity? StartRehydrateSpan()
    {
        var rehydrateSpan = _activationActivity is not null
            ? ActivitySources.LifecycleGrainSource.StartActivity(ActivityNames.ActivationRehydrate, ActivityKind.Internal, _activationActivity.Context)
            : ActivitySources.LifecycleGrainSource.StartActivity(ActivityNames.ActivationRehydrate, ActivityKind.Internal);
        rehydrateSpan?.SetTag(ActivityTagKeys.GrainId, GrainId.ToString());
        rehydrateSpan?.SetTag(ActivityTagKeys.GrainType, _shared.GrainTypeName);
        rehydrateSpan?.SetTag(ActivityTagKeys.SiloId, _shared.Runtime.SiloAddress.ToString());
        rehydrateSpan?.SetTag(ActivityTagKeys.ActivationId, ActivationId.ToString());
        return rehydrateSpan;
    }

    private void OnDehydrate(IDehydrationContext context)
    {
        LogDehydratingActivation(_shared.Logger);

        lock (_lock)
        {
            Debug.Assert(context is not null);

            if (IsUsingGrainDirectory)
            {
                context.TryAddValue(GrainAddressMigrationContextKey, Address);
            }

            Activity? dehydrateSpan = null;
            try
            {
                // Get the parent activity context from the dehydration context holder (captured when migration was initiated)
                var parentContext = DehydrationContext?.MigrationActivityContext;

                var grainMigrationParticipant = GrainInstance as IGrainMigrationParticipant;

                if (grainMigrationParticipant is not null)
                {
                    // Start a span for dehydration, parented to the migration request that triggered it
                    dehydrateSpan = parentContext.HasValue
                        ? ActivitySources.LifecycleGrainSource.StartActivity(ActivityNames.ActivationDehydrate,
                            ActivityKind.Internal, parentContext.Value)
                        : ActivitySources.LifecycleGrainSource.StartActivity(ActivityNames.ActivationDehydrate,
                            ActivityKind.Internal);
                    if (dehydrateSpan is { IsAllDataRequested: true })
                    {
                        dehydrateSpan.SetTag(ActivityTagKeys.GrainId, GrainId.ToString());
                        dehydrateSpan.SetTag(ActivityTagKeys.GrainType, _shared.GrainTypeName);
                        dehydrateSpan.SetTag(ActivityTagKeys.SiloId, _shared.Runtime.SiloAddress.ToString());
                        dehydrateSpan.SetTag(ActivityTagKeys.ActivationId, ActivationId.ToString());
                        if (ForwardingAddress is { } fwd)
                        {
                            dehydrateSpan.SetTag(ActivityTagKeys.MigrationTargetSilo, fwd.ToString());
                        }
                    }
                }

                // Note that these calls are in reverse order from Rehydrate, not for any particular reason other than symmetry.
                grainMigrationParticipant?.OnDehydrate(context);

                if (_lifecycle is { } lifecycle)
                {
                    foreach (var participant in lifecycle.GetMigrationParticipants())
                    {
                        participant.OnDehydrate(context);
                    }
                }
            }
            catch (Exception exception)
            {
                LogErrorDehydratingActivation(_shared.Logger, exception);
                SetActivityError(dehydrateSpan, exception, ActivityErrorEvents.DehydrateError);
            }
            finally
            {
                dehydrateSpan?.Dispose();
            }
        }

        LogDehydratedActivation(_shared.Logger);
    }


    /// <summary>
    /// Handle an incoming message and queue/invoke appropriate handler
    /// </summary>
    /// <param name="message"></param>
    private void InvokeIncomingRequest(Message message)
    {
        _shared.MessagingProcessingInstruments.OnDispatcherMessageProcessedOk(message);

        try
        {
            var task = _shared.InternalRuntime.RuntimeClient.Invoke(this, message);

            // Note: This runs for all outcomes - both Success or Fault
            if (task.IsCompleted)
            {
                OnCompletedRequest(message);
            }
            else
            {
                _ = OnCompleteAsync(this, message, task);
            }
        }
        catch
        {
            OnCompletedRequest(message);
        }

        static async Task OnCompleteAsync(ActivationData activation, Message message, Task task)
        {
            try
            {
                await task;
            }
            catch
            {
            }
            finally
            {
                activation.OnCompletedRequest(message);
            }
        }
    }

    /// <summary>
    /// Invoked when an activation has finished a transaction and may be ready for additional transactions
    /// </summary>
    /// <param name="message">The message that has just completed processing.</param>
    private void OnCompletedRequest(Message message)
    {
        lock (_lock)
        {
            if (_runningRequests.Remove(message) && !message.IsAlwaysInterleave)
            {
                --_runningNonAlwaysInterleaveCount;
                if (!message.IsReadOnly)
                {
                    --_runningNonAlwaysInterleaveWritableCount;
                }

                Debug.Assert(_runningNonAlwaysInterleaveCount >= 0);
                Debug.Assert(_runningNonAlwaysInterleaveWritableCount >= 0);
            }

            // If the message is meant to keep the activation active, reset the idle timer and ensure the activation
            // is in the activation working set.
            if (message.IsKeepAlive)
            {
                MarkActive();
            }

            // The below logic only works for non-reentrant activations
            if (_blockingRequest is null || message.Equals(_blockingRequest))
            {
                _blockingRequest = null;
                _busyDuration = default;
            }
        }

        // Signal the message pump to see if there is another request which can be processed now that this one has completed
        _workSignal.Signal();
    }

    /// <summary>
    /// Restarts the idle period and ensures the activation is in the working set. Must be called under <see cref="_lock"/>.
    /// </summary>
    private void MarkActive()
    {
        _idleDuration = CoarseStopwatch.StartNew();

        if (!_isInWorkingSet)
        {
            _isInWorkingSet = true;
            _shared.InternalRuntime.ActivationWorkingSet.OnActive(this);
        }
    }

    public void ReceiveMessage(object message) => ReceiveMessage((Message)message);
    public void ReceiveMessage(Message message)
    {
        _shared.InternalRuntime.MessagingTrace.OnDispatcherReceiveMessage(message);

        // Don't process messages that have already timed out
        if (message.IsExpired)
        {
            _shared.MessagingProcessingInstruments.OnDispatcherMessageProcessedError(message);
            _shared.InternalRuntime.MessagingTrace.OnDropExpiredMessage(message, MessagingInstruments.Phase.Dispatch);
            message.Dispose();
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

    public void ReceiveLocalRequest(ILocalRequest request, LocalRequestOptions options)
    {
        var message = _shared.InternalRuntime.RuntimeClient.CreateLocalRequest(this, request, options);
        _shared.InternalRuntime.MessagingTrace.OnDispatcherReceiveMessage(message);
        ReceiveRequest(message);
    }

    private void ReceiveResponse(Message message)
    {
        var state = State;
        if (state.IsDisposingOrDestroyed())
        {
            _shared.InternalRuntime.MessagingTrace.OnDispatcherReceiveInvalidActivation(message, state);
            // Note that we always process responses, even if the activation is disposed.
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
            _waitingRequests.Add((message, CoarseStopwatch.StartNew()));
        }

        _workSignal.Signal();
    }

    /// <summary>
    /// Rejects a request which this activation removed from its queue without processing it.
    /// </summary>
    private void RejectRequest(Message message, Exception exception)
    {
        if (message.IsLocalOnly)
        {
            LocalRequests.Reject(message, exception, _shared.Logger);
        }
        else
        {
            _shared.InternalRuntime.MessageCenter.RejectMessage(message, Message.RejectionTypes.Transient, exception);
        }
    }

    /// <summary>
    /// Removes all queued requests. The caller holds <see cref="_lock"/>.
    /// </summary>
    /// <returns>The requests, or <see langword="null"/> if there are none.</returns>
    private List<Message>? TakeWaitingRequests()
    {
        if (_waitingRequests.Count == 0)
        {
            return null;
        }

        var requests = new List<Message>(_waitingRequests.Count);
        foreach (var (message, _) in _waitingRequests)
        {
            requests.Add(message);
        }

        _waitingRequests.Clear();
        return requests;
    }

    /// <summary>
    /// Removes and rejects local requests, which never leave the activation.
    /// </summary>
    private void RejectLocalOnlyRequests(List<Message> requests)
    {
        foreach (var message in requests)
        {
            if (message.IsLocalOnly)
            {
                RejectRequest(message, new OrleansMessageRejectionException($"Activation {this} is no longer valid."));
            }
        }

        requests.RemoveAll(static message => message.IsLocalOnly);
    }

    /// <summary>
    /// Rejects or forwards requests which this activation dequeued once it could no longer process them.
    /// </summary>
    /// <remarks>
    /// Requests are forwarded if this was a duplicate activation or it was at some point successfully activated, and rejected otherwise.
    /// Local requests never leave the activation, so they are rejected.
    /// </remarks>
    private void RejectOrForwardRequests(List<Message> requests)
    {
        RejectLocalOnlyRequests(requests);
        if (requests.Count == 0)
        {
            return;
        }

        var reject = DeactivationException is not null && ForwardingAddress is null;
        if (reject)
        {
            LogRejectAllQueuedMessages(_shared.Logger, requests.Count, this);
        }
        else if (_shared.Logger.IsEnabled(LogLevel.Debug))
        {
            if (ForwardingAddress is { } address)
            {
                LogReroutingMessages(_shared.Logger, requests.Count, this, address);
            }
            else
            {
                LogReroutingMessagesNoForwarding(_shared.Logger, requests.Count, this);
            }
        }

        _shared.InternalRuntime.GrainLocator.InvalidateCache(Address);
        _shared.InternalRuntime.MessageCenter.ProcessRequestsToInvalidActivation(
            requests,
            Address,
            ForwardingAddress,
            DeactivationReason.Description,
            DeactivationException,
            rejectMessages: reject);
    }

    #region Activation
    public void Rehydrate(IRehydrationContext context)
    {
        ScheduleOperation(new Command.Rehydrate(context));
    }

    public void Activate(Dictionary<string, object>? requestContext, CancellationToken cancellationToken)
    {
        var metrics = CatalogInstruments.ActivationMetricTracker.Start(_shared.CatalogInstruments, IsUsingGrainDirectory, _shared.GrainTypeMetricName);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_shared.InternalRuntime.CollectionOptions.Value.ActivationTimeout);

        ScheduleOperation(new Command.Activate(requestContext, cts, metrics));
    }

    private async Task ActivateAsync(Dictionary<string, object>? requestContextData, CatalogInstruments.ActivationMetricTracker activationMetrics, CancellationToken cancellationToken)
    {
        // An activation which was deactivated or disposed before registration has nothing to register. The deactivation operation or disposal tears it down.
        if (!TryAdvanceSetup(ActivationState.Constructing))
        {
            LogIgnoringActivateAttempt(_shared.Logger, this, State);
            return;
        }

        _activationActivity?.AddEvent(new ActivityEvent("activation-start"));
        try
        {
            if (IsUsingGrainDirectory)
            {
                var (registered, registrationException) = await RegisterAsync(cancellationToken);
                if (!registered)
                {
                    // Deactivation cancels the activation token, so capture the directory outcome first.
                    activationMetrics.DirectoryRegistrationFailed(registrationException, cancellationToken.IsCancellationRequested);
                    var reason = new DeactivationReason(DeactivationReasonCode.DirectoryFailure, registrationException, "Failed to register activation in grain directory.");
                    if (registrationException is null)
                    {
                        // A competing registration means that this activation never registered.
                        FailSetup(reason, cancellationToken);
                        SetActivityError(_activationActivity, ActivityErrorEvents.ActivationCancelled);
                    }
                    else
                    {
                        // A registration which threw may have committed, so teardown treats registration as completed.
                        Deactivate(reason, cancellationToken);
                        SetActivityError(_activationActivity, registrationException, ActivityErrorEvents.ActivationCancelled);
                    }

                    return;
                }
            }

            if (!TryAdvanceSetup(ActivationState.Registering))
            {
                RecordSetupCanceled();
                return;
            }

            RunRehydration();
            if (!TryAdvanceSetup(ActivationState.Rehydrating))
            {
                RecordSetupCanceled();
                return;
            }

            _activationActivity?.AddEvent(new ActivityEvent("state-activating"));
            LogActivatingGrain(_shared.Logger, this);
            RequestContextExtensions.Import(requestContextData);
            await StartLifecycleAsync(cancellationToken);
            if (!TryAdvanceSetup(ActivationState.Starting))
            {
                RecordSetupCanceled();
                return;
            }

            await RunOnActivateAsync(cancellationToken);
            if (!TryAdvanceSetup(ActivationState.Activating))
            {
                RecordSetupCanceled();
                return;
            }

            _activationActivity?.AddEvent(new ActivityEvent("state-valid"));
            _activationActivity?.Dispose();
            _activationActivity = null;

            GrainLifecycleEvents.EmitActivated(this);

            LogFinishedActivatingGrain(_shared.Logger, this);
            activationMetrics.Succeeded();
        }
        catch (Exception exception) when (State is ActivationState.Activating && IsObservedCancellation(exception, cancellationToken))
        {
            if (exception is ObjectDisposedException ode)
            {
                LogActivationDisposedObjectAccessed(_shared.Logger, ode.ObjectName, this);
                FailSetup(
                    new(DeactivationReasonCode.RuntimeRequested, ode, $"Disposed object {ode.ObjectName} referenced after cancellation of activation was requested."),
                    CancellationToken.None);
            }
            else
            {
                FailSetup(new(DeactivationReasonCode.RuntimeRequested, exception, "Activation was cancelled by the runtime."), CancellationToken.None);
            }

            RecordSetupCanceled(exception);
        }
        catch (Exception exception)
        {
            _shared.CatalogInstruments.OnActivationFailedToActivate(_shared.GrainTypeMetricName);
            activationMetrics.Failed(cancellationToken.IsCancellationRequested);
            var sourceException = (exception as OrleansLifecycleCanceledException)?.InnerException ?? exception;
            LogErrorActivatingGrain(_shared.Logger, sourceException, this);
            if (!cancellationToken.IsCancellationRequested)
            {
                ScheduleOperation(new Command.Delay(TimeSpan.FromSeconds(5)));
            }

            FailSetup(new(DeactivationReasonCode.ActivationFailed, sourceException, "Failed to activate grain."), CancellationToken.None);
            SetActivityError(_activationActivity, ActivityErrorEvents.ActivationFailed);
            _activationActivity?.Dispose();
            _activationActivity = null;
        }
        finally
        {
            activationMetrics.Record();
            _workSignal.Signal();
        }

        // Records that setup stopped because deactivation was requested or the activation was disposed.
        void RecordSetupCanceled(Exception? exception = null)
        {
            _shared.CatalogInstruments.OnActivationFailedToActivate(_shared.GrainTypeMetricName);
            if (exception is not null)
            {
                SetActivityError(_activationActivity, exception, ActivityErrorEvents.ActivationCancelled);
            }
            else
            {
                SetActivityError(_activationActivity, ActivityErrorEvents.ActivationCancelled);
            }

            LogActivationCancelled(_shared.Logger, this, cancellationToken.IsCancellationRequested,
                DeactivationReason.ReasonCode, DeactivationReason.Description, ForwardingAddress);
            _activationActivity?.Dispose();
            _activationActivity = null;
            activationMetrics.Canceled();
        }
    }

    /// <summary>
    /// Gets whether user code observed activation cancellation, either by throwing <see cref="OperationCanceledException"/> or by accessing an
    /// object which was disposed because of cancellation.
    /// </summary>
    private static bool IsObservedCancellation(Exception exception, CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested && exception is ObjectDisposedException or OperationCanceledException;

    /// <summary>
    /// Registers this activation in the grain directory, replacing its previous registration.
    /// </summary>
    /// <returns>Whether the activation registered, and the exception which prevented registration. A registration which failed without an
    /// exception found a competing registration, whose silo becomes the <see cref="ForwardingAddress"/>.</returns>
    private async Task<(bool Registered, Exception? Exception)> RegisterAsync(CancellationToken cancellationToken)
    {
        using var registerSpan = _activationActivity is not null
            ? ActivitySources.LifecycleGrainSource.StartActivity(ActivityNames.RegisterDirectoryEntry, ActivityKind.Internal, _activationActivity.Context)
            : ActivitySources.LifecycleGrainSource.StartActivity(ActivityNames.RegisterDirectoryEntry, ActivityKind.Internal);
        registerSpan?.SetTag(ActivityTagKeys.GrainId, GrainId.ToString());
        registerSpan?.SetTag(ActivityTagKeys.SiloId, _shared.Runtime.SiloAddress.ToString());
        registerSpan?.SetTag(ActivityTagKeys.ActivationId, ActivationId.ToString());
        registerSpan?.SetTag(ActivityTagKeys.DirectoryPreviousRegistrationPresent, PreviousRegistration is not null);
        var previousRegistration = PreviousRegistration;
        var verifiedRecoveryMembershipVersion = 0L;

        try
        {
            while (true)
            {
                LogRegisteringGrain(_shared.Logger, this, previousRegistration);

                var result = await _shared.InternalRuntime.GrainLocator.Register(Address, previousRegistration, cancellationToken);
                if (Address.Matches(result))
                {
                    Address = result;

                    // If DGD recovery advanced while this registration was being committed, re-register
                    // against the recovered view before this activation can become valid.
                    if (_shared.GrainDirectory is DistributedGrainDirectory distributedGrainDirectory)
                    {
                        var recoveryMembershipVersion = distributedGrainDirectory.RecoveryMembershipVersion;
                        if (recoveryMembershipVersion > verifiedRecoveryMembershipVersion
                            && recoveryMembershipVersion > result.MembershipVersion.Value)
                        {
                            verifiedRecoveryMembershipVersion = recoveryMembershipVersion;
                            previousRegistration = result;
                            _activationActivity?.AddEvent(new ActivityEvent("directory-register-retry-recovery"));
                            registerSpan?.AddEvent(new ActivityEvent("retry-recovery"));
                            continue;
                        }
                    }

                    _activationActivity?.AddEvent(new ActivityEvent("directory-register-success"));
                    registerSpan?.AddEvent(new ActivityEvent("success"));
                    registerSpan?.SetTag(ActivityTagKeys.DirectoryRegisteredAddress, result.ToFullString());
                    return (true, null);
                }

                if (result?.SiloAddress is { } registeredSilo && registeredSilo.Equals(Address.SiloAddress))
                {
                    previousRegistration = result;
                    LogAttemptToRegisterWithPreviousActivation(_shared.Logger, GrainId, result);
                    _activationActivity?.AddEvent(new ActivityEvent("directory-register-retry-previous"));
                    registerSpan?.AddEvent(new ActivityEvent("retry-previous"));
                    continue;
                }

                ForwardingAddress = result?.SiloAddress;
                if (ForwardingAddress is { } address)
                {
                    DeactivationReason = new(DeactivationReasonCode.DuplicateActivation, $"This grain is active on another host ({address}).");
                    registerSpan?.SetTag(ActivityTagKeys.DirectoryForwardingAddress, address.ToString());
                }

                _shared.CatalogInstruments.OnActivationConcurrentRegistrationAttempt(_shared.GrainTypeMetricName);
                LogDuplicateActivation(_shared.Logger, Address, ForwardingAddress, GrainInstance?.GetType(), new(Address), WaitingCount);
                _activationActivity?.AddEvent(new ActivityEvent("duplicate-activation"));
                registerSpan?.AddEvent(new ActivityEvent("duplicate"));
                return (false, null);
            }
        }
        catch (Exception exception)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                LogFailedToRegisterGrain(_shared.Logger, exception, this);
            }

            _activationActivity?.AddEvent(new ActivityEvent("directory-register-failed"));
            SetActivityError(registerSpan, exception, ActivityErrorEvents.DirectoryRegisterFailed);
            return (false, exception);
        }
    }

    private async Task StartLifecycleAsync(CancellationToken cancellationToken)
    {
        if (_lifecycle is not { } lifecycle)
        {
            return;
        }

        try
        {
            _activationActivity?.AddEvent(new ActivityEvent("lifecycle-start"));

            // The activation timeout cancels this token. The runtime awaits user code to completion, so the activation keeps
            // its registration and services while that code runs. Activation fails if the token was canceled.
            await lifecycle.OnStart(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _activationActivity?.AddEvent(new ActivityEvent("lifecycle-started"));
        }
        catch (Exception exception)
        {
            LogErrorStartingLifecycle(_shared.Logger, exception, this);
            _activationActivity?.AddEvent(new ActivityEvent("lifecycle-start-failed"));
            throw;
        }
    }

    private async Task RunOnActivateAsync(CancellationToken cancellationToken)
    {
        if (GrainInstance is not IGrainBase grainBase)
        {
            return;
        }

        using var onActivateSpan = _activationActivity is not null
            ? ActivitySources.LifecycleGrainSource.StartActivity(ActivityNames.OnActivate, ActivityKind.Internal, _activationActivity.Context)
            : ActivitySources.LifecycleGrainSource.StartActivity(ActivityNames.OnActivate, ActivityKind.Internal);
        if (onActivateSpan is { IsAllDataRequested: true })
        {
            onActivateSpan.SetTag(ActivityTagKeys.GrainId, GrainId.ToString());
            onActivateSpan.SetTag(ActivityTagKeys.GrainType, _shared.GrainTypeName ?? GrainInstance.GetType().FullName);
            onActivateSpan.SetTag(ActivityTagKeys.SiloId, _shared.Runtime.SiloAddress.ToString());
            onActivateSpan.SetTag(ActivityTagKeys.ActivationId, ActivationId.ToString());
        }

        try
        {
            await grainBase.OnActivateAsync(cancellationToken);

            // Activation fails if its token was canceled, even if OnActivateAsync returned normally.
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception exception) when (!IsObservedCancellation(exception, cancellationToken))
        {
            LogErrorInGrainMethod(_shared.Logger, exception, nameof(IGrainBase.OnActivateAsync), this);
            SetActivityError(onActivateSpan, exception, ActivityErrorEvents.OnActivateFailed);
            throw;
        }
    }

    private void SetActivityError(Activity? erroredActivity, string? errorEventName)
    {
        if (erroredActivity is { } activity)
        {
            activity.SetStatus(ActivityStatusCode.Error, errorEventName);
        }
    }

    private void SetActivityError(Activity? erroredActivity, Exception exception, string? errorEventName)
    {
        if (erroredActivity is { } activity)
        {
            activity.SetStatus(ActivityStatusCode.Error, errorEventName);
            activity.SetTag(ActivityTagKeys.ExceptionType, exception.GetType().FullName);
            activity.SetTag(ActivityTagKeys.ExceptionMessage, exception.Message);
        }
    }

    #endregion

    #region Deactivation

    /// <summary>
    /// Runs teardown, from the phase where it starts through <see cref="ActivationState.Destroyed"/>.
    /// </summary>
    private async Task FinishDeactivating(Command.Deactivate deactivateCommand, CancellationToken cancellationToken)
    {
        using var _ = deactivateCommand.Activity;

        var deactivationMetrics = CatalogInstruments.DeactivationMetricTracker.Start(_shared.CatalogInstruments, _shared.GrainTypeMetricName);
        ActivationMigration? migration = null;
        try
        {
            bool completedActivation;
            lock (_lock)
            {
                // Operations run one at a time, so setup has stopped. Unless the phase which it was running failed, which entered teardown,
                // the activation is in the last phase which completed.
                if (State.IsSettingUp())
                {
                    SetState(State.GetTeardownEntry());
                }

                completedActivation = State is ActivationState.Deactivating;
            }

            try
            {
                LogCompletingDeactivation(_shared.Logger, this);

                // Stop timers from firing.
                DisposeTimers();

                // Teardown continues past errors in grain code, but an activation whose grain code failed does not migrate its state.
                var encounteredError = false;
                for (var phase = State; phase is > ActivationState.Active and < ActivationState.Disposing; phase = State)
                {
                    switch (phase)
                    {
                        case ActivationState.Deactivating:
                            encounteredError |= !await RunOnDeactivateAsync(deactivateCommand.Activity, cancellationToken);
                            break;
                        case ActivationState.Stopping:
                            encounteredError |= !await StopLifecycleAsync(cancellationToken);
                            break;
                        case ActivationState.Dehydrating when !encounteredError:
                            migration = await DehydrateAsync(completedActivation, cancellationToken);
                            break;
                        case ActivationState.Unregistering when migration is not { Completion.IsCompletedSuccessfully: true }:
                            // A migrated activation's registration is replaced by the migration target.
                            await UnregisterAsync(cancellationToken);
                            break;
                    }

                    AdvanceTeardown(phase);
                }
            }
            catch (Exception ex)
            {
                SetActivityError(deactivateCommand.Activity, ex, "Error in FinishDeactivating");
                LogErrorDeactivating(_shared.Logger, ex, this);
            }

            deactivationMetrics = RecordShutdownCause(deactivationMetrics, migrated: migration is { Completion.IsCompletedSuccessfully: true });

            // Forced disposal may have disposed this activation already, in which case teardown completes its remaining steps without disposing again.
            var ownsDisposal = TryClaimDisposal();
            if (ownsDisposal)
            {
                try
                {
                    await DisposeResourcesAsync();
                }
                catch (Exception exception)
                {
                    SetActivityError(deactivateCommand.Activity, exception, "Error in FinishDeactivating");
                    LogExceptionDisposing(_shared.Logger, exception, this);
                }
            }

            if (DeactivationStartTime is not null)
            {
                GrainLifecycleEvents.EmitDeactivated(this, DeactivationReason);
            }

            deactivationMetrics = deactivationMetrics.Record();

            // The activation leaves the catalog only now, so the catalog awaits all of its deactivation, and no new local activation of the grain is created before it completes.
            // Leaving, forwarding queued requests, and signaling deactivation are synchronous, and forwarding follows leaving so that forwarded requests do not return to this activation.
            UnregisterMessageTarget();
            try
            {
                if (migration is not null)
                {
                    _shared.InternalRuntime.GrainLocator.InvalidateCache(Address);
                    migration.Deliver();
                }
            }
            finally
            {
                try
                {
                    // Requests outside the original handoff remain owned by this activation.
                    List<Message>? queued;
                    lock (_lock)
                    {
                        queued = TakeWaitingRequests();
                    }

                    if (queued is not null)
                    {
                        RejectOrForwardRequests(queued);
                    }
                }
                finally
                {
                    if (ownsDisposal)
                    {
                        lock (_lock)
                        {
                            SetState(ActivationState.Destroyed);
                        }
                    }

                    GetDeactivationCompletionSource().TrySetResult(true);
                }
            }

            _workSignal.Signal();
        }
        finally
        {
            deactivationMetrics.RecordIfNeeded();
        }
    }

    /// <summary>
    /// Runs <see cref="IGrainBase.OnDeactivateAsync"/>.
    /// </summary>
    /// <returns><see langword="false"/> if it threw; otherwise, <see langword="true"/>.</returns>
    private async Task<bool> RunOnDeactivateAsync(Activity? deactivateActivity, CancellationToken cancellationToken)
    {
        if (GrainInstance is not IGrainBase grainBase)
        {
            return true;
        }

        using var onDeactivateSpan = deactivateActivity is not null
            ? ActivitySources.LifecycleGrainSource.StartActivity(ActivityNames.OnDeactivate, ActivityKind.Internal, parentContext: deactivateActivity.Context)
            : ActivitySources.LifecycleGrainSource.StartActivity(ActivityNames.OnDeactivate, ActivityKind.Internal);
        if (onDeactivateSpan is { IsAllDataRequested: true })
        {
            onDeactivateSpan.SetTag(ActivityTagKeys.GrainId, GrainId.ToString());
            onDeactivateSpan.SetTag(ActivityTagKeys.GrainType, _shared.GrainTypeName ?? GrainInstance.GetType().FullName);
            onDeactivateSpan.SetTag(ActivityTagKeys.SiloId, _shared.Runtime.SiloAddress.ToString());
            onDeactivateSpan.SetTag(ActivityTagKeys.ActivationId, ActivationId.ToString());
            onDeactivateSpan.SetTag(ActivityTagKeys.DeactivationReason, DeactivationReason.ToString());
        }

        try
        {
            LogBeforeOnDeactivateAsync(_shared.Logger, this);

            // The deactivation timeout cancels this token. The runtime awaits user code to completion before it
            // unregisters the activation or disposes its services.
            await grainBase.OnDeactivateAsync(DeactivationReason, cancellationToken);

            LogAfterOnDeactivateAsync(_shared.Logger, this);
            return true;
        }
        catch (Exception exception)
        {
            LogErrorInGrainMethod(_shared.Logger, exception, nameof(IGrainBase.OnDeactivateAsync), this);
            SetActivityError(onDeactivateSpan, exception, ActivityErrorEvents.OnDeactivateFailed);
            return false;
        }
    }

    /// <summary>
    /// Stops the grain lifecycle stages which started. Stages which never started are ignored.
    /// </summary>
    /// <returns><see langword="false"/> if stopping threw; otherwise, <see langword="true"/>.</returns>
    private async Task<bool> StopLifecycleAsync(CancellationToken cancellationToken)
    {
        if (_lifecycle is not { } lifecycle)
        {
            return true;
        }

        try
        {
            await lifecycle.OnStop(cancellationToken);
            return true;
        }
        catch (Exception exception)
        {
            LogErrorStoppingLifecycle(_shared.Logger, exception, this);
            return false;
        }
    }

    /// <summary>
    /// Migrates this activation if migration was requested or its silo is retiring.
    /// </summary>
    /// <param name="completedActivation">Whether the activation completed activation, which retirement requires, since an interrupted
    /// activation may hold partially initialized state.</param>
    /// <param name="cancellationToken">The token which cancels a migration which has not been sent.</param>
    /// <returns>The migration, which completes successfully once the target accepts it, or <see langword="null"/> if none was sent.</returns>
    private async Task<ActivationMigration?> DehydrateAsync(bool completedActivation, CancellationToken cancellationToken)
    {
        // When its silo retires, an activation which still has queued requests migrates, carrying the requests with it.
        if (completedActivation && DehydrationContext is null && WaitingCount > 0 && MigratesOnRetirement())
        {
            DehydrationContext = new(_shared.SerializerSessionPool, requestContext: null);
        }

        if (DehydrationContext is not { } context || _shared.MigrationManager is not { } migrationManager || cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        ActivationMigration? migration = null;
        try
        {
            if (ForwardingAddress is null)
            {
                var selectedAddress = await PlaceMigratingGrainAsync(context.RequestContext, cancellationToken);
                if (selectedAddress is null)
                {
                    return null;
                }

                ForwardingAddress = selectedAddress;
            }

            // Populate the dehydration context.
            if (context.RequestContext is { } requestContext)
            {
                RequestContextExtensions.Import(requestContext);
            }

            OnDehydrate(context.MigrationContext);

            // Send the dehydration context and queued requests to the target host, which admits them through its ordinary request queue.
            // This activation remains in the catalog until the migration completes, so that the requests which reach this silo for the grain reach it.
            // Cancellation stops a migration which has not been sent. One which has been sent completes when the target responds.
            migration = migrationManager.StartMigration(ForwardingAddress, Address, context.MigrationContext, TakeRequestsForMigration(), cancellationToken);
            await migration.Completion;
            _shared.InternalRuntime.GrainLocator.UpdateCache(GrainId, ForwardingAddress);
        }
        catch (Exception exception)
        {
            LogFailedToMigrateActivation(_shared.Logger, exception, this);
        }

        return migration;
    }

    private async Task UnregisterAsync(CancellationToken cancellationToken)
    {
        // An activation which was deactivated because of a directory failure does not unregister: either its registration failed,
        // or the silo which owned its directory partition failed, removing the registration.
        if (!IsUsingGrainDirectory || cancellationToken.IsCancellationRequested || DeactivationReason.ReasonCode is DeactivationReasonCode.DirectoryFailure)
        {
            return;
        }

        try
        {
            await _shared.InternalRuntime.GrainLocator.Unregister(Address, UnregistrationCause.Force).WaitAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                LogFailedToUnregisterActivation(_shared.Logger, exception, this);
            }
        }
    }

    private CatalogInstruments.DeactivationMetricTracker RecordShutdownCause(CatalogInstruments.DeactivationMetricTracker metrics, bool migrated)
    {
        if (IsStuckDeactivating)
        {
            _shared.CatalogInstruments.ActivationShutdownViaDeactivateStuckActivation(_shared.GrainTypeMetricName);
            return metrics.DeactivateStuckActivation();
        }

        if (migrated)
        {
            _shared.CatalogInstruments.ActivationShutdownViaMigration(_shared.GrainTypeMetricName);
            return metrics.Migration();
        }

        if (_isInWorkingSet)
        {
            _shared.CatalogInstruments.ActivationShutdownViaDeactivateOnIdle(_shared.GrainTypeMetricName);
            return metrics.DeactivateOnIdle();
        }

        _shared.CatalogInstruments.ActivationShutdownViaCollection(_shared.GrainTypeMetricName);
        return metrics.Collection();
    }

    private TaskCompletionSource<bool> GetDeactivationCompletionSource()
    {
        lock (_lock)
        {
            _extras ??= new();
            return _extras.DeactivationTask ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    ValueTask IGrainManagementExtension.DeactivateOnIdle() =>
        ((IGrainManagementExtension)this).DeactivateOnIdle(default);

    ValueTask IGrainManagementExtension.DeactivateOnIdle(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Deactivate(new(DeactivationReasonCode.ApplicationRequested, $"{nameof(IGrainManagementExtension.DeactivateOnIdle)} was called."), cancellationToken);
        return default;
    }

    ValueTask IGrainManagementExtension.MigrateOnIdle() =>
        ((IGrainManagementExtension)this).MigrateOnIdle(default);

    async ValueTask IGrainManagementExtension.MigrateOnIdle(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var requestContextData = RequestContext.CallContextData?.Value.Values;
        var selectedAddress = await PlaceMigratingGrainAsync(requestContextData, cancellationToken);
        if (selectedAddress is null)
        {
            return;
        }

        // Only migrate if a different silo was selected.
        ForwardingAddress = selectedAddress;
        LogDebugMigrating(_shared.Logger, GrainId, ForwardingAddress);
        Migrate(requestContextData, cancellationToken);
    }

    private async ValueTask<SiloAddress?> PlaceMigratingGrainAsync(Dictionary<string, object>? requestContextData, CancellationToken cancellationToken)
    {
        try
        {
            var placementService = _shared.Runtime.ServiceProvider.GetRequiredService<PlacementService>();
            var selectedAddress = await placementService.PlaceGrainAsync(GrainId, requestContextData, PlacementStrategy)
                .WaitAsync(cancellationToken);

            if (selectedAddress is null)
            {
                // No appropriate silo was selected for this grain.
                LogDebugPlacementStrategyFailedToSelectDestination(_shared.Logger, PlacementStrategy, GrainId);
                return null;
            }
            else if (selectedAddress.Equals(_shared.Runtime.SiloAddress))
            {
                // This could be because this is the only (compatible) silo for the grain or because the placement director chose this
                // silo for some other reason.
                LogDebugPlacementStrategySelectedCurrentSilo(_shared.Logger, PlacementStrategy, GrainId);
                return null;
            }

            return selectedAddress;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogErrorSelectingMigrationDestination(_shared.Logger, exception, GrainId);
            return null;
        }
    }

    private void UnregisterMessageTarget()
    {
        _shared.InternalRuntime.Catalog.UnregisterMessageTarget(this);
    }

    void ICallChainReentrantGrainContext.OnEnterReentrantSection(Guid reentrancyId)
    {
        var tracker = GetComponent<ReentrantRequestTracker>();
        if (tracker is null)
        {
            tracker = new ReentrantRequestTracker();
            SetComponent(tracker);
        }

        tracker.EnterReentrantSection(reentrancyId);
    }

    void ICallChainReentrantGrainContext.OnExitReentrantSection(Guid reentrancyId)
    {
        var tracker = GetComponent<ReentrantRequestTracker>();
        if (tracker is null)
        {
            throw new InvalidOperationException("Attempted to exit reentrant section without entering it.");
        }

        tracker.LeaveReentrantSection(reentrancyId);
    }

    private bool IsReentrantSection(Guid reentrancyId)
    {
        if (reentrancyId == Guid.Empty)
        {
            return false;
        }

        var tracker = GetComponent<ReentrantRequestTracker>();
        if (tracker is null)
        {
            return false;
        }

        return tracker.IsReentrantSectionActive(reentrancyId);
    }

    ValueTask IGrainCallCancellationExtension.CancelRequestAsync(
        GrainId senderGrainId,
        CorrelationId messageId,
        CancellationToken cancellationToken)
        => this.RunOrQueueTask(
            static state => state.self.CancelRequestAsyncCore(state.senderGrainId, state.messageId, state.cancellationToken),
            (self: this, senderGrainId, messageId, cancellationToken));

    private ValueTask CancelRequestAsyncCore(
        GrainId senderGrainId,
        CorrelationId messageId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryCancelRequest())
        {
            // The message being canceled may not have arrived yet, so retry a few times.
            return RetryCancellationAfterDelay();
        }

        return ValueTask.CompletedTask;

        async ValueTask RetryCancellationAfterDelay()
        {
            var attemptsRemaining = 3;
            do
            {
                await Task.Delay(1_000, cancellationToken);
            } while (!TryCancelRequest() && --attemptsRemaining > 0);
        }

        bool TryCancelRequest()
        {
            Message? message = null;
            var wasWaiting = false;
            lock (_lock)
            {
                // Check the running requests.
                foreach (var candidate in _runningRequests.Keys)
                {
                    if (candidate.Id == messageId && candidate.SendingGrain == senderGrainId)
                    {
                        message = candidate;
                        break;
                    }
                }

                if (message is null)
                {
                    // Check the waiting requests.
                    for (var i = 0; i < _waitingRequests.Count; i++)
                    {
                        var candidate = _waitingRequests[i].Message;
                        if (candidate.Id == messageId && candidate.SendingGrain == senderGrainId)
                        {
                            message = candidate;
                            _waitingRequests.RemoveAt(i);
                            wasWaiting = true;
                            break;
                        }
                    }
                }
            }

            var didCancel = false;
            if (message is not null && message.BodyObject is IInvokable request)
            {
                if (wasWaiting)
                {
                    // If the request was waiting, then we necessarily did manage to cancel it, so send the response now.
                    _shared.InternalRuntime.RuntimeClient.SendResponse(message, Response.FromException(new OperationCanceledException()));
                    didCancel = true;
                }
                else
                {
                    didCancel = TryCancelInvokable(request) || !request.IsCancellable;
                }
            }

            return didCancel;
        }

        bool TryCancelInvokable(IInvokable request)
        {
            try
            {
                return request.TryCancel();
            }
            catch (Exception exception)
            {
                LogErrorCancellationCallbackFailed(Shared.Logger, exception);
                return true;
            }
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "One or more cancellation callbacks failed."
    )]
    private static partial void LogErrorCancellationCallbackFailed(ILogger logger, Exception exception);

    #endregion

    /// <summary>
    /// Additional properties which are not needed for the majority of an activation's lifecycle.
    /// </summary>
    private class ActivationDataExtra : Dictionary<object, object>
    {
        private const int IsStuckProcessingMessageFlag = 1 << 0;
        private const int IsStuckDeactivatingFlag = 1 << 1;
        private const int IsDisposingFlag = 1 << 2;
        private const int IsDeactivationRequestedFlag = 1 << 3;
        private byte _flags;

        /// <summary>
        /// The number of <see cref="IActivationKeepAlive"/> holds which prevent idle collection.
        /// </summary>
        public int KeepAliveHolds;

        public HashSet<IGrainTimer>? Timers { get => GetValueOrDefault<HashSet<IGrainTimer>>(nameof(Timers)); set => SetOrRemoveValue(nameof(Timers), value); }

        /// <summary>
        /// During rehydration, this may contain the address for the previous (recently dehydrated) activation of this grain.
        /// </summary>
        public GrainAddress? PreviousRegistration { get => GetValueOrDefault<GrainAddress>(nameof(PreviousRegistration)); set => SetOrRemoveValue(nameof(PreviousRegistration), value); }

        /// <summary>
        /// The migration context which this activation rehydrates from, between accepting it and rehydrating.
        /// </summary>
        public IRehydrationContext? RehydrationContext { get => GetValueOrDefault<IRehydrationContext>(nameof(RehydrationContext)); set => SetOrRemoveValue(nameof(RehydrationContext), value); }

        /// <summary>
        /// If the activation is tearing down, this may contain a forwarding address for incoming messages
        /// </summary>
        public SiloAddress? ForwardingAddress { get => GetValueOrDefault<SiloAddress>(nameof(ForwardingAddress)); set => SetOrRemoveValue(nameof(ForwardingAddress), value); }

        /// <summary>
        /// A <see cref="TaskCompletionSource{TResult}"/> which completes when a grain has deactivated.
        /// </summary>
        public TaskCompletionSource<bool>? DeactivationTask { get => GetDeactivationInfoOrDefault()?.DeactivationTask; set => EnsureDeactivationInfo().DeactivationTask = value; }

        public TaskCompletionSource? ActivationReady { get => GetValueOrDefault<TaskCompletionSource>(nameof(ActivationReady)); set => SetOrRemoveValue(nameof(ActivationReady), value); }

        public DateTime? DeactivationStartTime { get => GetDeactivationInfoOrDefault()?.DeactivationStartTime; set => EnsureDeactivationInfo().DeactivationStartTime = value; }

        public DeactivationReason DeactivationReason { get => GetDeactivationInfoOrDefault()?.DeactivationReason ?? default; set => EnsureDeactivationInfo().DeactivationReason = value; }

        /// <summary>
        /// When migrating to another location, this contains the information to preserve across activations.
        /// </summary>
        public DehydrationContextHolder? DehydrationContext { get => GetValueOrDefault<DehydrationContextHolder>(nameof(DehydrationContext)); set => SetOrRemoveValue(nameof(DehydrationContext), value); }

        private DeactivationInfo? GetDeactivationInfoOrDefault() => GetValueOrDefault<DeactivationInfo>(nameof(DeactivationInfo));
        private DeactivationInfo EnsureDeactivationInfo()
        {
            ref var info = ref CollectionsMarshal.GetValueRefOrAddDefault(this, nameof(DeactivationInfo), out _);
            info ??= new DeactivationInfo();
            return (DeactivationInfo)info;
        }

        public bool IsStuckProcessingMessage { get => GetFlag(IsStuckProcessingMessageFlag); set => SetFlag(IsStuckProcessingMessageFlag, value); }
        public bool IsStuckDeactivating { get => GetFlag(IsStuckDeactivatingFlag); set => SetFlag(IsStuckDeactivatingFlag, value); }
        public bool IsDisposing { get => GetFlag(IsDisposingFlag); set => SetFlag(IsDisposingFlag, value); }
        public bool IsDeactivationRequested { get => GetFlag(IsDeactivationRequestedFlag); set => SetFlag(IsDeactivationRequestedFlag, value); }

        private void SetFlag(int flag, bool value)
        {
            if (value)
            {
                _flags |= (byte)flag;
            }
            else
            {
                _flags &= (byte)~flag;
            }
        }

        private bool GetFlag(int flag) => (_flags & flag) != 0;
        private T? GetValueOrDefault<T>(object key)
        {
            TryGetValue(key, out var result);
            return (T?)result;
        }

        private void SetOrRemoveValue(object key, object? value)
        {
            if (value is null)
            {
                Remove(key);
            }
            else
            {
                base[key] = value;
            }
        }

        private sealed class DeactivationInfo
        {
            public DateTime? DeactivationStartTime;
            public DeactivationReason DeactivationReason;
            public TaskCompletionSource<bool>? DeactivationTask;
        }
    }

    private abstract class Command(CancellationTokenSource cts) : IDisposable
    {
        private bool _disposed;
#if NET10_0_OR_GREATER
        private readonly Lock _lock = new();
#else
        private readonly object _lock = new();
#endif
        private readonly CancellationTokenSource _cts = cts;
        public CancellationToken CancellationToken => _cts.Token;

        public virtual void Cancel()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _cts.Cancel();
            }
        }

        public virtual void Dispose()
        {
            try
            {
                lock (_lock)
                {
                    _disposed = true;
                    _cts.Dispose();
                }
            }
            catch
            {
                // Ignore.
            }

            GC.SuppressFinalize(this);
        }

        public sealed class Deactivate(CancellationTokenSource cts, Activity? activity) : Command(cts)
        {
            public Activity? Activity { get; } = activity;
        }

        public sealed class Activate(Dictionary<string, object>? requestContext, CancellationTokenSource cts, CatalogInstruments.ActivationMetricTracker metrics) : Command(cts)
        {
            public Dictionary<string, object>? RequestContext { get; } = requestContext;
            public CatalogInstruments.ActivationMetricTracker Metrics { get; } = metrics;
        }

        public sealed class Rehydrate(IRehydrationContext context) : Command(new())
        {
            private IRehydrationContext? _context = context;

            /// <summary>
            /// Gets the migration context, which this command disposes unless the activation takes it.
            /// </summary>
            public IRehydrationContext Context => _context ?? throw new InvalidOperationException("The rehydration context was taken.");

            /// <summary>
            /// Transfers ownership of the migration context to the caller.
            /// </summary>
            public IRehydrationContext TakeContext()
            {
                var result = Context;
                _context = null;
                return result;
            }

            public override void Dispose()
            {
                base.Dispose();
                (_context as IDisposable)?.Dispose();
            }
        }

        public sealed class Delay(TimeSpan duration) : Command(new())
        {
            public TimeSpan Duration { get; } = duration;
        }
    }

    internal class ReentrantRequestTracker : Dictionary<Guid, int>
    {
        public void EnterReentrantSection(Guid reentrancyId)
        {
            Debug.Assert(reentrancyId != Guid.Empty);
            ref var count = ref CollectionsMarshal.GetValueRefOrAddDefault(this, reentrancyId, out _);
            ++count;
        }

        public void LeaveReentrantSection(Guid reentrancyId)
        {
            Debug.Assert(reentrancyId != Guid.Empty);
            ref var count = ref CollectionsMarshal.GetValueRefOrNullRef(this, reentrancyId);
            if (Unsafe.IsNullRef(ref count))
            {
                return;
            }

            if (--count <= 0)
            {
                Remove(reentrancyId);
            }
        }

        public bool IsReentrantSectionActive(Guid reentrancyId)
        {
            Debug.Assert(reentrancyId != Guid.Empty);
            return TryGetValue(reentrancyId, out var count) && count > 0;
        }
    }

    private class DehydrationContextHolder(SerializerSessionPool sessionPool, Dictionary<string, object>? requestContext)
    {
        public readonly MigrationContext MigrationContext = new(sessionPool);
        public readonly Dictionary<string, object>? RequestContext = requestContext;

        /// <summary>
        /// The activity context from the grain call that initiated the migration.
        /// This is used to parent the dehydrate span to the migration request trace.
        /// </summary>
        public ActivityContext? MigrationActivityContext { get; set; } = Activity.Current?.Context;
    }

    [LoggerMessage(
        EventId = (int)ErrorCode.Catalog_Reject_ActivationTooManyRequests,
        Level = LogLevel.Warning,
        Message = "Overload - {Count} enqueued requests for activation {Activation}, exceeding hard limit rejection threshold of {HardLimit}"
    )]
    private static partial void LogRejectActivationTooManyRequests(ILogger logger, int count, ActivationData activation, int hardLimit);

    [LoggerMessage(
        EventId = (int)ErrorCode.Catalog_Warn_ActivationTooManyRequests,
        Level = LogLevel.Warning,
        Message = "Hot - {Count} enqueued requests for activation {Activation}, exceeding soft limit warning threshold of {SoftLimit}"
    )]
    private static partial void LogWarnActivationTooManyRequests(ILogger logger, int count, ActivationData activation, int softLimit);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Error while cancelling on-going operation '{Operation}'."
    )]
    private static partial void LogErrorCancellingOperation(ILogger logger, Exception exception, object operation);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Migrating {GrainId} to {SiloAddress}"
    )]
    private static partial void LogDebugMigrating(ILogger logger, GrainId grainId, SiloAddress siloAddress);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error while selecting a migration destination for {GrainId}"
    )]
    private static partial void LogErrorSelectingMigrationDestination(ILogger logger, Exception exception, GrainId grainId);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Placement strategy {PlacementStrategy} failed to select a destination for migration of {GrainId}"
    )]
    private static partial void LogDebugPlacementStrategyFailedToSelectDestination(ILogger logger, PlacementStrategy placementStrategy, GrainId grainId);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Placement strategy {PlacementStrategy} selected the current silo as the destination for migration of {GrainId}"
    )]
    private static partial void LogDebugPlacementStrategySelectedCurrentSilo(ILogger logger, PlacementStrategy placementStrategy, GrainId grainId);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error invoking MayInterleave predicate on grain {Grain} for message {Message}"
    )]
    private static partial void LogErrorInvokingMayInterleavePredicate(ILogger logger, Exception exception, ActivationData grain, Message message);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error in ProcessOperationsAsync for grain activation '{Activation}'."
    )]
    private static partial void LogErrorInProcessOperationsAsync(ILogger logger, Exception exception, ActivationData activation);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Rehydrating grain '{GrainContext}' from previous activation."
    )]
    private static partial void LogRehydratingGrain(ILogger logger, ActivationData grainContext);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Ignoring attempt to rehydrate grain '{GrainContext}' in the '{State}' state."
    )]
    private static partial void LogIgnoringRehydrateAttempt(ILogger logger, ActivationData grainContext, ActivationState state);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Previous activation address was {PreviousRegistration}"
    )]
    private static partial void LogPreviousActivationAddress(ILogger logger, GrainAddress previousRegistration);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Rehydrated grain from previous activation"
    )]
    private static partial void LogRehydratedGrain(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error while rehydrating activation"
    )]
    private static partial void LogErrorRehydratingActivation(ILogger logger, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Dehydrating grain activation"
    )]
    private static partial void LogDehydratingActivation(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Dehydrated grain activation"
    )]
    private static partial void LogDehydratedActivation(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error while dehydrating activation"
    )]
    private static partial void LogErrorDehydratingActivation(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = (int)ErrorCode.Catalog_RerouteAllQueuedMessages,
        Level = LogLevel.Debug,
        Message = "Rejecting {Count} messages from invalid activation {Activation}."
    )]
    private static partial void LogRejectAllQueuedMessages(ILogger logger, int count, ActivationData activation);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Registering grain '{Grain}' in activation directory. Previous known registration is '{PreviousRegistration}'.")]
    private static partial void LogRegisteringGrain(ILogger logger, ActivationData grain, GrainAddress? previousRegistration);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "The grain directory has an existing entry pointing to a different activation of this grain, '{GrainId}', on this silo: '{PreviousRegistration}'."
            + " This may indicate that the previous activation was deactivated but the directory was not successfully updated."
            + " The directory will be updated to point to this activation."
    )]
    private static partial void LogAttemptToRegisterWithPreviousActivation(ILogger logger, GrainId grainId, GrainAddress previousRegistration);

    [LoggerMessage(
        EventId = (int)ErrorCode.Dispatcher_ExtendedMessageProcessing,
        Level = LogLevel.Warning,
        Message = "Current request has been active for {CurrentRequestActiveTime} for grain {Grain}. Currently executing {BlockingRequest}. Trying to enqueue {Message}.")]
    private static partial void LogWarningDispatcher_ExtendedMessageProcessing(
        ILogger logger,
        TimeSpan currentRequestActiveTime,
        ActivationDataLogValue grain,
        Message blockingRequest,
        Message message);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Abandoning stuck deactivating activation {Activation}. ForwardingAddress={ForwardingAddress}")]
    private static partial void LogWarningAbandoningStuckDeactivatingActivation(ILogger logger, ActivationData activation, SiloAddress? forwardingAddress);

    private readonly struct ActivationDataLogValue(ActivationData activation, bool includeExtraDetails = false)
    {
        public override string ToString() => activation.ToDetailedString(includeExtraDetails);
    }

    [LoggerMessage(
        EventId = (int)ErrorCode.Runtime_Error_100064,
        Level = LogLevel.Warning,
        Message = "Failed to register grain {Grain} in grain directory")]
    private static partial void LogFailedToRegisterGrain(ILogger logger, Exception exception, ActivationData grain);


    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Ignoring activation request for {Grain} because this grain is in the '{State}' state")]
    private static partial void LogIgnoringActivateAttempt(ILogger logger, ActivationData grain, ActivationState state);

    [LoggerMessage(
        EventId = (int)ErrorCode.Catalog_BeforeCallingActivate,
        Level = LogLevel.Debug,
        Message = "Activating grain {Grain}")]
    private static partial void LogActivatingGrain(ILogger logger, ActivationData grain);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error starting lifecycle for activation '{Activation}'")]
    private static partial void LogErrorStartingLifecycle(ILogger logger, Exception exception, ActivationData activation);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error stopping lifecycle for activation '{Activation}'")]
    private static partial void LogErrorStoppingLifecycle(ILogger logger, Exception exception, ActivationData activation);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error thrown from {MethodName} for activation '{Activation}'")]
    private static partial void LogErrorInGrainMethod(ILogger logger, Exception exception, string methodName, ActivationData activation);

    [LoggerMessage(
        EventId = (int)ErrorCode.Catalog_AfterCallingActivate,
        Level = LogLevel.Debug,
        Message = "Finished activating grain {Grain}")]
    private static partial void LogFinishedActivatingGrain(ILogger logger, ActivationData grain);

    [LoggerMessage(
        EventId = (int)ErrorCode.Catalog_ErrorCallingActivate,
        Level = LogLevel.Error,
        Message = "Error activating grain {Grain}")]
    private static partial void LogErrorActivatingGrain(ILogger logger, Exception exception, ActivationData grain);

    [LoggerMessage(
        EventId = (int)ErrorCode.Catalog_DisposedObjectAccess,
        Level = LogLevel.Warning,
        Message = "Disposed object {ObjectName} accessed in OnActivateAsync for grain {Grain}. Ensure the cancellationToken is passed to all async methods or they have .WaitAsync(cancellationToken) called on them.")]
    private static partial void LogActivationDisposedObjectAccessed(ILogger logger, string objectName, ActivationData grain);

    [LoggerMessage(
        EventId = (int)ErrorCode.Catalog_CancelledActivate,
        Level = LogLevel.Information,
        Message = "Activation was cancelled for {Grain}. CancellationRequested={CancellationRequested}, DeactivationReasonCode={DeactivationReasonCode}, DeactivationReason={DeactivationReason}, ForwardingAddress={ForwardingAddress}"
    )]
    private static partial void LogActivationCancelled(ILogger logger, ActivationData grain, bool cancellationRequested, DeactivationReasonCode deactivationReasonCode, string? deactivationReason, SiloAddress? forwardingAddress);
    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "Completing deactivation of '{Activation}'")]
    private static partial void LogCompletingDeactivation(ILogger logger, ActivationData activation);

    [LoggerMessage(
        EventId = (int)ErrorCode.Catalog_BeforeCallingDeactivate,
        Level = LogLevel.Debug,
        Message = "About to call OnDeactivateAsync for '{Activation}'")]
    private static partial void LogBeforeOnDeactivateAsync(ILogger logger, ActivationData activation);

    [LoggerMessage(
        EventId = (int)ErrorCode.Catalog_AfterCallingDeactivate,
        Level = LogLevel.Debug,
        Message = "Returned from calling '{Activation}' OnDeactivateAsync method")]
    private static partial void LogAfterOnDeactivateAsync(ILogger logger, ActivationData activation);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Failed to unregister activation '{Activation}' from directory")]
    private static partial void LogFailedToUnregisterActivation(ILogger logger, Exception exception, ActivationData activation);

    [LoggerMessage(
        EventId = (int)ErrorCode.Catalog_DeactivateActivation_Exception,
        Level = LogLevel.Warning,
        Message = "Error deactivating '{Activation}'")]
    private static partial void LogErrorDeactivating(ILogger logger, Exception exception, ActivationData activation);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Exception disposing activation '{Activation}'")]
    private static partial void LogExceptionDisposing(ILogger logger, Exception exception, ActivationData activation);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to migrate activation '{Activation}'")]
    private static partial void LogFailedToMigrateActivation(ILogger logger, Exception exception, ActivationData activation);

    private readonly struct FullAddressLogRecord(GrainAddress address)
    {
        public override string ToString() => address.ToFullString();
    }

    [LoggerMessage(
        EventId = (int)ErrorCode.Catalog_DuplicateActivation,
        Level = LogLevel.Debug,
        Message = "Tried to create a duplicate activation {Address}, but we'll use {ForwardingAddress} instead. GrainInstance type is {GrainInstanceType}. Full activation address is {FullAddress}. We have {WaitingCount} messages to forward")]
    private static partial void LogDuplicateActivation(
        ILogger logger,
        GrainAddress address,
        SiloAddress? forwardingAddress,
        Type? grainInstanceType,
        FullAddressLogRecord fullAddress,
        int waitingCount);

    [LoggerMessage(
        EventId = (int)ErrorCode.Catalog_RerouteAllQueuedMessages,
        Level = LogLevel.Debug,
        Message = "Rerouting {NumMessages} messages from invalid grain activation {Grain} to {ForwardingAddress}")]
    private static partial void LogReroutingMessages(ILogger logger, int numMessages, ActivationData grain, SiloAddress forwardingAddress);

    [LoggerMessage(
        EventId = (int)ErrorCode.Catalog_RerouteAllQueuedMessages,
        Level = LogLevel.Debug,
        Message = "Rerouting {NumMessages} messages from invalid grain activation {Grain}")]
    private static partial void LogReroutingMessagesNoForwarding(ILogger logger, int numMessages, ActivationData grain);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error in grain message loop"
    )]
    private static partial void LogErrorInGrainMessageLoop(ILogger logger, Exception exception);
}
