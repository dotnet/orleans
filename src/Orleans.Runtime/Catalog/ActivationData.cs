using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.GrainDirectory;
using Orleans.GrainReferences;
using Orleans.Internal;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Scheduler;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.TypeSystem;

namespace Orleans.Runtime
{
    /// <summary>
    /// Maintains additional per-activation state that is required for Orleans internal operations.
    /// MUST lock this object for any concurrent access
    /// Consider: compartmentalize by usage, e.g., using separate interfaces for data for catalog, etc.
    /// </summary>
    internal class ActivationData : IActivationData, IGrainExtensionBinder, IAsyncDisposable, IActivationWorkingSetMember, IGrainManagementExtension
    {
        // This is the maximum amount of time we expect a request to continue processing
        private readonly TimeSpan maxRequestProcessingTime;
        private readonly TimeSpan maxWarningRequestProcessingTime;
        private readonly SiloMessagingOptions messagingOptions;
        private readonly ILogger logger;
        private readonly IServiceScope serviceScope;
        public readonly TimeSpan CollectionAgeLimit;
        private readonly GrainTypeComponents _shared;
        private readonly InternalGrainRuntime _runtime;
        private readonly WorkItemGroup _workItemGroup;
        private Dictionary<Type, object> _components;
        private readonly List<Message> _waitingRequests = new();
        private readonly SingleWaiterSemaphore _messagesSemaphore = new() { RunContinuationsAsynchronously = true };
        private readonly GrainLifecycle lifecycle;
        private readonly Task _messageLoopTask;
        private bool isInWorkingSet;
        private DateTime currentRequestStartTime;
        private DateTime becameIdle;
        private bool collectionCancelledFlag;
        private DateTime keepAliveUntil;
        private ActivationDataExtra _extras;

        public ActivationData(
            ActivationAddress addr,
            Func<IGrainContext, WorkItemGroup> createWorkItemGroup,
            PlacementStrategy placedUsing,
            TimeSpan ageLimit,
            IOptions<SiloMessagingOptions> messagingOptions,
            TimeSpan maxWarningRequestProcessingTime,
            TimeSpan maxRequestProcessingTime,
            ILoggerFactory loggerFactory,
            IServiceProvider applicationServices,
            IGrainRuntime grainRuntime,
            GrainReferenceActivator referenceActivator,
            GrainTypeComponents sharedComponents,
            InternalGrainRuntime runtime)
        {
            Address = addr ?? throw new ArgumentNullException(nameof(addr));
            PlacedUsing = placedUsing ?? throw new ArgumentNullException(nameof(placedUsing));

            _shared = sharedComponents;
            _runtime = runtime;
            logger = loggerFactory.CreateLogger<ActivationData>();
            lifecycle = new GrainLifecycle(loggerFactory.CreateLogger<GrainLifecycle>());
            this.maxRequestProcessingTime = maxRequestProcessingTime;
            this.maxWarningRequestProcessingTime = maxWarningRequestProcessingTime;
            this.messagingOptions = messagingOptions.Value;
            State = ActivationState.Create;
            CollectionAgeLimit = ageLimit;
            GrainReference = referenceActivator.CreateReference(addr.Grain, default);
            serviceScope = applicationServices.CreateScope();
            Runtime = grainRuntime;
            isInWorkingSet = true;
            keepAliveUntil = DateTime.MinValue;
            _workItemGroup = createWorkItemGroup(this);
            _messageLoopTask = this.RunOrQueueTask(RunMessagePump);
        }

        public IGrainRuntime Runtime { get; }
        public IAddressable GrainInstance { get; private set; }
        public ActivationAddress Address { get; private set; }
        public GrainReference GrainReference { get; }
        public ActivationState State { get; private set; }
        public List<object> _pendingOperations { get; private set; }
        public PlacementStrategy PlacedUsing { get; private set; }
        public Message Blocking { get; private set; }
        public Dictionary<Message, DateTime> RunningRequests { get; } = new Dictionary<Message, DateTime>();
        public DateTime CollectionTicket { get; private set; }
        public IServiceProvider ActivationServices => serviceScope.ServiceProvider;
        public ActivationId ActivationId => Address.Activation;
        public IServiceProvider ServiceProvider => serviceScope?.ServiceProvider;
        public IGrainLifecycle ObservableLifecycle => lifecycle;
        internal ILifecycleObserver Lifecycle => lifecycle;
        public GrainId GrainId => Address.Grain;
        internal bool IsExemptFromCollection => CollectionAgeLimit == Timeout.InfiniteTimeSpan;
        public bool ShouldBeKeptAlive => keepAliveUntil != default && keepAliveUntil >= DateTime.UtcNow;

