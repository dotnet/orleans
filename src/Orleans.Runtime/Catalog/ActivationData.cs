#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Core.Internal;
using Orleans.GrainDirectory;
using Orleans.Internal;
using Orleans.Runtime.Placement;
using Orleans.Runtime.Scheduler;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.Session;
using Orleans.Serialization.TypeSystem;

namespace Orleans.Runtime;

/// <summary>
/// Maintains additional per-activation state that is required for Orleans internal operations.
/// MUST lock this object for any concurrent access
/// Consider: compartmentalize by usage, e.g., using separate interfaces for data for catalog, etc.
/// </summary>
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
    private readonly GrainTypeSharedContext _shared;
    private readonly IServiceScope _serviceScope;
    private readonly WorkItemGroup _workItemGroup;
    private readonly List<(Message Message, CoarseStopwatch QueuedTime)> _waitingRequests = new();
    private readonly Dictionary<Message, CoarseStopwatch> _runningRequests = new();
    private readonly SingleWaiterAutoResetEvent _workSignal = new() { RunContinuationsAsynchronously = true };
    private GrainLifecycle? _lifecycle;
    private Queue<object>? _pendingOperations;
    private Message? _blockingRequest;
    private bool _isInWorkingSet = true;
    private CoarseStopwatch _busyDuration;
    private CoarseStopwatch _idleDuration;
    private GrainReference? _selfReference;

    // Values which are needed less frequently and do not warrant living directly on activation for object size reasons.
    // The values in this field are typically used to represent termination state of an activation or features which are not
    // used by all grains, such as grain timers.
    private ActivationDataExtra? _extras;

    // The task representing this activation's message loop.
    // This field is assigned and never read and exists only for debugging purposes (eg, in memory dumps, to associate a loop task with an activation).
#pragma warning disable IDE0052 // Remove unread private members
    private readonly Task _messageLoopTask;
