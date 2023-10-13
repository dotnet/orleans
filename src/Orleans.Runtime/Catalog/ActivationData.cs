using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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

namespace Orleans.Runtime
{
    /// <summary>
    /// Maintains additional per-activation state that is required for Orleans internal operations.
    /// MUST lock this object for any concurrent access
    /// Consider: compartmentalize by usage, e.g., using separate interfaces for data for catalog, etc.
    /// </summary>
    internal sealed class ActivationData : IGrainContext, ICollectibleGrainContext, IGrainExtensionBinder, IActivationWorkingSetMember, IGrainTimerRegistry, IGrainManagementExtension, ICallChainReentrantGrainContext, IAsyncDisposable
    {
        private const string GrainAddressMigrationContextKey = "sys.addr";
        private readonly GrainTypeSharedContext _shared;
        private readonly IServiceScope _serviceScope;
        private readonly WorkItemGroup _workItemGroup;
        private readonly List<(Message Message, CoarseStopwatch QueuedTime)> _waitingRequests = new();
        private readonly Dictionary<Message, CoarseStopwatch> _runningRequests = new();
        private readonly SingleWaiterAutoResetEvent _workSignal = new() { RunContinuationsAsynchronously = true };
        private GrainLifecycle _lifecycle;
        private List<object> _pendingOperations;
        private Message _blockingRequest;
        private bool _isInWorkingSet;
        private CoarseStopwatch _busyDuration;
        private CoarseStopwatch _idleDuration;
        private GrainReference _selfReference;

        // Values which are needed less frequently and do not warrant living directly on activation for object size reasons.
        // The values in this field are typically used to represent termination state of an activation or features which are not
        // used by all grains, such as grain timers.
        private ActivationDataExtra _extras;

        // The task representing this activation's message loop.
        // This field is assigned and never read and exists only for debugging purposes (eg, in memory dumps, to associate a loop task with an activation).
#pragma warning disable IDE0052 // Remove unread private members
        private readonly Task _messageLoopTask;
#pragma warning restore IDE0052 // Remove unread private members

        public ActivationData(
            GrainAddress addr,
            Func<IGrainContext, WorkItemGroup> createWorkItemGroup,
            IServiceProvider applicationServices,
            GrainTypeSharedContext shared)
        {
            _shared = shared;
            Address = addr ?? throw new ArgumentNullException(nameof(addr));
            State = ActivationState.Create;
            _serviceScope = applicationServices.CreateScope();
            _isInWorkingSet = true;
            _workItemGroup = createWorkItemGroup(this);
            _messageLoopTask = this.RunOrQueueTask(RunMessageLoop);
        }

        public IGrainRuntime GrainRuntime => _shared.Runtime;
        public object GrainInstance { get; private set; }
        public GrainAddress Address { get; }
        public GrainReference GrainReference => _selfReference ??= _shared.GrainReferenceActivator.CreateReference(GrainId, default);
        public ActivationState State { get; private set; }
        public PlacementStrategy PlacementStrategy => _shared.PlacementStrategy;
        public DateTime CollectionTicket { get; set; }
        public IServiceProvider ActivationServices => _serviceScope.ServiceProvider;
        public ActivationId ActivationId => Address.ActivationId;
        public IGrainLifecycle ObservableLifecycle => Lifecycle;
        internal GrainLifecycle Lifecycle
        {
            get
            {
                if (_lifecycle is { } lifecycle) return lifecycle;
                lock (this) { return _lifecycle ??= new GrainLifecycle(_shared.Logger); }
            }
        }

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

        public SiloAddress ForwardingAddress
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

        private Exception DeactivationException => _extras?.DeactivationReason.Exception;

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

        private HashSet<IGrainTimer> Timers
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

        private DehydrationContextHolder DehydrationContext
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

        public TTarget GetTarget<TTarget>() where TTarget : class => (TTarget)GrainInstance;

        TComponent ITargetHolder.GetComponent<TComponent>()
        {
            var result = GetComponent<TComponent>();
            if (result is null && typeof(IGrainExtension).IsAssignableFrom(typeof(TComponent)))
            {
                var implementation = ActivationServices.GetServiceByKey<Type, IGrainExtension>(typeof(TComponent));
                if (implementation is not TComponent typedResult)
                {
                    throw new GrainExtensionNotInstalledException($"No extension of type {typeof(TComponent)} is installed on this instance and no implementations are registered for automated install");
                }

                SetComponent(typedResult);
                result = typedResult;
            }

            return result;
        }

        public TComponent GetComponent<TComponent>() where TComponent : class
        {
            TComponent result;
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
            else if (ActivationServices.GetService<TComponent>() is { } component)
            {
                SetComponent(component);
                result = component;
            }
            else
            {
                result = _shared.GetComponent<TComponent>();
            }

            return result;
        }