        // Currently, the only supported multi-activation grain is one using the StatelessWorkerPlacement strategy.
        internal bool IsStatelessWorker => PlacedUsing is StatelessWorkerPlacement;

        /// <summary>
        /// Returns a value indicating whether or not this placement strategy requires activations to be registered in
        /// the grain directory.
        /// </summary>
        internal bool IsUsingGrainDirectory => PlacedUsing.IsUsingGrainDirectory;

        public int WaitingCount => _waitingRequests.Count;
        public bool IsInactive => !IsCurrentlyExecuting && _waitingRequests.Count == 0;
        public bool IsCurrentlyExecuting => RunningRequests.Count > 0;
        public IWorkItemScheduler Scheduler => _workItemGroup;

        public ActivationAddress ForwardingAddress
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

        public string Name => string.Format("[Activation: {0}{1}{2}{3}]",
                     Address.Silo,
                     GrainId,
                     ActivationId,
                     GetActivationInfoString());

        public TTarget GetTarget<TTarget>() => (TTarget)GrainInstance;

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

        public TComponent GetComponent<TComponent>()
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
            else if (_components is object && _components.TryGetValue(typeof(TComponent), out var resultObj))
            {
                result = (TComponent)resultObj;
            }
            else
            {
                result = _shared.GetComponent<TComponent>();
            }


            return result;
        }

        public void SetComponent<TComponent>(TComponent instance)
        {
            if (GrainInstance is TComponent)
            {
                throw new ArgumentException("Cannot override a component which is implemented by this grain");
            }

            if (this is TComponent)
            {
                throw new ArgumentException("Cannot override a component which is implemented by this grain context");
            }

            if (instance == null)
            {
                _components?.Remove(typeof(TComponent));
                return;
            }

            if (_components is null) _components = new Dictionary<Type, object>();
            _components[typeof(TComponent)] = instance;
        }

        internal void SetGrainInstance(Grain grainInstance)
        {
            GrainInstance = grainInstance;
        }

        public void OnTimerCreated(IGrainTimer timer)
        {
            AddTimer(timer);
        }

        public void SetState(ActivationState state)
        {
            State = state;
        }

        public bool TrySetCollectionCancelledFlag()
        {
            lock (this)
            {
                if (default(DateTime) == CollectionTicket || collectionCancelledFlag) return false;
                collectionCancelledFlag = true;
                return true;
            }
        }

        public void ResetCollectionCancelledFlag()
        {
            lock (this)
            {
                collectionCancelledFlag = false;
            }
        }

        public void ResetCollectionTicket()
        {
            CollectionTicket = default(DateTime);
        }

        public void SetCollectionTicket(DateTime ticket)
        {
            if (ticket == default(DateTime)) throw new ArgumentException("default(DateTime) is disallowed", "ticket");
            if (CollectionTicket != default(DateTime))
            {
                throw new InvalidOperationException("call ResetCollectionTicket before calling SetCollectionTicket.");
            }

            CollectionTicket = ticket;
        }