#pragma warning restore IDE0052 // Remove unread private members

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
        _messageLoopTask = this.RunOrQueueTask(RunMessageLoop);
    }

    public IGrainRuntime GrainRuntime => _shared.Runtime;
    public object? GrainInstance { get; private set; }
    public GrainAddress Address { get; private set; }
    public GrainReference GrainReference => _selfReference ??= _shared.GrainReferenceActivator.CreateReference(GrainId, default);
    public ActivationState State { get; private set; } = ActivationState.Creating;
    public PlacementStrategy PlacementStrategy => _shared.PlacementStrategy;
    public DateTime CollectionTicket { get; set; }
    public IServiceProvider ActivationServices => _serviceScope.ServiceProvider;
    public ActivationId ActivationId => Address.ActivationId;
    public IGrainLifecycle ObservableLifecycle
    {
        get
        {
            if (_lifecycle is { } lifecycle) return lifecycle;
            lock (this) { return _lifecycle ??= new GrainLifecycle(_shared.Logger); }
        }
    }

    internal GrainTypeSharedContext Shared => _shared;

    public GrainId GrainId => Address.GrainId;
    public bool IsExemptFromCollection => _shared.CollectionAgeLimit == Timeout.InfiniteTimeSpan;
    public DateTime KeepAliveUntil { get; set; } = DateTime.MinValue;
    public bool IsValid => State is ActivationState.Valid;

    // Currently, the only supported multi-activation grain is one using the StatelessWorkerPlacement strategy.
    internal bool IsStatelessWorker => PlacementStrategy is StatelessWorkerPlacement;

    /// <summary>
    /// Returns a value indicating whether or not this placement strategy requires activations to be registered in
    /// the grain directory.
    /// </summary>
    internal bool IsUsingGrainDirectory => PlacementStrategy.IsUsingGrainDirectory;

    public int WaitingCount => _waitingRequests.Count;
    public bool IsInactive => !IsCurrentlyExecuting && _waitingRequests.Count == 0;
    public bool IsCurrentlyExecuting => _runningRequests.Count > 0;
    public IWorkItemScheduler Scheduler => _workItemGroup;
    public Task Deactivated => GetDeactivationCompletionSource().Task;

    public SiloAddress? ForwardingAddress
    {
        get => _extras?.ForwardingAddress;
        set
        {
            lock (this)
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
            lock (this)
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
            lock (this)
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
            lock (this)
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
            lock (this)
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
            lock (this)
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
            lock (this)
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
            lock (this)
            {
                _extras ??= new();
                _extras.DehydrationContext = value;
            }
        }
    }

    public TimeSpan CollectionAgeLimit => _shared.CollectionAgeLimit;

    public TTarget? GetTarget<TTarget>() where TTarget : class => (TTarget?)GrainInstance;

    TComponent? ITargetHolder.GetComponent<TComponent>() where TComponent : class
    {
        var result = GetComponent<TComponent>();
        if (result is null && typeof(IGrainExtension).IsAssignableFrom(typeof(TComponent)))
        {
            var implementation = ActivationServices.GetKeyedService<IGrainExtension>(typeof(TComponent));
            if (implementation is not TComponent typedResult)
            {
                throw new GrainExtensionNotInstalledException($"No extension of type {typeof(TComponent)} is installed on this instance and no implementations are registered for automated install");
            }

            SetComponent(typedResult);
            result = typedResult;
        }

        return result;
    }

    public TComponent? GetComponent<TComponent>() where TComponent : class
    {
        TComponent? result = default;
        if (GrainInstance is TComponent grainResult)
        {
            result = grainResult;
        }
        else if (this is TComponent contextResult)
        {
            result = contextResult;
        }
        else if (_extras is { } components && components.TryGetValue(typeof(TComponent), out var resultObj))
        {
            result = (TComponent)resultObj;
        }
        else if (_shared.GetComponent<TComponent>() is { } sharedComponent)
        {
            result = sharedComponent;
        }
        else if (ActivationServices.GetService<TComponent>() is { } component)
        {
            SetComponent(component);
            result = component;
        }

        return result;
    }

    public void SetComponent<TComponent>(TComponent? instance) where TComponent : class
    {
        if (GrainInstance is TComponent)
        {
            throw new ArgumentException("Cannot override a component which is implemented by this grain");
        }

        if (this is TComponent)
        {
            throw new ArgumentException("Cannot override a component which is implemented by this grain context");
        }

        lock (this)
        {
            if (instance == null)
            {
                _extras?.Remove(typeof(TComponent));
                return;
            }

            _extras ??= new();
            _extras[typeof(TComponent)] = instance;
        }
    }

    internal void SetGrainInstance(object grainInstance)
    {
        ArgumentNullException.ThrowIfNull(grainInstance);

        lock (this)
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

    public void SetState(ActivationState state)
    {
        State = state;
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
        lock (this)
        {
            return _runningRequests.Count + WaitingCount;
        }
    }

    internal List<Message> DequeueAllWaitingRequests()
    {
        lock (this)
        {
            var result = new List<Message>(_waitingRequests.Count);
            foreach (var (message, _) in _waitingRequests)
            {
                // Local-only messages are not allowed to escape the activation.
                if (message.IsLocalOnly)
                {
                    continue;
                }

                result.Add(message);
            }

            _waitingRequests.Clear();
            return result;
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
        if (timespan == TimeSpan.MaxValue || timespan == Timeout.InfiniteTimeSpan)
        {
            // otherwise creates negative time.
            KeepAliveUntil = DateTime.MaxValue;
        }
        else if (timespan <= TimeSpan.Zero)
        {
            // reset any current keepAliveUntil
            ResetKeepAliveRequest();
        }
        else
        {
            KeepAliveUntil = GrainRuntime.TimeProvider.GetUtcNow().UtcDateTime + timespan;
        }
    }

    public void ResetKeepAliveRequest() => KeepAliveUntil = DateTime.MinValue;

    private void ScheduleOperation(object operation)
    {
        lock (this)
        {
            _pendingOperations ??= new();
            _pendingOperations.Enqueue(operation);
        }

        _workSignal.Signal();
    }

    private void CancelPendingOperations()
    {
        lock (this)
        {
            // If the grain is currently activating, cancel that operation.
            if (_pendingOperations is not { } operations)
            {
                return;
            }

            foreach (var op in operations)
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
    }

    public void Migrate(Dictionary<string, object>? requestContext, CancellationToken cancellationToken = default)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_shared.InternalRuntime.CollectionOptions.Value.DeactivationTimeout);

        if (Equals(RuntimeContext.Current) && State is ActivationState.Deactivating)
        {
            // The grain is executing and is already deactivating, so just set the migration context and return.
            StartMigratingCore(requestContext, null);
        }
        else
        {
            // We use a named work item since it is cheaper than allocating a Task and has the benefit of being named.
            _workItemGroup.QueueWorkItem(new MigrateWorkItem(this, requestContext, cts));
        }
    }

    private async Task StartMigratingAsync(Dictionary<string, object>? requestContext, CancellationTokenSource cts)
    {
        lock (this)
        {
            if (State is not (ActivationState.Activating or ActivationState.Valid or ActivationState.Deactivating))
            {
                return;
            }
        }

        try
        {
            var newLocation = await PlaceMigratingGrainAsync(requestContext, cts.Token);
            if (newLocation is null)
            {
                // Will not deactivate/migrate.
                return;
            }

            lock (this)
            {
                if (!DeactivateCore(new DeactivationReason(DeactivationReasonCode.Migrating, "Migrating to a new location."), cts.Token))
                {
                    // Grain is not able to start deactivating or has already completed.
                    return;
                }

                StartMigratingCore(requestContext, newLocation);
            }

            LogDebugMigrating(_shared.Logger, GrainId, newLocation);
        }
        catch (Exception exception)
        {
            LogErrorSelectingMigrationDestination(_shared.Logger, exception, GrainId);
            return;
        }
    }

    private void StartMigratingCore(Dictionary<string, object>? requestContext, SiloAddress? newLocation)
    {
        if (DehydrationContext is not null)
        {
            // Migration has already started.
            return;
        }

        // Set a migration context to capture any state which should be transferred.
        // Doing this signals to the deactivation process that a migration is occurring, so it is important that this happens before we begin deactivation.
        DehydrationContext = new(_shared.SerializerSessionPool, requestContext);
        ForwardingAddress = newLocation;
    }

    private async ValueTask<SiloAddress?> PlaceMigratingGrainAsync(Dictionary<string, object>? requestContext, CancellationToken cancellationToken)
    {
        var placementService = _shared.Runtime.ServiceProvider.GetRequiredService<PlacementService>();
        var newLocation = await placementService.PlaceGrainAsync(GrainId, requestContext, PlacementStrategy).WaitAsync(cancellationToken);

        // If a new (different) host is not selected, do not migrate.
        if (newLocation == Address.SiloAddress || newLocation is null)
        {
            // No more appropriate silo was selected for this grain. The migration attempt will be aborted.
            // This could be because this is the only (compatible) silo for the grain or because the placement director chose this
            // silo for some other reason.
            if (newLocation is null)
            {
                LogDebugPlacementStrategyFailedToSelectDestination(_shared.Logger, PlacementStrategy, GrainId);
            }
            else
            {
                LogDebugPlacementStrategySelectedCurrentSilo(_shared.Logger, PlacementStrategy, GrainId);
            }

            // Will not migrate.
            return null;
        }

        return newLocation;
    }

    public void Deactivate(DeactivationReason reason, CancellationToken cancellationToken = default) => DeactivateCore(reason, cancellationToken);

    public bool DeactivateCore(DeactivationReason reason, CancellationToken cancellationToken)
    {
        lock (this)
        {
            var state = State;
            if (state is ActivationState.Invalid)
            {
                return false;
            }

            if (DeactivationReason.ReasonCode == DeactivationReasonCode.None)
            {
                DeactivationReason = reason;
            }

            if (!DeactivationStartTime.HasValue)
            {
                DeactivationStartTime = GrainRuntime.TimeProvider.GetUtcNow().UtcDateTime;
            }

            if (state is ActivationState.Creating or ActivationState.Activating or ActivationState.Valid)
            {
                CancelPendingOperations();

                _shared.InternalRuntime.ActivationWorkingSet.OnDeactivating(this);
                SetState(ActivationState.Deactivating);
                var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(_shared.InternalRuntime.CollectionOptions.Value.DeactivationTimeout);
                ScheduleOperation(new Command.Deactivate(cts, state));
            }
        }

        return true;
    }

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
        lock (this)
        {
            Timers ??= new HashSet<IGrainTimer>();
            Timers.Add(timer);
        }
    }

    void IGrainTimerRegistry.OnTimerDisposed(IGrainTimer timer)
    {
        lock (this) // need to lock since dispose can be called on finalizer thread, outside grain context (not single threaded).
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
        lock (this)
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
        lock (this)
        {
            if (State != ActivationState.Valid)
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
        lock (this)
        {
            var currentlyExecuting = includeExtraDetails ? _blockingRequest : null;
            return @$"[Activation: {Address.SiloAddress}/{GrainId}{ActivationId} {GetActivationInfoString()} State={State} NonReentrancyQueueSize={WaitingCount} NumRunning={_runningRequests.Count} IdlenessTimeSpan={GetIdleness()} CollectionAgeLimit={_shared.CollectionAgeLimit}{(currentlyExecuting != null ? " CurrentlyExecuting=" : null)}{currentlyExecuting}]";
        }
    }

    private string GetActivationInfoString()
    {
        var placement = PlacementStrategy?.GetType().Name;
        var grainTypeName = _shared.GrainTypeName ?? GrainInstance switch
        {
            { } grainInstance => RuntimeTypeNameFormatter.Format(grainInstance.GetType()),
            _ => null
        };
        return grainTypeName is null ? $"#Placement={placement}" : $"#GrainType={grainTypeName} Placement={placement}";
    }

    public void Dispose() => DisposeAsync().AsTask().Wait();

    public async ValueTask DisposeAsync()
    {
        _extras ??= new();
        if (_extras.IsDisposing) return;
        _extras.IsDisposing = true;

        CancelPendingOperations();

        lock (this)
        {
            _shared.InternalRuntime.ActivationWorkingSet.OnDeactivated(this);
            SetState(ActivationState.Invalid);
        }

        DisposeTimers();

        try
        {
            var activator = _shared.GetComponent<IGrainActivator>();
            if (activator != null && GrainInstance is { } instance)
            {
                await activator.DisposeInstance(this, instance);
            }
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            _shared.OnDestroyActivation(this);
            GetComponent<IActivationLifecycleObserver>()?.OnDestroyActivation(this);
        }
        catch (ObjectDisposedException)
        {
        }

        await DisposeAsync(_serviceScope);
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
        if (!(implementation is TExtensionInterface typedResult))
        {
            throw new GrainExtensionNotInstalledException($"No extension of type {typeof(TExtensionInterface)} is installed on this instance and no implementations are registered for automated install");
        }

        SetComponent(typedResult);
        return typedResult;
    }

    bool IActivationWorkingSetMember.IsCandidateForRemoval(bool wouldRemove)
    {
        const int IdlenessLowerBound = 10_000;
        lock (this)
        {
            var inactive = IsInactive && _idleDuration.ElapsedMilliseconds > IdlenessLowerBound;

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
                    lock (this)
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
                _shared.InternalRuntime.MessagingTrace.LogError(exception, "Error in grain message loop");
            }
        }

        void ProcessPendingRequests()
        {
            var i = 0;

            do
            {
                Message? message = null;
                lock (this)
                {
                    if (_waitingRequests.Count <= i)
                    {
                        break;
                    }

                    message = _waitingRequests[i].Message;

                    // If the activation is not valid, reject all pending messages except for local-only messages.
                    // Local-only messages are used for internal system operations and should not be rejected while the grain is valid or deactivating.
                    if (State != ActivationState.Valid && !(message.IsLocalOnly && State is ActivationState.Deactivating))
                    {
                        ProcessRequestsToInvalidActivation();
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
                                message.CacheInvalidationHeader ??= new List<GrainAddressCacheUpdate>();
                                message.CacheInvalidationHeader.Add(new GrainAddressCacheUpdate(Address, validAddress: null));

                                var reason = new DeactivationReason(
                                    DeactivationReasonCode.IncompatibleRequest,
                                    $"Received incompatible request for interface {message.InterfaceType} version {message.InterfaceVersion}. This activation supports interface version {currentVersion}.");

                                Deactivate(reason, cancellationToken: default);
                                return;
                            }
                        }
                    }
                    catch (Exception exception)
                    {
                        if (!message.IsLocalOnly)
                        {
                            _shared.InternalRuntime.MessageCenter.RejectMessage(message, Message.RejectionTypes.Transient, exception);
                        }

                        _waitingRequests.RemoveAt(i);
                        continue;
                    }

                    // Process this message, removing it from the queue.
                    _waitingRequests.RemoveAt(i);

                    Debug.Assert(State == ActivationState.Valid || message.IsLocalOnly);
                    RecordRunning(message, message.IsAlwaysInterleave);
                }

                // Start invoking the message outside of the lock
                InvokeIncomingRequest(message);
            }
            while (true);
        }

        void RecordRunning(Message message, bool isInterleavable)
        {
            var stopwatch = CoarseStopwatch.StartNew();
            _runningRequests.Add(message, stopwatch);

            if (_blockingRequest != null || isInterleavable) return;

            // This logic only works for non-reentrant activations
            // Consider: Handle long request detection for reentrant activations.
            _blockingRequest = message;
            _busyDuration = stopwatch;
        }

        void ProcessRequestsToInvalidActivation()
        {
            if (State is ActivationState.Creating or ActivationState.Activating)
            {
                // Do nothing until the activation becomes either valid or invalid
                return;
            }

            if (State is ActivationState.Deactivating)
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
                }

                if (!IsStuckDeactivating && !IsStuckProcessingMessage)
                {
                    // Do not forward messages while the grain is still deactivating and has not been declared stuck, since they
                    // will be forwarded to the same grain instance.
                    return;
                }
            }

            if (DeactivationException is null || ForwardingAddress is { })
            {
                // Either this was a duplicate activation or it was at some point successfully activated
                // Forward all pending messages
                RerouteAllQueuedMessages();
            }
            else
            {
                // Reject all pending messages
                RejectAllQueuedMessages();
            }
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

            if (_blockingRequest is null)
            {
                return true;
            }

            if (_blockingRequest.IsReadOnly && incoming.IsReadOnly)
            {
                return true;
            }

            // Handle call-chain reentrancy
            if (incoming.GetReentrancyId() is Guid id
                && IsReentrantSection(id))
            {
                return true;
            }

            if (GetComponent<GrainCanInterleave>() is GrainCanInterleave canInterleave)
            {
                try
                {
                    return canInterleave.MayInterleave(GrainInstance, incoming);
                }
                catch (Exception exception)
                {
                    LogErrorInvokingMayInterleavePredicate(_shared.Logger, exception, this, incoming);
                    throw;
                }
            }

            return false;
        }

        async Task ProcessOperationsAsync()
        {
            object? op = null;
            while (true)
            {
                lock (this)
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
                            RehydrateInternal(command.Context);
                            break;
                        case Command.Activate command:
                            await ActivateAsync(command.RequestContext, command.CancellationToken).SuppressThrowing();
                            break;
                        case Command.Deactivate command:
                            await FinishDeactivating(command.PreviousState, command.CancellationToken).SuppressThrowing();
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
                    await DisposeAsync(op);
                }
            }
        }
    }

    private void RehydrateInternal(IRehydrationContext context)
    {
        try
        {
            LogRehydratingGrain(_shared.Logger, this);

            lock (this)
            {
                if (State != ActivationState.Creating)
                {
                    LogIgnoringRehydrateAttempt(_shared.Logger, this, State);
                    return;
                }

                if (context.TryGetValue(GrainAddressMigrationContextKey, out GrainAddress? previousRegistration) && previousRegistration is not null)
                {
                    PreviousRegistration = previousRegistration;
                    LogPreviousActivationAddress(_shared.Logger, previousRegistration);
                }

                if (_lifecycle is { } lifecycle)
                {
                    foreach (var participant in lifecycle.GetMigrationParticipants())
                    {
                        participant.OnRehydrate(context);
                    }
                }

                (GrainInstance as IGrainMigrationParticipant)?.OnRehydrate(context);
            }

            LogRehydratedGrain(_shared.Logger);
        }
        catch (Exception exception)
        {
            LogErrorRehydratingActivation(_shared.Logger, exception);
        }
    }

    private void OnDehydrate(IDehydrationContext context)
    {
        LogDehydratingActivation(_shared.Logger);

        lock (this)
        {
            Debug.Assert(context is not null);

            // Note that these calls are in reverse order from Rehydrate, not for any particular reason other than symmetry.
            (GrainInstance as IGrainMigrationParticipant)?.OnDehydrate(context);

            if (_lifecycle is { } lifecycle)
            {
                foreach (var participant in lifecycle.GetMigrationParticipants())
                {
                    participant.OnDehydrate(context);
                }
            }

            if (IsUsingGrainDirectory)
            {
                context.TryAddValue(GrainAddressMigrationContextKey, Address);
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
        MessagingProcessingInstruments.OnDispatcherMessageProcessedOk(message);
        _shared.InternalRuntime.MessagingTrace.OnScheduleMessage(message);

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

        static async ValueTask OnCompleteAsync(ActivationData activation, Message message, Task task)
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
        lock (this)
        {
            _runningRequests.Remove(message);

            // If the message is meant to keep the activation active, reset the idle timer and ensure the activation
            // is in the activation working set.
            if (message.IsKeepAlive)
            {
                _idleDuration = CoarseStopwatch.StartNew();

                if (!_isInWorkingSet)
                {
                    _isInWorkingSet = true;
                    _shared.InternalRuntime.ActivationWorkingSet.OnActive(this);
                }
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

    public void ReceiveMessage(object message) => ReceiveMessage((Message)message);
    public void ReceiveMessage(Message message)
    {
        _shared.InternalRuntime.MessagingTrace.OnDispatcherReceiveMessage(message);

        // Don't process messages that have already timed out
        if (message.IsExpired)
        {
            MessagingProcessingInstruments.OnDispatcherMessageProcessedError(message);
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
        lock (this)
        {
            if (State == ActivationState.Invalid)
            {
                _shared.InternalRuntime.MessagingTrace.OnDispatcherReceiveInvalidActivation(message, State);

                // Always process responses
                _shared.InternalRuntime.RuntimeClient.ReceiveResponse(message);
                return;
            }

            MessagingProcessingInstruments.OnDispatcherMessageProcessedOk(message);
            _shared.InternalRuntime.RuntimeClient.ReceiveResponse(message);
        }
    }

    private void ReceiveRequest(Message message)
    {
        var overloadException = CheckOverloaded();
        if (overloadException != null && !message.IsLocalOnly)
        {
            MessagingProcessingInstruments.OnDispatcherMessageProcessedError(message);
            _shared.InternalRuntime.MessageCenter.RejectMessage(message, Message.RejectionTypes.Overloaded, overloadException, "Target activation is overloaded " + this);
            return;
        }

        lock (this)
        {
            _waitingRequests.Add((message, CoarseStopwatch.StartNew()));
        }

        _workSignal.Signal();
    }

    /// <summary>
    /// Rejects all messages enqueued for the provided activation.
    /// </summary>
    private void RejectAllQueuedMessages()
    {
        lock (this)
        {
            List<Message> msgs = DequeueAllWaitingRequests();
            if (msgs == null || msgs.Count <= 0) return;

            LogRejectAllQueuedMessages(_shared.Logger, msgs.Count, this);
            _shared.InternalRuntime.GrainLocator.InvalidateCache(Address);
            _shared.InternalRuntime.MessageCenter.ProcessRequestsToInvalidActivation(
                msgs,
                Address,
                forwardingAddress: ForwardingAddress,
                failedOperation: DeactivationReason.Description,
                exc: DeactivationException,
                rejectMessages: true);
        }
    }

    private void RerouteAllQueuedMessages()
    {
        lock (this)
        {
            List<Message> msgs = DequeueAllWaitingRequests();
            if (msgs is not { Count: > 0 })
            {
                return;
            }

            // If deactivation was caused by a transient failure, allow messages to be forwarded.
            if (DeactivationReason.ReasonCode.IsTransientError())
            {
                foreach (var msg in msgs)
                {
                    msg.ForwardCount = Math.Max(msg.ForwardCount - 1, 0);
                }
            }

            if (_shared.Logger.IsEnabled(LogLevel.Debug))
            {
                if (ForwardingAddress is { } address)
                {
                    LogReroutingMessages(_shared.Logger, msgs.Count, this, address);
                }
                else
                {
                    LogReroutingMessagesNoForwarding(_shared.Logger, msgs.Count, this);
                }
            }

            _shared.InternalRuntime.GrainLocator.InvalidateCache(Address);
            _shared.InternalRuntime.MessageCenter.ProcessRequestsToInvalidActivation(msgs, Address, ForwardingAddress, DeactivationReason.Description, DeactivationException);
        }
    }

    #region Activation
    public void Rehydrate(IRehydrationContext context)
    {
        ScheduleOperation(new Command.Rehydrate(context));
    }

    public void Activate(Dictionary<string, object>? requestContext, CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_shared.InternalRuntime.CollectionOptions.Value.ActivationTimeout);

        ScheduleOperation(new Command.Activate(requestContext, cts));
    }

    private async Task ActivateAsync(Dictionary<string, object>? requestContextData, CancellationToken cancellationToken)
    {
        // A chain of promises that will have to complete in order to complete the activation
        // Register with the grain directory and call the Activate method on the new activation.
        try
        {
            // Currently, the only grain type that is not registered in the Grain Directory is StatelessWorker.
            // Among those that are registered in the directory, we currently do not have any multi activations.
            if (IsUsingGrainDirectory)
            {
                Exception? registrationException;
                var previousRegistration = PreviousRegistration;
                bool success;
                try
                {
                    while (true)
                    {
                        LogRegisteringGrain(_shared.Logger, this, previousRegistration);

                        var result = await _shared.InternalRuntime.GrainLocator.Register(Address, previousRegistration).WaitAsync(cancellationToken);
                        if (Address.Matches(result))
                        {
                            Address = result;
                            success = true;
                        }
                        else if (result?.SiloAddress is { } registeredSilo && registeredSilo.Equals(Address.SiloAddress))
                        {
                            // Attempt to register this activation again, using the registration of the previous instance of this grain,
                            // which is registered to this silo. That activation must be a defunct predecessor of this activation,
                            // since the catalog only allows one activation of a given grain at a time.
                            // This could occur if the previous activation failed to unregister itself from the grain directory.
                            previousRegistration = result;
                            LogAttemptToRegisterWithPreviousActivation(_shared.Logger, GrainId, result);
                            continue;
                        }
                        else
                        {
                            // Set the forwarding address so that messages enqueued on this activation can be forwarded to
                            // the existing activation.
                            ForwardingAddress = result?.SiloAddress;
                            if (ForwardingAddress is { } address)
                            {
                                DeactivationReason = new(DeactivationReasonCode.DuplicateActivation, $"This grain is active on another host ({address}).");
                            }

                            success = false;
                            CatalogInstruments.ActivationConcurrentRegistrationAttempts.Add(1);
                            // If this was a duplicate, it's not an error, just a race.
                            // Forward on all of the pending messages, and then forget about this activation.
                            LogDuplicateActivation(
                                _shared.Logger,
                                Address,
                                ForwardingAddress,
                                GrainInstance?.GetType(),
                                new(Address),
                                WaitingCount);
                        }

                        break;
                    }

                    registrationException = null;
                }
                catch (Exception exception)
                {
                    registrationException = exception;
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        LogFailedToRegisterGrain(_shared.Logger, registrationException, this);
                    }

                    success = false;
                }

                if (!success)
                {
                    Deactivate(new(DeactivationReasonCode.DirectoryFailure, registrationException, "Failed to register activation in grain directory."));

                    // Activation failed.
                    return;
                }
            }

            lock (this)
            {
                SetState(ActivationState.Activating);
            }

            LogActivatingGrain(_shared.Logger, this);

            // Start grain lifecycle within try-catch wrapper to safely capture any exceptions thrown from called function
            try
            {
                RequestContextExtensions.Import(requestContextData);
                try
                {
                    if (_lifecycle is { } lifecycle)
                    {
                        await lifecycle.OnStart(cancellationToken).WaitAsync(cancellationToken);
                    }
                }
                catch (Exception exception)
                {
                    LogErrorStartingLifecycle(_shared.Logger, exception, this);
                    throw;
                }

                if (GrainInstance is IGrainBase grainBase)
                {
                    try
                    {
                        await grainBase.OnActivateAsync(cancellationToken).WaitAsync(cancellationToken);
                    }
                    catch (Exception exception)
                    {
                        LogErrorInGrainMethod(_shared.Logger, exception, nameof(IGrainBase.OnActivateAsync), this);
                        throw;
                    }
                }

                lock (this)
                {
                    if (State is ActivationState.Activating)
                    {
                        SetState(ActivationState.Valid); // Activate calls on this activation are finished
                        _shared.InternalRuntime.ActivationWorkingSet.OnActivated(this);
                    }
                }

                LogFinishedActivatingGrain(_shared.Logger, this);
            }
            catch (Exception exception)
            {
                CatalogInstruments.ActivationFailedToActivate.Add(1);

                // Capture the exception so that it can be propagated to rejection messages
                var sourceException = (exception as OrleansLifecycleCanceledException)?.InnerException ?? exception;
                LogErrorActivatingGrain(_shared.Logger, sourceException, this);

                // Unregister this as a message target after some period of time.
                // This is delayed so that consistently failing activation, perhaps due to an application bug or network
                // issue, does not cause a flood of doomed activations.
                // If the cancellation token was canceled, there is no need to wait an additional time, since the activation
                // has already waited some significant amount of time.
                if (!cancellationToken.IsCancellationRequested)
                {
                    ScheduleOperation(new Command.Delay(TimeSpan.FromSeconds(5)));
                }

                // Perform the required deactivation steps.
                Deactivate(new(DeactivationReasonCode.ActivationFailed, sourceException, "Failed to activate grain."));

                // Activation failed.
                return;
            }
        }
        catch (Exception exception)
        {
            LogActivationFailed(_shared.Logger, exception, this);
            Deactivate(new(DeactivationReasonCode.ApplicationError, exception, "Failed to activate grain."));
        }
        finally
        {
            _workSignal.Signal();
        }
    }
    #endregion

    #region Deactivation

    /// <summary>
    /// Completes the deactivation process.
    /// </summary>
    private async Task FinishDeactivating(ActivationState previousState, CancellationToken cancellationToken)
    {
        var migrated = false;
        var encounteredError = false;
        try
        {
            LogCompletingDeactivation(_shared.Logger, this);

            // Stop timers from firing.
            DisposeTimers();

            // If the grain was valid when deactivation started, call OnDeactivateAsync.
            if (previousState == ActivationState.Valid)
            {
                if (GrainInstance is IGrainBase grainBase)
                {
                    try
                    {
                        LogBeforeOnDeactivateAsync(_shared.Logger, this);

                        await grainBase.OnDeactivateAsync(DeactivationReason, cancellationToken).WaitAsync(cancellationToken);

                        LogAfterOnDeactivateAsync(_shared.Logger, this);
                    }
                    catch (Exception exception)
                    {
                        LogErrorInGrainMethod(_shared.Logger, exception, nameof(IGrainBase.OnDeactivateAsync), this);

                        // Swallow the exception and continue with deactivation.
                        encounteredError = true;
                    }
                }
            }

            try
            {
                if (_lifecycle is { } lifecycle)
                {
                    // Stops the lifecycle stages which were previously started.
                    // Stages which were never started are ignored.
                    await lifecycle.OnStop(cancellationToken).WaitAsync(cancellationToken);
                }
            }
            catch (Exception exception)
            {
                LogErrorStartingLifecycle(_shared.Logger, exception, this);

                // Swallow the exception and continue with deactivation.
                encounteredError = true;
            }

            if (!encounteredError
                && DehydrationContext is { } context
                && _shared.MigrationManager is { } migrationManager
                && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    ForwardingAddress ??= await PlaceMigratingGrainAsync(context.RequestContext, cancellationToken);

                    if (ForwardingAddress is { } forwardingAddress)
                    {
                        // Populate the dehydration context.
                        if (context.RequestContext is { } requestContext)
                        {
                            RequestContextExtensions.Import(requestContext);
                        }

                        OnDehydrate(context.MigrationContext);

                        // Send the dehydration context to the target host.
                        await migrationManager.MigrateAsync(forwardingAddress, GrainId, context.MigrationContext).AsTask().WaitAsync(cancellationToken);
                        _shared.InternalRuntime.GrainLocator.UpdateCache(GrainId, forwardingAddress);
                        migrated = true;
                    }
                }
                catch (Exception exception)
                {
                    LogFailedToMigrateActivation(_shared.Logger, exception, this);
                }
                finally
                {
                    RequestContext.Clear();
                }
            }

            // If the instance is being deactivated due to a directory failure, we should not unregister it.
            var isDirectoryFailure = DeactivationReason.ReasonCode is DeactivationReasonCode.DirectoryFailure;
            var isShuttingDown = DeactivationReason.ReasonCode is DeactivationReasonCode.ShuttingDown;

            if (!migrated && IsUsingGrainDirectory && !cancellationToken.IsCancellationRequested && !isDirectoryFailure && !isShuttingDown)
            {
                // Unregister from directory.
                // If the grain was migrated, the new activation will perform a check-and-set on the registration itself.
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
            else if (isDirectoryFailure)
            {
                // Optimization: forward to the same host to restart activation without needing to invalidate caches.
                ForwardingAddress ??= Address.SiloAddress;
            }
        }
        catch (Exception ex)
        {
            LogErrorDeactivating(_shared.Logger, ex, this);
        }

        if (IsStuckDeactivating)
        {
            CatalogInstruments.ActivationShutdownViaDeactivateStuckActivation();
        }
        else if (migrated)
        {
            CatalogInstruments.ActivationShutdownViaMigration();
        }
        else if (_isInWorkingSet)
        {
            CatalogInstruments.ActivationShutdownViaDeactivateOnIdle();
        }
        else
        {
            CatalogInstruments.ActivationShutdownViaCollection();
        }

        UnregisterMessageTarget();

        try
        {
            await DisposeAsync();
        }
        catch (Exception exception)
        {
            LogExceptionDisposing(_shared.Logger, exception, this);
        }

        // Signal deactivation
        GetDeactivationCompletionSource().TrySetResult(true);
        _workSignal.Signal();
    }

    private TaskCompletionSource<bool> GetDeactivationCompletionSource()
    {
        lock (this)
        {
            _extras ??= new();
            return _extras.DeactivationTask ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    ValueTask IGrainManagementExtension.DeactivateOnIdle()
    {
        Deactivate(new(DeactivationReasonCode.ApplicationRequested, $"{nameof(IGrainManagementExtension.DeactivateOnIdle)} was called."), CancellationToken.None);
        return default;
    }

    ValueTask IGrainManagementExtension.MigrateOnIdle()
    {
        Migrate(RequestContext.CallContextData?.Value.Values, CancellationToken.None);
        return default;
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

    ValueTask IGrainCallCancellationExtension.CancelRequestAsync(GrainId senderGrainId, CorrelationId messageId)
    {
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
                await Task.Delay(1_000);
            } while (!TryCancelRequest() && --attemptsRemaining > 0);
        }

        bool TryCancelRequest()
        {
            Message? message = null;
            var wasWaiting = false;
            lock (this)
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

            if (message is not null && message.BodyObject is IInvokable request)
            {
                if (TaskScheduler.Current != _workItemGroup.TaskScheduler)
                {
                    // Ensure that cancellation callbacks are performed on the grain's scheduler.
                    _workItemGroup.TaskScheduler.QueueAction(() => CancelRequest(request));
                }
                else
                {
                    CancelRequest(request);
                }

                if (wasWaiting)
                {
                    _shared.InternalRuntime.RuntimeClient.SendResponse(message, Response.FromException(new OperationCanceledException()));
                }

                return true;
            }

            return false;
        }

        void CancelRequest(IInvokable request)
        {
            try
            {
                request.TryCancel();
            }
            catch (Exception exception)
            {
                LogErrorCancellationCallbackFailed(Shared.Logger, exception);
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
        private byte _flags;

        public HashSet<IGrainTimer>? Timers { get => GetValueOrDefault<HashSet<IGrainTimer>>(nameof(Timers)); set => SetOrRemoveValue(nameof(Timers), value); }

        /// <summary>
        /// During rehydration, this may contain the address for the previous (recently dehydrated) activation of this grain.
        /// </summary>
        public GrainAddress? PreviousRegistration { get => GetValueOrDefault<GrainAddress>(nameof(PreviousRegistration)); set => SetOrRemoveValue(nameof(PreviousRegistration), value); }

        /// <summary>
        /// If State == Invalid, this may contain a forwarding address for incoming messages
        /// </summary>
        public SiloAddress? ForwardingAddress { get => GetValueOrDefault<SiloAddress>(nameof(ForwardingAddress)); set => SetOrRemoveValue(nameof(ForwardingAddress), value); }

        /// <summary>
        /// A <see cref="TaskCompletionSource{TResult}"/> which completes when a grain has deactivated.
        /// </summary>
        public TaskCompletionSource<bool>? DeactivationTask { get => GetDeactivationInfoOrDefault()?.DeactivationTask; set => EnsureDeactivationInfo().DeactivationTask = value; }

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
        private readonly CancellationTokenSource _cts = cts;
        public CancellationToken CancellationToken => _cts.Token;

        public virtual void Cancel()
        {
            lock (this)
            {
                if (_disposed) return;
                _cts.Cancel();
            }
        }

        public virtual void Dispose()
        {
            try
            {
                lock (this)
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

        public sealed class Deactivate(CancellationTokenSource cts, ActivationState previousState) : Command(cts)
        {
            public ActivationState PreviousState { get; } = previousState;
        }

        public sealed class Activate(Dictionary<string, object>? requestContext, CancellationTokenSource cts) : Command(cts)
        {
            public Dictionary<string, object>? RequestContext { get; } = requestContext;
        }

        public sealed class Rehydrate(IRehydrationContext context) : Command(new())
        {
            public readonly IRehydrationContext Context = context;

            public override void Dispose()
            {
                base.Dispose();
                (Context as IDisposable)?.Dispose();
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
    }

    private class MigrateWorkItem(ActivationData activation, Dictionary<string, object>? requestContext, CancellationTokenSource cts) : WorkItemBase
    {
        public override string Name => "Migrate";

        public override IGrainContext GrainContext => activation;

        public override void Execute() => activation.StartMigratingAsync(requestContext, cts).Ignore();
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
        Level = LogLevel.Error,
        Message = "Activation of grain {Grain} failed")]
    private static partial void LogActivationFailed(ILogger logger, Exception exception, ActivationData grain);

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
}