        public void SetComponent<TComponent>(TComponent instance) where TComponent : class
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
            switch (GrainInstance, grainInstance)
            {
                case (null, not null):
                    _shared.OnCreateActivation(this);
                    GetComponent<IActivationLifecycleObserver>()?.OnCreateActivation(this);
                    break;
                case (not null, null):
                    _shared.OnDestroyActivation(this);
                    GetComponent<IActivationLifecycleObserver>()?.OnDestroyActivation(this);
                    break;
            }

            if (grainInstance is ILifecycleParticipant<IGrainLifecycle> participant)
            {
                participant.Participate(ObservableLifecycle);
            }

            GrainInstance = grainInstance;
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
        public LimitExceededException CheckOverloaded()
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
                _shared.Logger.LogWarning(
                    (int)ErrorCode.Catalog_Reject_ActivationTooManyRequests,
                    "Overload - {Count} enqueued requests for activation {Activation}, exceeding hard limit rejection threshold of {HardLimit}",
                    count,
                    this,
                    maxRequestsHardLimit);

                return new LimitExceededException(limitName, count, maxRequestsHardLimit, ToString());
            }

            if (maxRequestsSoftLimit > 0 && count > maxRequestsSoftLimit) // Soft limit
            {
                _shared.Logger.LogWarning(
                    (int)ErrorCode.Catalog_Warn_ActivationTooManyRequests,
                    "Hot - {Count} enqueued requests for activation {Activation}, exceeding soft limit warning threshold of {SoftLimit}",
                    count,
                    this,
                    maxRequestsSoftLimit);
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
                var tmp = _waitingRequests.Select(m => m.Item1).ToList();
                _waitingRequests.Clear();
                return tmp;
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
            if (timespan <= TimeSpan.Zero)
            {
                // reset any current keepAliveUntil
                ResetKeepAliveRequest();
            }
            else if (timespan == TimeSpan.MaxValue)
            {
                // otherwise creates negative time.
                KeepAliveUntil = DateTime.MaxValue;
            }
            else
            {
                KeepAliveUntil = DateTime.UtcNow + timespan;
            }
        }

        public void ResetKeepAliveRequest()
        {
            KeepAliveUntil = DateTime.MinValue;
        }

        private void ScheduleOperation(object operation)
        {
            lock (this)
            {
                _pendingOperations ??= new();
                _pendingOperations.Add(operation);
            }

            _workSignal.Signal();
        }

        public void Migrate(Dictionary<string, object> requestContext, CancellationToken? cancellationToken = default)
        {
            if (!cancellationToken.HasValue)
            {
                cancellationToken = new CancellationTokenSource(_shared.InternalRuntime.CollectionOptions.Value.DeactivationTimeout).Token;
            }

            // We use a named work item since it is cheaper than allocating a Task and has the benefit of being named.
            _workItemGroup.QueueWorkItem(new MigrateWorkItem(this, requestContext, cancellationToken.Value));
        }