        /// <summary>
        /// Check whether this activation is overloaded. 
        /// Returns LimitExceededException if overloaded, otherwise <c>null</c>c>
        /// </summary>
        /// <returns>Returns LimitExceededException if overloaded, otherwise <c>null</c>c></returns>
        public LimitExceededException CheckOverloaded()
        {
            string limitName = LimitNames.LIMIT_MAX_ENQUEUED_REQUESTS;
            int maxRequestsHardLimit = messagingOptions.MaxEnqueuedRequestsHardLimit;
            int maxRequestsSoftLimit = messagingOptions.MaxEnqueuedRequestsSoftLimit;
            if (IsStatelessWorker)
            {
                limitName = LimitNames.LIMIT_MAX_ENQUEUED_REQUESTS_STATELESS_WORKER;
                maxRequestsHardLimit = messagingOptions.MaxEnqueuedRequestsHardLimit_StatelessWorker;
                maxRequestsSoftLimit = messagingOptions.MaxEnqueuedRequestsSoftLimit_StatelessWorker;
            }

            if (maxRequestsHardLimit <= 0 && maxRequestsSoftLimit <= 0) return null; // No limits are set

            int count = GetRequestCount();

            if (maxRequestsHardLimit > 0 && count > maxRequestsHardLimit) // Hard limit
            {
                logger.LogWarning(
                    (int)ErrorCode.Catalog_Reject_ActivationTooManyRequests,
                    "Overload - {Count} enqueued requests for activation {Activation}, exceeding hard limit rejection threshold of {HardLimit}",
                    count,
                    this,
                    maxRequestsHardLimit);

                return new LimitExceededException(limitName, count, maxRequestsHardLimit, ToString());
            }

            if (maxRequestsSoftLimit > 0 && count > maxRequestsSoftLimit) // Soft limit
            {
                logger.LogWarning(
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
                return RunningRequests.Count + WaitingCount;
            }
        }

        internal List<Message> DequeueAllWaitingRequests()
        {
            lock (this)
            {
                var tmp = _waitingRequests.ToList();
                _waitingRequests.Clear();
                return tmp;
            }
        }

        /// <summary>
        /// Returns how long this activation has been idle.
        /// </summary>
        public TimeSpan GetIdleness(DateTime now)
        {
            if (now == default)
            {
                throw new ArgumentException("default(DateTime) is not allowed; Use DateTime.UtcNow instead.", "now");
            }

            return now - becameIdle;
        }

        /// <summary>
        /// Returns whether this activation has been idle long enough to be collected.
        /// </summary>
        public bool IsStale(DateTime now)
        {
            return GetIdleness(now) >= CollectionAgeLimit;
        }

        public void DelayDeactivation(TimeSpan timespan)
        {
            if (timespan <= TimeSpan.Zero)
            {
                // reset any current keepAliveUntill
                ResetKeepAliveRequest();
            }
            else if (timespan == TimeSpan.MaxValue)
            {
                // otherwise creates negative time.
                keepAliveUntil = DateTime.MaxValue;
            }
            else
            {
                keepAliveUntil = DateTime.UtcNow + timespan;
            }
        }

        public void ResetKeepAliveRequest()
        {
            keepAliveUntil = DateTime.MinValue;
        }

        private void ScheduleOperation(object operation)
        {
            lock (this)
            {
                _pendingOperations ??= new();
                _pendingOperations.Add(operation);
            }

            _messagesSemaphore.Signal();
        }

        public void DeactivateOnIdle()
        {
            var token = new CancellationTokenSource(_runtime.CollectionOptions.Value.DeactivationTimeout).Token;
            lock (this)
            {
                if (State is ActivationState.Activating or ActivationState.Create)
                {
                    throw new InvalidOperationException("Calling DeactivateOnIdle from within OnActivateAsync is not supported");
                }

                if (DeactivationReason.Code == DeactivationReasonCode.None)
                {
                    DeactivationReason = new(DeactivationReasonCode.ActivationInitiated, "DeactivateOnIdle");
                }
            }

            Deactivate(token);
        }

        public void Deactivate() => Deactivate(new CancellationTokenSource(_runtime.CollectionOptions.Value.DeactivationTimeout).Token);

        public void Deactivate(CancellationToken token)
        {
            StartDeactivating();
            ScheduleOperation(new Command.Deactivate(token));
        }

        public async Task DeactivateAsync(CancellationToken token)
        {
            Deactivate(token);
            await GetDeactivationCompletionSource().Task;
        }

        private void DeactivateStuckActivation()
        {
            IsStuckProcessingMessage = true;
            var msg = $"Activation {this} has been processing request {Blocking} since {currentRequestStartTime} and is likely stuck";
            DeactivationReason = new(DeactivationReasonCode.StuckProcessingMessage, msg);

            // Mark the grain as deactivating so that messages are forwarded instead of being invoked
            Deactivate();

            // Try to remove this activation from the catalog and directory
            // This leaves this activation dangling, stuck processing the current request until it eventually completes
            // (which likely will never happen at this point, since if the grain was deemed stuck then there is probably some kind of
            // application bug, perhaps a deadlock)
            _runtime.Catalog.UnregisterMessageTarget(this);
            _runtime.GrainLocator.Unregister(Address, UnregistrationCause.Force).Ignore();
        }

        internal void AddTimer(IGrainTimer timer)
        {
            lock (this)
            {
                Timers ??= new HashSet<IGrainTimer>();
                Timers.Add(timer);
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

        public void OnTimerDisposed(IGrainTimer orleansTimerInsideGrain)
        {
            lock (this) // need to lock since dispose can be called on finalizer thread, outside grain context (not single threaded).
            {
                if (Timers is null)
                {
                    return;
                }

                Timers.Remove(orleansTimerInsideGrain);
            }
        }

        internal Task WaitForAllTimersToFinish(CancellationToken cancellationToken)
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
                    Utils.SafeExecute(timer.Dispose, logger, "timer.Dispose has thrown");
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

                if (Blocking is object)
                {
                    var message = Blocking;
                    var timeSinceQueued = now - message.QueuedTime;
                    var executionTime = now - currentRequestStartTime;
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

                foreach (var running in RunningRequests)
                {
                    var message = running.Key;
                    var startTime = running.Value;
                    if (ReferenceEquals(message, Blocking)) continue;

                    // Check how long they've been executing.
                    var executionTime = now - startTime;
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
                foreach (var message in _waitingRequests)
                {
                    var waitTime = now - message.QueuedTime;
                    if (waitTime >= longQueueTimeDuration)
                    {
                        // Message X has been enqueued on the target grain for Y and is currently position QueueLength in queue for processing.
                        GetStatusList(ref diagnostics);
                        var messageDiagnostics = new List<string>(diagnostics)
                        {
                           $"Message {message} has been enqueued on the target grain for {waitTime} and is currently position {queueLength} in queue for processing."
                        };

                        var response = messageFactory.CreateDiagnosticResponseMessage(message, isExecuting: false, isWaiting: true, messageDiagnostics);
                        messageCenter.SendMessage(response);
                    }

                    queueLength++;
                }
            }

            void GetStatusList(ref List<string> diagnostics)
            {
                if (diagnostics is object) return;

                diagnostics = new List<string>
                {
                    ToDetailedString(),
                    $"TaskScheduler status: {_workItemGroup.DumpStatus()}"
                };
            }
        }

        public override string ToString()
        {
            return string.Format("[Activation: {0}/{1}{2}{3} State={4}]",
                 Address.Silo,
                 GrainId,
                 ActivationId,
                 GetActivationInfoString(),
                 State);
        }

        internal string ToDetailedString(bool includeExtraDetails = false)
        {
            lock (this)
            {
                return
                    $"[Activation: {Address.Silo.ToLongString()}/{GrainId.ToString()}{ActivationId} {GetActivationInfoString()} "
                    + $"State={State} NonReentrancyQueueSize={WaitingCount} NumRunning={RunningRequests.Count} "
                    + $"IdlenessTimeSpan={GetIdleness(DateTime.UtcNow)} CollectionAgeLimit={CollectionAgeLimit}"
                    + $"{((includeExtraDetails && Blocking != null) ? " CurrentlyExecuting=" + Blocking : "")}]";
            }
        }

        private string GetActivationInfoString()
        {
            var placement = PlacedUsing != null ? PlacedUsing.GetType().Name : "";
            return GrainInstance is null ? placement : $"#GrainType={RuntimeTypeNameFormatter.Format(GrainInstance?.GetType())} Placement={placement}";
        }

        public async ValueTask DisposeAsync()
        {
            var activator = GetComponent<IGrainActivator>();
            if (activator != null)
            {
                await activator.DisposeInstance(this, GrainInstance);
            }

            switch (serviceScope)
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
            where TExtension : TExtensionInterface
            where TExtensionInterface : IGrainExtension
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
            where TExtensionInterface : IGrainExtension
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
            lock (this)
            {
                var inactive = IsInactive;

                // This instance will remain in the working set if it is either not pending removal or if it is currently active.
                isInWorkingSet = !wouldRemove || !inactive;
                return inactive;
            }
        }

        private async Task RunMessagePump()
        {
            // Note that this loop never terminates. That might look strange, but there is a reason for it:
            // an activation must always accept and process any incoming messages. How an activation processes
            // those messages is up to the activation's state to determine. If the activation has not yet
            // completed activation, it will let the messages continue to queue up until it completes activation.
            // If the activation failed to activate, messages will be responded to with a rejection.
            // If the activation has terminated, messages will be forwarded on to a new activation of this grain.
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

                    await _messagesSemaphore.WaitAsync();
                }
                catch (Exception exception)
                {
                    _runtime.MessagingTrace.LogError(exception, "Error in grain message loop");
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

                        message = _waitingRequests[i];
                        if (!MayInvokeRequest(message))
                        {
                            // The activation is not able to process this message right now, so try the next message.
                            ++i;

                            if (Blocking != null)
                            {
                                var currentRequestActiveTime = DateTime.UtcNow - currentRequestStartTime;
                                if (currentRequestActiveTime > maxRequestProcessingTime && !IsStuckProcessingMessage)
                                {
                                    DeactivateStuckActivation();
                                }
                                else if (currentRequestActiveTime > maxWarningRequestProcessingTime)
                                {
                                    // Consider: Handle long request detection for reentrant activations -- this logic only works for non-reentrant activations
                                    logger.LogWarning(
                                        (int)ErrorCode.Dispatcher_ExtendedMessageProcessing,
                                        "Current request has been active for {CurrentRequestActiveTime} for grain {Grain}. Currently executing {BlockingRequest}. Trying to enqueue {Message}.",
                                        currentRequestActiveTime,
                                        ToDetailedString(),
                                        Blocking,
                                        message);
                                }
                            }

                            continue;
                        }

                        // If the current message is incompatible, deactivate this activation and eventually forward the message to a new incarnation.
                        if (message.InterfaceVersion > 0)
                        {
                            var compatibilityDirector = _runtime.CompatibilityDirectorManager.GetDirector(message.InterfaceType);
                            var currentVersion = _runtime.GrainVersionManifest.GetLocalVersion(message.InterfaceType);
                            if (!compatibilityDirector.IsCompatible(message.InterfaceVersion, currentVersion))
                            {
                                DeactivationReason = new(
                                    DeactivationReasonCode.IncompatibleRequest,
                                    $"Received incompatible request for interface {message.InterfaceType} version {message.InterfaceVersion}. This activation supports version {currentVersion}");

                                Deactivate();
                                return;
                            }
                        }

                        // Process this message, removing it from the queue.
                        _waitingRequests.RemoveAt(i);

                        Debug.Assert(State == ActivationState.Valid);
                        RecordRunning(message, message.IsAlwaysInterleave);

                        void RecordRunning(Message message, bool isInterleavable)
                        {
                            var now = DateTime.UtcNow;
                            RunningRequests.Add(message, now);

                            if (Blocking != null || isInterleavable) return;

                            // This logic only works for non-reentrant activations
                            // Consider: Handle long request detection for reentrant activations.
                            Blocking = message;
                            currentRequestStartTime = now;
                        }
                    }

                    // Start invoking the message outside of the lock
                    InvokeIncomingRequest(message);
                }
                while (true);
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
                    var deactivatingTime = DateTime.UtcNow - DeactivationStartTime.Value;
                    if (deactivatingTime > maxRequestProcessingTime && !IsStuckDeactivating)
                    {
                        IsStuckDeactivating = true;
                        if (DeactivationReason.Text is { Length: > 0 })
                        {
                            var msg = $"Activation {this} has been deactivating since {DeactivationStartTime.Value} and is likely stuck";
                            DeactivationReason = new(DeactivationReason.Code, DeactivationReason.Text + ". " + msg);
                        }
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

                if (Blocking is null)
                {
                    return true;
                }

                if (Blocking.IsReadOnly && incoming.IsReadOnly)
                {
                    return true;
                }

                if (GetComponent<GrainCanInterleave>() is GrainCanInterleave canInterleave)
                {
                    return canInterleave.MayInterleave(incoming);
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
                            case Command.Activate activation:
                                await ActivateAsync(activation.RequestContext, activation.CancellationToken);
                                break;
                            case Command.Deactivate deactivation:
                                await FinishDeactivating(deactivation.CancellationToken);
                                break;
                            case Command.Delay delay:
                                await Task.Delay(delay.Duration);
                                break;
                            case Command.UnregisterMessageTarget:
                                _runtime.Catalog.UnregisterMessageTarget(this);
                                break;
                            default:
                                throw new NotSupportedException($"Encountered unknown operation of type {op?.GetType().ToString() ?? "null"} {op}");
                        }
                    }
                    catch (Exception exception)
                    {
                        logger.LogError(exception, "Error in RunOnInactive for grain activation {Activation}", this);
                    }
                }
            }
        }

        /// <summary>
        /// Handle an incoming message and queue/invoke appropriate handler
        /// </summary>
        /// <param name="message"></param>
        private void InvokeIncomingRequest(Message message)
        {
            MessagingProcessingStatisticsGroup.OnDispatcherMessageProcessedOk(message);
            _runtime.MessagingTrace.OnScheduleMessage(message);

            try
            {
                var task = _runtime.RuntimeClient.Invoke(this, message);

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
                RunningRequests.Remove(message);

                if (RunningRequests.Count == 0)
                {
                    becameIdle = DateTime.UtcNow;
                }

                if (!isInWorkingSet)
                {
                    isInWorkingSet = true;
                    _runtime.ActivationWorkingSet.OnActive(this);
                }

                // The below logic only works for non-reentrant activations
                if (Blocking is null || message.Equals(Blocking))
                {
                    Blocking = null;
                    currentRequestStartTime = DateTime.MinValue;
                }
            }

            // Signal the message pump to see if there is another request which can be processed now that this one has completed
            _messagesSemaphore.Signal();
        }

        public void ReceiveMessage(object message) => ReceiveMessage((Message)message);
        public void ReceiveMessage(Message message)
        {
            _runtime.MessagingTrace.OnDispatcherReceiveMessage(message);

            // Don't process messages that have already timed out
            if (message.IsExpired)
            {
                MessagingProcessingStatisticsGroup.OnDispatcherMessageProcessedError(message);
                _runtime.MessagingTrace.OnDropExpiredMessage(message, MessagingStatisticsGroup.Phase.Dispatch);
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
                    _runtime.MessagingTrace.OnDispatcherReceiveInvalidActivation(message, State);

                    // Always process responses
                    _runtime.RuntimeClient.ReceiveResponse(message);
                    return;
                }

                MessagingProcessingStatisticsGroup.OnDispatcherMessageProcessedOk(message);
                _runtime.RuntimeClient.ReceiveResponse(message);
            }
        }

        private void ReceiveRequest(Message message)
        {
            var overloadException = CheckOverloaded();
            if (overloadException != null)
            {
                MessagingProcessingStatisticsGroup.OnDispatcherMessageProcessedError(message);
                _runtime.MessageCenter.RejectMessage(message, Message.RejectionTypes.Overloaded, overloadException, "Target activation is overloaded " + this);
                return;
            }

            ActivationState state;
            Message blockingMessage;
            lock (this)
            {
                state = State;
                blockingMessage = Blocking;
                if (!message.QueuedTime.HasValue)
                {
                    message.QueuedTime = DateTime.UtcNow;
                }

                _waitingRequests.Add(message);
            }

            _messagesSemaphore.Signal();
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

                if (logger.IsEnabled(LogLevel.Debug))
                    logger.Debug(
                        ErrorCode.Catalog_RerouteAllQueuedMessages,
                        string.Format("RejectAllQueuedMessages: {0} msgs from Invalid activation {1}.", msgs.Count, this));
                _runtime.LocalGrainDirectory.InvalidateCacheEntry(Address);
                _runtime.MessageCenter.ProcessRequestsToInvalidActivation(
                    msgs,
                    Address,
                    forwardingAddress: null,
                    failedOperation: DeactivationReason.Text,
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

                if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug((int)ErrorCode.Catalog_RerouteAllQueuedMessages, "Rerouting {NumMessages} messages from invalid grain activation {Grain}", msgs.Count, this);
                _runtime.LocalGrainDirectory.InvalidateCacheEntry(Address);
                _runtime.MessageCenter.ProcessRequestsToInvalidActivation(msgs, Address, ForwardingAddress, DeactivationReason.Text, DeactivationException);
            }
        }

        #region Activation

        public void Activate(Dictionary<string, object> requestContext) => Activate(requestContext, CancellationToken.None/*new CancellationTokenSource(_runtime.CollectionOptions.Value.ActivationTimeout).Token*/);

        public void Activate(Dictionary<string, object> requestContext, CancellationToken cancellationToken)
        {
            ScheduleOperation(new Command.Activate(requestContext, cancellationToken));
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

                _runtime.ActivationWorkingSet.OnActivated(this);
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    logger.Debug("InitActivation is done: {0}", Address);
                }
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Activation of grain {Grain} failed", this);
            }
            finally
            {
                _messagesSemaphore.Signal();
            }

            async Task<bool> CallActivateAsync(Dictionary<string, object> requestContextData, CancellationToken cancellationToken)
            {
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogDebug((int)ErrorCode.Catalog_BeforeCallingActivate, "Activating grain {Grain}", this);
                }

                // Start grain lifecycle within try-catch wrapper to safely capture any exceptions thrown from called function
                try
                {
                    RequestContextExtensions.Import(requestContextData);
                    await Lifecycle.OnStart(cancellationToken);

                    lock (this)
                    {
                        if (State == ActivationState.Activating)
                        {
                            SetState(ActivationState.Valid); // Activate calls on this activation are finished
                        }
                    }

                    if (logger.IsEnabled(LogLevel.Debug))
                    {
                        logger.LogDebug((int)ErrorCode.Catalog_AfterCallingActivate, "Finished activating grain {Grain}", this);
                    }

                    return true;
                }
                catch (Exception exception)
                {
                    CounterStatistic.FindOrCreate(StatisticNames.CATALOG_ACTIVATION_FAILED_TO_ACTIVATE).Increment();

                    // Capture the exeption so that it can be propagated to rejection messages
                    var sourceException = (exception as OrleansLifecycleCanceledException)?.InnerException ?? exception;
                    logger.LogError((int)ErrorCode.Catalog_ErrorCallingActivate, sourceException, "Error activating grain {Grain}", this);

                    // Unregister the activation from the directory so other silo don't keep sending message to it
                    lock (this)
                    {
                        SetState(ActivationState.FailedToActivate);
                        DeactivationReason = new(DeactivationReasonCode.FailedToActivate, sourceException, "Failed to activate grain");
                    }

                    if (IsUsingGrainDirectory && ForwardingAddress is null)
                    {
                        try
                        {
                            await _runtime.GrainLocator.Unregister(Address, UnregistrationCause.Force);
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(
                                (int)ErrorCode.Catalog_UnregisterAsync,
                                ex,
                                "Failed to unregister grain activation {Grain} after activation failed",
                                this);
                        }
                    }

                    // Unregister this as a message target after some period of time.
                    // This is delayed so that consistently failing activation, perhaps due to an application bug or network
                    // issue, does not cause a flood of doomed activations.
                    ScheduleOperation(new Command.Delay(TimeSpan.FromSeconds(5)));
                    ScheduleOperation(new Command.UnregisterMessageTarget());

                    lock (this)
                    {
                        SetState(ActivationState.Invalid);
                    }

                    return false;
                }
                finally
                {
                    RequestContext.Clear();
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
                    var result = await _runtime.GrainLocator.Register(Address);
                    if (Address.Equals(result))
                    {
                        success = true;
                    }
                    else
                    {
                        // Set the forwarding address so that messages enqueued on this activation can be forwarded to
                        // the existing activation.
                        ForwardingAddress = result;
                        DeactivationReason = new(DeactivationReasonCode.DuplicateActivation, null, "Duplicate activation");
                        success = false;
                        CounterStatistic
                            .FindOrCreate(StatisticNames.CATALOG_ACTIVATION_CONCURRENT_REGISTRATION_ATTEMPTS)
                            .Increment();
                        if (logger.IsEnabled(LogLevel.Debug))
                        {
                            // If this was a duplicate, it's not an error, just a race.
                            // Forward on all of the pending messages, and then forget about this activation.
                            var primary = _runtime.LocalGrainDirectory.GetPrimaryForGrain(ForwardingAddress.Grain);
                            var logMsg =
                                $"Tried to create a duplicate activation {Address}, but we'll use {ForwardingAddress} instead. " +
                                $"GrainInstance Type is {GrainInstance?.GetType()}. " +
                                $"{(primary != null ? "Primary Directory partition for this grain is " + primary + ". " : string.Empty)}" +
                                $"Full activation address is {Address.ToFullString()}. We have {WaitingCount} messages to forward.";
                            logger.Debug(ErrorCode.Catalog_DuplicateActivation, logMsg);
                        }
                    }

                    registrationException = null;
                }
                catch (Exception exception)
                {
                    registrationException = exception;
                    success = false;
                }

                if (!success)
                {
                    lock (this)
                    {
                        SetState(ActivationState.Invalid);
                    }

                    _runtime.Catalog.UnregisterMessageTarget(this);
                    DeactivationReason = new(DeactivationReasonCode.GrainDirectoryFailure, registrationException, "Failed to register activation in grain directory");
                    logger.LogWarning((int)ErrorCode.Runtime_Error_100064, registrationException, "Failed to register grain {Grain} in grain directory", ToString());
                }
            }

            return success;
        }
        #endregion

        #region Deactivation

        /// <summary>
        /// Starts the deactivation process.
        /// </summary>
        public void StartDeactivating()
        {
            lock (this)
            {
                if (State is ActivationState.Deactivating or ActivationState.Invalid or ActivationState.FailedToActivate)
                {
                    return;
                }

                DeactivationStartTime = DateTime.UtcNow;
                SetState(ActivationState.Deactivating);
                if (!IsCurrentlyExecuting)
                {
                    StopAllTimers();
                }

                _runtime.ActivationWorkingSet.OnDeactivating(this);
            }
        }

        /// <summary>
        /// Completes the deactivation process.
        /// </summary>
        /// <param name="cancellationToken">A cancellation which terminates graceful deactivation when cancelled.</param>
        private async Task FinishDeactivating(CancellationToken cancellationToken)
        {
            try
            {
                if (logger.IsEnabled(LogLevel.Trace))
                {
                    logger.LogTrace("FinishDeactivating activation {Activation}", this.ToDetailedString());
                }

                StartDeactivating();
                StopAllTimers();

                // Wait timers and call OnDeactivateAsync()
                await WaitForAllTimersToFinish(cancellationToken);
                await CallGrainDeactivate(cancellationToken);

                // Unregister from directory
                await _runtime.GrainLocator.Unregister(Address, UnregistrationCause.Force);
                if (logger.IsEnabled(LogLevel.Trace))
                {
                    logger.LogTrace("Completed async portion of FinishDeactivating for activation {Activation}", this.ToDetailedString());
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning((int)ErrorCode.Catalog_DeactivateActivation_Exception, ex, "Exception when trying to deactivate {Activation}", this);
            }

            lock (this)
            {
                SetState(ActivationState.Invalid);
            }

            if (IsStuckDeactivating)
            {
                CounterStatistic.FindOrCreate(StatisticNames.CATALOG_ACTIVATION_SHUTDOWN_VIA_DEACTIVATE_STUCK_ACTIVATION).Increment();
            }
            else if (isInWorkingSet)
            {
                CounterStatistic.FindOrCreate(StatisticNames.CATALOG_ACTIVATION_SHUTDOWN_VIA_DEACTIVATE_ON_IDLE).Increment();
            }
            else
            {
                CounterStatistic.FindOrCreate(StatisticNames.CATALOG_ACTIVATION_SHUTDOWN_VIA_COLLECTION).Increment();
            }

            _runtime.ActivationWorkingSet.OnDeactivated(this);

            try
            {
                _runtime.Catalog.UnregisterMessageTarget(this);
                await DisposeAsync();
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Exception disposing activation {Activation}", (ActivationData)this);
            }

            // Signal deactivation
            GetDeactivationCompletionSource().TrySetResult(true);
            _messagesSemaphore.Signal();

            if (logger.IsEnabled(LogLevel.Trace))
            {
                logger.LogTrace("Completed final portion of FinishDeactivating for activation {Activation}", this.ToDetailedString());
            }

            async Task CallGrainDeactivate(CancellationToken ct)
            {
                try
                {
                    // Note: This call is being made from within Scheduler.Queue wrapper, so we are already executing on worker thread
                    if (logger.IsEnabled(LogLevel.Debug)) logger.Debug(ErrorCode.Catalog_BeforeCallingDeactivate, "About to call {1} grain's OnDeactivateAsync() method {0}", this, GrainInstance?.GetType().FullName);

                    // Call OnDeactivateAsync inline, but within try-catch wrapper to safely capture any exceptions thrown from called function
                    try
                    {
                        // just check in case this activation data is already Invalid or not here at all.
                        if (State == ActivationState.Deactivating)
                        {
                            RequestContext.Clear(); // Clear any previous RC, so it does not leak into this call by mistake. 
                            await Lifecycle.OnStop(ct).WithCancellation(ct);
                        }

                        if (logger.IsEnabled(LogLevel.Debug)) logger.Debug(ErrorCode.Catalog_AfterCallingDeactivate, "Returned from calling {1} grain's OnDeactivateAsync() method {0}", this, GrainInstance?.GetType().FullName);
                    }
                    catch (Exception exc)
                    {
                        logger.Error(ErrorCode.Catalog_ErrorCallingDeactivate,
                            string.Format("Error calling grain's OnDeactivateAsync() method - Grain type = {1} Activation = {0}", this, GrainInstance?.GetType().FullName), exc);
                    }
                }
                catch (Exception exc)
                {
                    logger.Error(ErrorCode.Catalog_FinishGrainDeactivateAndCleanupStreams_Exception, string.Format("CallGrainDeactivateAndCleanupStreams Activation = {0} failed.", this), exc);
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

        Task IGrainManagementExtension.DeactivateOnIdle()
        {
            DeactivateOnIdle();
            return Task.CompletedTask;
        }

        #endregion

        /// <summary>
        /// Additional properties which are not needed for the majority of an activation's lifecycle.
        /// </summary>
        private class ActivationDataExtra
        {
            /// <summary>
            /// If State == Invalid, this may contain a forwarding address for incoming messages
            /// </summary>
            public ActivationAddress ForwardingAddress { get; set; }

            /// <summary>
            /// A <see cref="TaskCompletionSource{TResult}"/> which completes when a grain has deactivated.
            /// </summary>
            public TaskCompletionSource<bool> DeactivationTask { get; set; }

            public HashSet<IGrainTimer> Timers { get; set; }

            public DateTime? DeactivationStartTime { get; set; }

            public bool IsStuckProcessingMessage { get; set; }

            public bool IsStuckDeactivating { get; set; }

            public DeactivationReason DeactivationReason { get; set; }
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

            public class Delay : Command
            {
                public Delay(TimeSpan duration)
                {
                    Duration = duration;
                }

                public TimeSpan Duration { get; }
            }

            public class UnregisterMessageTarget : Command
            {
            }
        }
    }

    internal struct DeactivationReason
    {
        public DeactivationReason(DeactivationReasonCode code, string text)
        {
            Code = code;
            Text = text;
            Exception = null;
        }

        public DeactivationReason(DeactivationReasonCode code, Exception exception, string text)
        {
            Code = code;
            Text = text;
            Exception = exception;
        }

        public string Text { get; }
        public DeactivationReasonCode Code { get; }

        /// <summary>
        /// If not null, contains the exception thrown during activation.
        /// </summary>
        public Exception Exception { get; }
    }

    internal enum DeactivationReasonCode : byte
    {
        None,
        IdleActivationCollector,
        FailedToActivate,
        GrainDirectoryFailure,
        ActivationInitiated, // DeactivateOnIdle
        StuckProcessingMessage,
        DuplicateActivation,
        IncompatibleRequest,
    }
}