        private async Task StartMigratingAsync(Dictionary<string, object> requestContext, CancellationToken cancellationToken)
        {
            lock (this)
            {
                // Avoid the cost of selecting a new location if the activation is not currently valid.
                if (State is not ActivationState.Valid)
                {
                    return;
                }
            }

            SiloAddress newLocation;
            try
            {
                // Run placement to select a new host. If a new (different) host is not selected, do not migrate.
                var placementService = _shared.Runtime.ServiceProvider.GetRequiredService<PlacementService>();
                newLocation = await placementService.PlaceGrainAsync(GrainId, requestContext, PlacementStrategy);
                if (newLocation == Address.SiloAddress || newLocation is null)
                {
                    // No more appropriate silo was selected for this grain. The migration attempt will be aborted.
                    // This could be because this is the only (compatible) silo for the grain or because the placement director chose this
                    // silo for some other reason.
                    if (_shared.Logger.IsEnabled(LogLevel.Debug))
                    {
                        if (newLocation is null)
                        {
                            _shared.Logger.LogDebug("Placement strategy {PlacementStrategy} failed to select a destination for migration of {GrainId}", PlacementStrategy, GrainId);
                        }
                        else
                        {
                            _shared.Logger.LogDebug("Placement strategy {PlacementStrategy} selected the current silo as the destination for migration of {GrainId}", PlacementStrategy, GrainId);
                        }
                    }

                    // Will not deactivate/migrate.
                    return;
                }

                lock (this)
                {
                    if (!StartDeactivating(new DeactivationReason(DeactivationReasonCode.Migrating, "Migrating to a new location")))
                    {
                        // Grain is already deactivating, ignore the migration request.
                        return;
                    }

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

                if (_shared.Logger.IsEnabled(LogLevel.Debug))
                {
                    _shared.Logger.LogDebug("Migrating {GrainId} to {SiloAddress}", GrainId, newLocation);
                }

                // Start deactivation to prevent any other.
                ScheduleOperation(new Command.Deactivate(cancellationToken));
            }
            catch (Exception exception)
            {
                _shared.Logger.LogError(exception, "Error while selecting a migration destination for {GrainId}", GrainId);
                return;
            }
        }

        public void Deactivate(DeactivationReason reason, CancellationToken? token = default)
        {
            if (!token.HasValue)
            {
                token = new CancellationTokenSource(_shared.InternalRuntime.CollectionOptions.Value.DeactivationTimeout).Token;
            }

            StartDeactivating(reason);
            ScheduleOperation(new Command.Deactivate(token.Value));
        }

        private void DeactivateStuckActivation()
        {
            IsStuckProcessingMessage = true;
            var msg = $"Activation {this} has been processing request {_blockingRequest} since {_busyDuration} and is likely stuck.";
            var reason = new DeactivationReason(DeactivationReasonCode.ActivationUnresponsive, msg);

            // Mark the grain as deactivating so that messages are forwarded instead of being invoked
            Deactivate(reason, token: default);

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

        void IGrainTimerRegistry.OnTimerDisposed(IGrainTimer orleansTimerInsideGrain)
        {
            lock (this) // need to lock since dispose can be called on finalizer thread, outside grain context (not single threaded).
            {
                if (Timers is null)
                {
                    return;
                }

                Timers.Remove(orleansTimerInsideGrain);
                if (Timers.Count == 0)
                {
                    Timers = null;
                }
            }
        }

        private void StopAllTimers()
        {
            lock (this)
            {
                if (Timers is null)
                {
                    return;
                }

                foreach (var timer in Timers)
                {
                    timer.Stop();
                }
            }
        }

        private Task WaitForAllTimersToFinish(CancellationToken cancellationToken)
        {
            lock (this)
            {
                if (Timers is null)
                {
                    return Task.CompletedTask;
                }

                var tasks = new List<Task>();
                var timerCopy = Timers.ToList(); // need to copy since OnTimerDisposed will change the timers set.
                foreach (var timer in timerCopy)
                {
                    // first call dispose, then wait to finish.
                    Utils.SafeExecute(timer.Dispose, _shared.Logger, "timer.Dispose has thrown");
                    tasks.Add(timer.GetCurrentlyExecutingTickTask());
                }

                return Task.WhenAll(tasks).WithCancellation(cancellationToken);
            }
        }

        public void AnalyzeWorkload(DateTime now, IMessageCenter messageCenter, MessageFactory messageFactory, SiloMessagingOptions options)
        {
            var slowRunningRequestDuration = options.RequestProcessingWarningTime;
            var longQueueTimeDuration = options.RequestQueueDelayWarningTime;

            List<string> diagnostics = null;
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
                    if (executionTime >= slowRunningRequestDuration)
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
                    if (ReferenceEquals(message, _blockingRequest)) continue;

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

            void GetStatusList(ref List<string> diagnostics)
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

        public async ValueTask DisposeAsync()
        {
            _extras ??= new();
            if (_extras.IsDisposing) return;
            _extras.IsDisposing = true;

            try
            {
                var activator = GetComponent<IGrainActivator>();
                if (activator != null)
                {
                    await activator.DisposeInstance(this, GrainInstance);
                }
            }
            catch (ObjectDisposedException)
            {
            }

            switch (_serviceScope)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync();
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }

        bool IEquatable<IGrainContext>.Equals(IGrainContext other) => ReferenceEquals(this, other);

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

            var implementation = ActivationServices.GetServiceByKey<Type, IGrainExtension>(typeof(TExtensionInterface));
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
                        List<object> operations = null;
                        lock (this)
                        {
                            if (_pendingOperations is { Count: > 0 })
                            {
                                operations = _pendingOperations;
                                _pendingOperations = null;
                            }
                        }

                        if (operations is not null)
                        {
                            await ProcessOperationsAsync(operations);
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
                    Message message = null;
                    lock (this)
                    {
                        if (_waitingRequests.Count <= i)
                        {
                            break;
                        }

                        if (State != ActivationState.Valid)
                        {
                            ProcessRequestsToInvalidActivation();
                            break;
                        }

                        message = _waitingRequests[i].Message;
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
                                        _shared.Logger.LogWarning(
                                            (int)ErrorCode.Dispatcher_ExtendedMessageProcessing,
                                            "Current request has been active for {CurrentRequestActiveTime} for grain {Grain}. Currently executing {BlockingRequest}. Trying to enqueue {Message}.",
                                            currentRequestActiveTime,
                                            ToDetailedString(),
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
                                    message.CacheInvalidationHeader ??= new();
                                    message.CacheInvalidationHeader.Add(new GrainAddress { GrainId = GrainId, SiloAddress = Address.SiloAddress });

                                    var reason = new DeactivationReason(
                                        DeactivationReasonCode.IncompatibleRequest,
                                        $"Received incompatible request for interface {message.InterfaceType} version {message.InterfaceVersion}. This activation supports interface version {currentVersion}.");

                                    Deactivate(reason, token: default);
                                    return;
                                }
                            }
                        }
                        catch (Exception exception)
                        {
                            _shared.InternalRuntime.MessageCenter.RejectMessage(message, Message.RejectionTypes.Transient, exception);
                            _waitingRequests.RemoveAt(i);
                            continue;
                        }

                        // Process this message, removing it from the queue.
                        _waitingRequests.RemoveAt(i);

                        Debug.Assert(State == ActivationState.Valid);
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
                if (State is ActivationState.Create or ActivationState.Activating)
                {
                    // Do nothing until the activation becomes either valid or invalid
                    return;
                }

                if (State is ActivationState.Deactivating)
                {
                    // Determine whether to declare this activation as stuck
                    var deactivatingTime = DateTime.UtcNow - DeactivationStartTime.Value;
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
                        _shared.Logger?.LogError(exception, "Error invoking MayInterleave predicate on grain {Grain} for message {Message}", this, incoming);
                        throw;
                    }
                }

                return false;
            }

            async Task ProcessOperationsAsync(List<object> operations)
            {
                foreach (var op in operations)
                {
                    try
                    {
                        switch (op)
                        {
                            case Command.Rehydrate command:
                                RehydrateInternal(command.Context);
                                break;
                            case Command.Activate command:
                                await ActivateAsync(command.RequestContext, command.CancellationToken);
                                break;
                            case Command.Deactivate command:
                                await FinishDeactivating(command.CancellationToken);
                                break;
                            case Command.Delay command:
                                await Task.Delay(command.Duration);
                                break;
                            case Command.UnregisterFromCatalog:
                                UnregisterMessageTarget();
                                break;
                            default:
                                throw new NotSupportedException($"Encountered unknown operation of type {op?.GetType().ToString() ?? "null"} {op}");
                        }
                    }
                    catch (Exception exception)
                    {
                        _shared.Logger.LogError(exception, "Error in RunOnInactive for grain activation {Activation}", this);
                    }
                }
            }
        }

        private void RehydrateInternal(IRehydrationContext context)
        {
            try
            {
                if (_shared.Logger.IsEnabled(LogLevel.Debug))
                {
                    _shared.Logger.LogDebug("Rehydrating grain from previous activation");
                }

                lock (this)
                {
                    if (State != ActivationState.Create)
                    {
                        throw new InvalidOperationException($"Attempted to rehydrate a grain in the {State} state");
                    }

                    if (context.TryGetValue(GrainAddressMigrationContextKey, out GrainAddress previousRegistration) && previousRegistration is not null)
                    {
                        // Propagate the previous registration, so that the new activation can atomically replace it with its new address.
                        (_extras ??= new()).PreviousRegistration = previousRegistration;
                        if (_shared.Logger.IsEnabled(LogLevel.Debug))
                        {
                            _shared.Logger.LogDebug("Previous activation address was {PreviousRegistration}", previousRegistration);
                        }
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

                if (_shared.Logger.IsEnabled(LogLevel.Debug))
                {
                    _shared.Logger.LogDebug("Rehydrated grain from previous activation");
                }
            }
            catch (Exception exception)
            {
                _shared.Logger.LogError(exception, "Error while rehydrating activation");
            }
            finally
            {
                (context as IDisposable)?.Dispose();
            }
        }

        private void OnDehydrate(IDehydrationContext context)
        {
            if (_shared.Logger.IsEnabled(LogLevel.Debug))
            {
                _shared.Logger.LogDebug("Dehydrating grain activation");
            }

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

            if (_shared.Logger.IsEnabled(LogLevel.Debug))
            {
                _shared.Logger.LogDebug("Dehydrated grain activation");
            }
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
        /// <param name="message">The message that has just completed processing.
        /// This will be <c>null</c> for the case of completion of Activate/Deactivate calls.</param>
        private void OnCompletedRequest(Message message)
        {
            lock (this)
            {
                _runningRequests.Remove(message);

                if (_runningRequests.Count == 0)
                {
                    _idleDuration = CoarseStopwatch.StartNew();
                }

                if (!_isInWorkingSet)
                {
                    _isInWorkingSet = true;
                    _shared.InternalRuntime.ActivationWorkingSet.OnActive(this);
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
                if (State == ActivationState.Invalid || State == ActivationState.FailedToActivate)
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
            if (overloadException != null)
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

                if (_shared.Logger.IsEnabled(LogLevel.Debug))
                    _shared.Logger.LogDebug(
                        (int)ErrorCode.Catalog_RerouteAllQueuedMessages,
                        "RejectAllQueuedMessages: {Count} messages from invalid activation {Activation}.",
                        msgs.Count,
                        this);
                _shared.InternalRuntime.LocalGrainDirectory.InvalidateCacheEntry(Address);
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

                if (_shared.Logger.IsEnabled(LogLevel.Debug)) _shared.Logger.LogDebug((int)ErrorCode.Catalog_RerouteAllQueuedMessages, "Rerouting {NumMessages} messages from invalid grain activation {Grain}", msgs.Count, this);
                _shared.InternalRuntime.LocalGrainDirectory.InvalidateCacheEntry(Address);
                _shared.InternalRuntime.MessageCenter.ProcessRequestsToInvalidActivation(msgs, Address, ForwardingAddress, DeactivationReason.Description, DeactivationException);
            }
        }

        #region Activation

        public void Rehydrate(IRehydrationContext context)
        {
            ScheduleOperation(new Command.Rehydrate(context));
        }

        public void Activate(Dictionary<string, object> requestContext, CancellationToken? cancellationToken)
        {
            if (!cancellationToken.HasValue)
            {
                cancellationToken = new CancellationTokenSource(_shared.InternalRuntime.CollectionOptions.Value.ActivationTimeout).Token;
            }

            ScheduleOperation(new Command.Activate(requestContext, cancellationToken.Value));
        }

        private async Task ActivateAsync(Dictionary<string, object> requestContextData, CancellationToken cancellationToken)
        {
            // A chain of promises that will have to complete in order to complete the activation
            // Register with the grain directory, register with the store if necessary and call the Activate method on the new activation.
            try
            {
                var success = await RegisterActivationInGrainDirectoryAndValidate();
                if (!success)
                {
                    // If registration failed, bail out.
                    return;
                }

                lock (this)
                {
                    SetState(ActivationState.Activating);
                }

                success = await CallActivateAsync(requestContextData, cancellationToken);
                if (!success)
                {
                    // If activation failed, bail out.
                    return;
                }

                _shared.InternalRuntime.ActivationWorkingSet.OnActivated(this);
                if (_shared.Logger.IsEnabled(LogLevel.Debug))
                {
                    _shared.Logger.LogDebug("InitActivation is done: {Address}", Address);
                }
            }
            catch (Exception exception)
            {
                _shared.Logger.LogError(exception, "Activation of grain {Grain} failed", this);
            }
            finally
            {
                _workSignal.Signal();
            }

            async Task<bool> CallActivateAsync(Dictionary<string, object> requestContextData, CancellationToken cancellationToken)
            {
                if (_shared.Logger.IsEnabled(LogLevel.Debug))
                {
                    _shared.Logger.LogDebug((int)ErrorCode.Catalog_BeforeCallingActivate, "Activating grain {Grain}", this);
                }

                // Start grain lifecycle within try-catch wrapper to safely capture any exceptions thrown from called function
                try
                {
                    RequestContextExtensions.Import(requestContextData);
                    await Lifecycle.OnStart(cancellationToken).WithCancellation("Timed out waiting for grain lifecycle to complete activation", cancellationToken);
                    if (GrainInstance is IGrainBase grainBase)
                    {
                        await grainBase.OnActivateAsync(cancellationToken).WithCancellation($"Timed out waiting for {nameof(IGrainBase.OnActivateAsync)} to complete", cancellationToken);
                    }

                    lock (this)
                    {
                        if (State == ActivationState.Activating)
                        {
                            SetState(ActivationState.Valid); // Activate calls on this activation are finished
                        }
                    }

                    if (_shared.Logger.IsEnabled(LogLevel.Debug))
                    {
                        _shared.Logger.LogDebug((int)ErrorCode.Catalog_AfterCallingActivate, "Finished activating grain {Grain}", this);
                    }

                    return true;
                }
                catch (Exception exception)
                {
                    CatalogInstruments.ActivationFailedToActivate.Add(1);

                    // Capture the exception so that it can be propagated to rejection messages
                    var sourceException = (exception as OrleansLifecycleCanceledException)?.InnerException ?? exception;
                    _shared.Logger.LogError((int)ErrorCode.Catalog_ErrorCallingActivate, sourceException, "Error activating grain {Grain}", this);

                    // Unregister the activation from the directory so other silo don't keep sending message to it
                    lock (this)
                    {
                        SetState(ActivationState.FailedToActivate);
                        DeactivationReason = new(DeactivationReasonCode.ActivationFailed, sourceException, "Failed to activate grain.");
                    }

                    GetDeactivationCompletionSource().TrySetResult(true);

                    if (IsUsingGrainDirectory && ForwardingAddress is null)
                    {
                        try
                        {
                            await _shared.InternalRuntime.GrainLocator.Unregister(Address, UnregistrationCause.Force);
                        }
                        catch (Exception ex)
                        {
                            _shared.Logger.LogWarning(
                                (int)ErrorCode.Catalog_UnregisterAsync,
                                ex,
                                "Failed to unregister grain activation {Grain} after activation failed",
                                this);
                        }
                    }

                    // Unregister this as a message target after some period of time.
                    // This is delayed so that consistently failing activation, perhaps due to an application bug or network
                    // issue, does not cause a flood of doomed activations.
                    // If the cancellation token was canceled, there is no need to wait an additional time, since the activation
                    // has already waited some significant amount of time.
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        ScheduleOperation(new Command.Delay(TimeSpan.FromSeconds(5)));
                    }

                    ScheduleOperation(new Command.UnregisterFromCatalog());

                    lock (this)
                    {
                        SetState(ActivationState.Invalid);
                    }

                    return false;
                }
            }
        }

        private async ValueTask<bool> RegisterActivationInGrainDirectoryAndValidate()
        {
            bool success;

            // Currently, the only grain type that is not registered in the Grain Directory is StatelessWorker.
            // Among those that are registered in the directory, we currently do not have any multi activations.
            if (!IsUsingGrainDirectory)
            {
                // Grains which do not use the grain directory do not need to do anything here
                success = true;
            }
            else
            {
                Exception registrationException;
                try
                {
                    var result = await _shared.InternalRuntime.GrainLocator.Register(Address, _extras?.PreviousRegistration);
                    if (Address.Matches(result))
                    {
                        success = true;
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
                        if (_shared.Logger.IsEnabled(LogLevel.Debug))
                        {
                            // If this was a duplicate, it's not an error, just a race.
                            // Forward on all of the pending messages, and then forget about this activation.
                            var primary = _shared.InternalRuntime.LocalGrainDirectory.GetPrimaryForGrain(GrainId);
                            _shared.Logger.LogDebug(
                                (int)ErrorCode.Catalog_DuplicateActivation,
                                "Tried to create a duplicate activation {Address}, but we'll use {ForwardingAddress} instead. "
                                + "GrainInstance type is {GrainInstanceType}. {PrimaryMessage}"
                                + "Full activation address is {Address}. We have {WaitingCount} messages to forward.",
                                Address,
                                ForwardingAddress,
                                GrainInstance?.GetType(),
                                primary != null ? "Primary Directory partition for this grain is " + primary + ". " : string.Empty,
                                Address.ToFullString(),
                                WaitingCount);
                        }
                    }

                    registrationException = null;
                }
                catch (Exception exception)
                {
                    registrationException = exception;
                    _shared.Logger.LogWarning((int)ErrorCode.Runtime_Error_100064, registrationException, "Failed to register grain {Grain} in grain directory", ToString());
                    success = false;
                }

                if (!success)
                {
                    if (DeactivationReason.ReasonCode == DeactivationReasonCode.None)
                    {
                        DeactivationReason = new(DeactivationReasonCode.InternalFailure, registrationException, "Failed to register activation in grain directory.");
                    }

                    lock (this)
                    {
                        SetState(ActivationState.Invalid);
                    }

                    UnregisterMessageTarget();
                }
            }

            return success;
        }
        #endregion

        #region Deactivation

        /// <summary>
        /// Starts the deactivation process.
        /// </summary>
        public bool StartDeactivating(DeactivationReason reason)
        {
            lock (this)
            {
                if (State is ActivationState.Deactivating or ActivationState.Invalid or ActivationState.FailedToActivate)
                {
                    return false;
                }

                if (State is ActivationState.Activating or ActivationState.Create)
                {
                    throw new InvalidOperationException("Calling DeactivateOnIdle from within OnActivateAsync is not supported");
                }

                // If State is Valid, then begin deactivation.

                if (DeactivationReason.ReasonCode == DeactivationReasonCode.None)
                {
                    DeactivationReason = reason;
                }

                DeactivationStartTime = DateTime.UtcNow;
                SetState(ActivationState.Deactivating);
                if (!IsCurrentlyExecuting)
                {
                    StopAllTimers();
                }

                _shared.InternalRuntime.ActivationWorkingSet.OnDeactivating(this);
            }

            return true;
        }

        /// <summary>
        /// Completes the deactivation process.
        /// </summary>
        /// <param name="cancellationToken">A cancellation which terminates graceful deactivation when cancelled.</param>
        private async Task FinishDeactivating(CancellationToken cancellationToken)
        {
            var migrated = false;
            try
            {
                if (_shared.Logger.IsEnabled(LogLevel.Trace))
                {
                    _shared.Logger.LogTrace("FinishDeactivating activation {Activation}", this.ToDetailedString());
                }

                StopAllTimers();

                // Wait timers and call OnDeactivateAsync(reason, cancellationToken)
                await WaitForAllTimersToFinish(cancellationToken);
                await CallGrainDeactivate(cancellationToken);

                if (DehydrationContext is { } context && _shared.MigrationManager is { } migrationManager)
                {
                    Debug.Assert(ForwardingAddress is not null);

                    try
                    {
                        // Populate the dehydration context.
                        if (context.RequestContext is { } requestContext)
                        {
                            RequestContextExtensions.Import(requestContext);
                        }
                        else
                        {
                            RequestContext.Clear();
                        }

                        OnDehydrate(context.Value);

                        // Send the dehydration context to the target host.
                        await migrationManager.MigrateAsync(ForwardingAddress, GrainId, context.Value);
                        migrated = true;
                    }
                    catch (Exception exception)
                    {
                        _shared.Logger.LogWarning(exception, "Failed to migrate grain {GrainId} to {SiloAddress}", GrainId, ForwardingAddress);
                    }
                    finally
                    {
                        RequestContext.Clear();
                    }
                }

                if (!migrated)
                {
                    // Unregister from directory
                    await _shared.InternalRuntime.GrainLocator.Unregister(Address, UnregistrationCause.Force);
                }

                if (_shared.Logger.IsEnabled(LogLevel.Trace))
                {
                    _shared.Logger.LogTrace("Completed async portion of FinishDeactivating for activation {Activation}", this.ToDetailedString());
                }
            }
            catch (Exception ex)
            {
                _shared.Logger.LogWarning((int)ErrorCode.Catalog_DeactivateActivation_Exception, ex, "Exception when trying to deactivate {Activation}", this);
            }

            lock (this)
            {
                SetState(ActivationState.Invalid);
            }

            if (IsStuckDeactivating)
            {
                CatalogInstruments.ActiviationShutdownViaDeactivateStuckActivation();
            }
            else if (migrated)
            {
                CatalogInstruments.ActiviationShutdownViaMigration();
            }
            else if (_isInWorkingSet)
            {
                CatalogInstruments.ActiviationShutdownViaDeactivateOnIdle();
            }
            else
            {
                CatalogInstruments.ActiviationShutdownViaCollection();
            }

            _shared.InternalRuntime.ActivationWorkingSet.OnDeactivated(this);

            try
            {
                UnregisterMessageTarget();
                await DisposeAsync();
            }
            catch (Exception exception)
            {
                _shared.Logger.LogWarning(exception, "Exception disposing activation {Activation}", (ActivationData)this);
            }

            // Signal deactivation
            GetDeactivationCompletionSource().TrySetResult(true);
            _workSignal.Signal();

            if (_shared.Logger.IsEnabled(LogLevel.Trace))
            {
                _shared.Logger.LogTrace("Completed final portion of FinishDeactivating for activation {Activation}", this.ToDetailedString());
            }

            async Task CallGrainDeactivate(CancellationToken ct)
            {
                try
                {
                    // Note: This call is being made from within Scheduler.Queue wrapper, so we are already executing on worker thread
                    if (_shared.Logger.IsEnabled(LogLevel.Debug))
                        _shared.Logger.LogDebug(
                            (int)ErrorCode.Catalog_BeforeCallingDeactivate,
                            "About to call {Activation} grain's OnDeactivateAsync(...) method {GrainInstanceType}",
                            this,
                            GrainInstance?.GetType().FullName);

                    // Call OnDeactivateAsync inline, but within try-catch wrapper to safely capture any exceptions thrown from called function
                    try
                    {
                        // just check in case this activation data is already Invalid or not here at all.
                        if (State == ActivationState.Deactivating)
                        {
                            RequestContext.Clear(); // Clear any previous RC, so it does not leak into this call by mistake.
                            if (GrainInstance is IGrainBase grainBase)
                            {
                                await grainBase.OnDeactivateAsync(DeactivationReason, ct).WithCancellation($"Timed out waiting for {nameof(IGrainBase.OnDeactivateAsync)} to complete", ct);
                            }

                            await Lifecycle.OnStop(ct).WithCancellation("Timed out waiting for grain lifecycle to complete deactivation", ct);
                        }

                        if (_shared.Logger.IsEnabled(LogLevel.Debug))
                            _shared.Logger.LogDebug(
                                (int)ErrorCode.Catalog_AfterCallingDeactivate,
                                "Returned from calling {Activation} grain's OnDeactivateAsync(...) method {GrainInstanceType}",
                                this,
                                GrainInstance?.GetType().FullName);
                    }
                    catch (Exception exc)
                    {
                        _shared.Logger.LogError(
                            (int)ErrorCode.Catalog_ErrorCallingDeactivate,
                            exc,
                            "Error calling grain's OnDeactivateAsync(...) method - Grain type = {GrainType} Activation = {Activation}",
                            GrainInstance?.GetType().FullName,
                            this);
                    }
                }
                catch (Exception exc)
                {
                    _shared.Logger.LogError(
                        (int)ErrorCode.Catalog_FinishGrainDeactivateAndCleanupStreams_Exception,
                        exc,
                        "CallGrainDeactivateAndCleanupStreams Activation = {Activation} failed.",
                        this);
                }
            }
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
            Deactivate(new(DeactivationReasonCode.ApplicationRequested, $"{nameof(IGrainManagementExtension.DeactivateOnIdle)} was called."));
            return default;
        }

        ValueTask IGrainManagementExtension.MigrateOnIdle()
        {
            Migrate(RequestContext.CallContextData?.Value.Values);
            return default;
        }

        private void UnregisterMessageTarget()
        {
            _shared.InternalRuntime.Catalog.UnregisterMessageTarget(this);
            if (GrainInstance is not null)
            {
                SetGrainInstance(null);
            }
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

            public HashSet<IGrainTimer> Timers { get => GetValueOrDefault<HashSet<IGrainTimer>>(nameof(Timers)); set => SetOrRemoveValue(nameof(Timers), value); }

            /// <summary>
            /// During rehydration, this may contain the address for the previous (recently dehydrated) activation of this grain.
            /// </summary>
            public GrainAddress PreviousRegistration { get => GetValueOrDefault<GrainAddress>(nameof(PreviousRegistration)); set => SetOrRemoveValue(nameof(PreviousRegistration), value); }

            /// <summary>
            /// If State == Invalid, this may contain a forwarding address for incoming messages
            /// </summary>
            public SiloAddress ForwardingAddress { get => GetValueOrDefault<SiloAddress>(nameof(ForwardingAddress)); set => SetOrRemoveValue(nameof(ForwardingAddress), value); }

            /// <summary>
            /// A <see cref="TaskCompletionSource{TResult}"/> which completes when a grain has deactivated.
            /// </summary>
            public TaskCompletionSource<bool> DeactivationTask { get => GetDeactivationInfoOrDefault()?.DeactivationTask; set => EnsureDeactivationInfo().DeactivationTask = value; }

            public DateTime? DeactivationStartTime { get => GetDeactivationInfoOrDefault()?.DeactivationStartTime; set => EnsureDeactivationInfo().DeactivationStartTime = value; }

            public DeactivationReason DeactivationReason { get => GetDeactivationInfoOrDefault()?.DeactivationReason ?? default; set => EnsureDeactivationInfo().DeactivationReason = value; }

            /// <summary>
            /// When migrating to another location, this contains the information to preserve across activations.
            /// </summary>
            public DehydrationContextHolder DehydrationContext { get => GetValueOrDefault<DehydrationContextHolder>(nameof(DehydrationContext)); set => SetOrRemoveValue(nameof(DehydrationContext), value); }

            private DeactivationInfo GetDeactivationInfoOrDefault() => GetValueOrDefault<DeactivationInfo>(nameof(DeactivationInfo));
            private DeactivationInfo EnsureDeactivationInfo()
            {
                if (!TryGetValue(nameof(DeactivationInfo), out var info))
                {
                    info = base[nameof(DeactivationInfo)] = new DeactivationInfo();
                }

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
            private T GetValueOrDefault<T>(object key)
            {
                TryGetValue(key, out var result);
                return (T)result;
            }

            private void SetOrRemoveValue(object key, object value)
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
                public TaskCompletionSource<bool> DeactivationTask;
            }
        }

        private class Command
        {
            protected Command() { }

            public class Deactivate : Command
            {
                public Deactivate(CancellationToken cancellation) => CancellationToken = cancellation;
                public CancellationToken CancellationToken { get; }
            }

            public class Activate : Command
            {
                public Activate(Dictionary<string, object> requestContext, CancellationToken cancellationToken)
                {
                    RequestContext = requestContext;
                    CancellationToken = cancellationToken;
                }

                public Dictionary<string, object> RequestContext { get; }
                public CancellationToken CancellationToken { get; }
            }

            public class Rehydrate : Command
            {
                public readonly IRehydrationContext Context;

                public Rehydrate(IRehydrationContext context)
                {
                    Context = context;
                }
            }

            public class Delay : Command
            {
                public Delay(TimeSpan duration)
                {
                    Duration = duration;
                }

                public TimeSpan Duration { get; }
            }

            public class UnregisterFromCatalog : Command
            {
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

        private class DehydrationContextHolder
        {
            public readonly MigrationContext Value;
            public readonly Dictionary<string, object> RequestContext;
            public DehydrationContextHolder(SerializerSessionPool sessionPool, Dictionary<string, object> requestContext)
            {
                RequestContext = requestContext;
                Value = new MigrationContext(sessionPool);
            }
        }

        private class MigrateWorkItem : IWorkItem
        {
            private readonly ActivationData _activation;
            private readonly Dictionary<string, object> _requestContext;
            private readonly CancellationToken _cancellationToken;

            public MigrateWorkItem(ActivationData activation, Dictionary<string, object> requestContext, CancellationToken cancellationToken)
            {
                _activation = activation;
                _requestContext = requestContext;
                _cancellationToken = cancellationToken;
            }

            public string Name => "Migrate";
            public IGrainContext GrainContext => _activation;
            public void Execute() => _activation.StartMigratingAsync(_requestContext, _cancellationToken).Ignore();
        }
    }
}
