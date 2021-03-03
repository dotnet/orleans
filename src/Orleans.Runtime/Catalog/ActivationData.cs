using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.GrainReferences;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Scheduler;

namespace Orleans.Runtime
{
    /// <summary>
    /// Maintains additional per-activation state that is required for Orleans internal operations.
    /// MUST lock this object for any concurrent access
    /// Consider: compartmentalize by usage, e.g., using separate interfaces for data for catalog, etc.
    /// </summary>
    internal class ActivationData : IActivationData, IGrainExtensionBinder, IAsyncDisposable
    {
        // This is the maximum amount of time we expect a request to continue processing
        private readonly TimeSpan maxRequestProcessingTime;
        private readonly TimeSpan maxWarningRequestProcessingTime;
        private readonly SiloMessagingOptions messagingOptions;
        private readonly ILogger logger;
        private readonly IServiceScope serviceScope;
        public readonly TimeSpan CollectionAgeLimit;
        private readonly GrainTypeComponents _shared;
        private readonly ActivationMessageScheduler _messageScheduler;
        private readonly Action<object> _receiveMessageInScheduler;
        private HashSet<IGrainTimer> timers;
        private Dictionary<Type, object> _components;

        public ActivationData(
            ActivationAddress addr,
            PlacementStrategy placedUsing,
            IActivationCollector collector,
            TimeSpan ageLimit,
            IOptions<SiloMessagingOptions> messagingOptions,
            TimeSpan maxWarningRequestProcessingTime,
            TimeSpan maxRequestProcessingTime,
            ILoggerFactory loggerFactory,
            IServiceProvider applicationServices,
            IGrainRuntime grainRuntime,
            GrainReferenceActivator referenceActivator,
            GrainTypeComponents sharedComponents,
            ActivationMessageScheduler messageScheduler)
        {
            if (null == addr) throw new ArgumentNullException(nameof(addr));
            if (null == placedUsing) throw new ArgumentNullException(nameof(placedUsing));
            if (null == collector) throw new ArgumentNullException(nameof(collector));

            _receiveMessageInScheduler = state => this.ReceiveMessageInScheduler(state);
            _shared = sharedComponents;
            _messageScheduler = messageScheduler;
            logger = loggerFactory.CreateLogger<ActivationData>();
            this.lifecycle = new GrainLifecycle(loggerFactory.CreateLogger<LifecycleSubject>());
            this.maxRequestProcessingTime = maxRequestProcessingTime;
            this.maxWarningRequestProcessingTime = maxWarningRequestProcessingTime;
            this.messagingOptions = messagingOptions.Value;
            ResetKeepAliveRequest();
            Address = addr;
            State = ActivationState.Create;
            PlacedUsing = placedUsing;
            if (!this.GrainId.IsSystemTarget())
            {
                this.collector = collector;
            }

            CollectionAgeLimit = ageLimit;

            this.GrainReference = referenceActivator.CreateReference(addr.Grain, default);
            this.serviceScope = applicationServices.CreateScope();
            this.Runtime = grainRuntime;
        }

        public IGrainRuntime Runtime { get; }

        public IServiceProvider ActivationServices => this.serviceScope.ServiceProvider;

        internal WorkItemGroup WorkItemGroup { get; set; }

        public async ValueTask ActivateAsync(CancellationToken cancellation)
        {
            await this.Lifecycle.OnStart(cancellation);

            lock (this)
            {
                if (this.State == ActivationState.Activating)
                {
                    this.SetState(ActivationState.Valid); // Activate calls on this activation are finished
                }

                this.collector.ScheduleCollection(this);

                if (!this.IsCurrentlyExecuting)
                {
                    this.RunOnInactive();
                }

                // Run message pump to see if there is a new request is queued to be processed
                _messageScheduler.RunMessagePump(this);
            }
        }

        public TComponent GetComponent<TComponent>()
        {
            TComponent result;
            if (this.GrainInstance is TComponent grainResult)
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
            if (this.GrainInstance is TComponent)
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

        public IAddressable GrainInstance { get; private set; }

        public ActivationId ActivationId { get { return Address.Activation; } }

        public ActivationAddress Address { get; private set; }

        public IServiceProvider ServiceProvider => this.serviceScope?.ServiceProvider;

        private readonly GrainLifecycle lifecycle;

        public IGrainLifecycle ObservableLifecycle => lifecycle;

        internal ILifecycleObserver Lifecycle => lifecycle;

        public void OnTimerCreated(IGrainTimer timer)
        {
            AddTimer(timer);
        }

        public GrainReference GrainReference { get; }

        public SiloAddress Silo { get { return Address.Silo;  } }

        public GrainId GrainId { get { return Address.Grain; } }

        public ActivationState State { get; private set; }

        public void SetState(ActivationState state)
        {
            State = state;
        }

        // Don't accept any new messages and stop all timers.
        public void PrepareForDeactivation()
        {
            SetState(ActivationState.Deactivating);
            deactivationStartTime = DateTime.UtcNow;
            if (!IsCurrentlyExecuting)
                StopAllTimers();
        }

        /// <summary>
        /// If State == Invalid, this may contain a forwarding address for incoming messages
        /// </summary>
        public ActivationAddress ForwardingAddress { get; set; }

        private IActivationCollector collector;

        internal bool IsExemptFromCollection
        {
            get { return collector == null; }
        }

        public DateTime CollectionTicket { get; private set; }
        private bool collectionCancelledFlag;

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

        public PlacementStrategy PlacedUsing { get; private set; }

        // Currently, the only supported multi-activation grain is one using the StatelessWorkerPlacement strategy.
        internal bool IsStatelessWorker => this.PlacedUsing is StatelessWorkerPlacement;
        
        /// <summary>
        /// Returns a value indicating whether or not this placement strategy requires activations to be registered in
        /// the grain directory.
        /// </summary>
        internal bool IsUsingGrainDirectory => this.PlacedUsing.IsUsingGrainDirectory;

        public Message Blocking { get; private set; }
        public Dictionary<Message, DateTime> RunningRequests { get; private set; } = new Dictionary<Message, DateTime>();

        private DateTime currentRequestStartTime;
        private DateTime becameIdle;
        private DateTime deactivationStartTime;

        public void RecordRunning(Message message, bool isInterleavable)
        {
            // Note: This method is always called while holding lock on this activation, so no need for additional locks here
            var now = DateTime.UtcNow;
            RunningRequests.Add(message, now);

            if (this.Blocking != null || isInterleavable) return;

            // This logic only works for non-reentrant activations
            // Consider: Handle long request detection for reentrant activations.
            this.Blocking = message;
            currentRequestStartTime = now;
        }

        public void ResetRunning(Message message)
        {
            // Note: This method is always called while holding lock on this activation, so no need for additional locks here
            RunningRequests.Remove(message);

            if (RunningRequests.Count == 0)
            {
                becameIdle = DateTime.UtcNow;
                if (!IsExemptFromCollection)
                {
                    collector.TryRescheduleCollection(this);
                }
            }

            // The below logic only works for non-reentrant activations.
            if (this.Blocking != null && !message.Equals(this.Blocking)) return;

            this.Blocking = null;
            currentRequestStartTime = DateTime.MinValue;
        }

        private long inFlightCount;
        private long enqueuedOnDispatcherCount;

        /// <summary>
        /// Number of messages that are actively being processed [as opposed to being in the Waiting queue].
        /// In most cases this will be 0 or 1, but for Reentrant grains can be >1.
        /// </summary>
        public long InFlightCount { get { return Interlocked.Read(ref inFlightCount); } }

        /// <summary>
        /// Number of messages that are being received [as opposed to being in the scheduler queue or actively processed].
        /// </summary>
        public long EnqueuedOnDispatcherCount { get { return Interlocked.Read(ref enqueuedOnDispatcherCount); } }

        /// <summary>Increment the number of in-flight messages currently being processed.</summary>
        public void IncrementInFlightCount() { Interlocked.Increment(ref inFlightCount); }
        
        /// <summary>Decrement the number of in-flight messages currently being processed.</summary>
        public void DecrementInFlightCount() { Interlocked.Decrement(ref inFlightCount); }

        /// <summary>Increment the number of messages currently in the process of being received.</summary>
        public void IncrementEnqueuedOnDispatcherCount() { Interlocked.Increment(ref enqueuedOnDispatcherCount); }

        /// <summary>Decrement the number of messages currently in the process of being received.</summary>
        public void DecrementEnqueuedOnDispatcherCount() { Interlocked.Decrement(ref enqueuedOnDispatcherCount); }
       
        /// <summary>
        /// grouped by sending activation: responses first, then sorted by id
        /// </summary>
        private List<Message> waiting;

        public int WaitingCount 
        { 
            get
            {
                return waiting == null ? 0 : waiting.Count;
            }
        }

        public enum EnqueueMessageResult
        {
            Success,
            ErrorInvalidActivation,
            ErrorStuckActivation,
            ErrorActivateFailed,
        }

        /// <summary>
        /// Insert in a FIFO order
        /// </summary>
        /// <param name="message"></param>
        public EnqueueMessageResult EnqueueMessage(Message message)
        {
            lock (this)
            {
                if (State == ActivationState.Invalid)
                {
                    logger.Warn(ErrorCode.Dispatcher_InvalidActivation,
                        "Cannot enqueue message to invalid activation {0} : {1}", this.ToDetailedString(), message);
                    return EnqueueMessageResult.ErrorInvalidActivation;
                }
                if (State == ActivationState.FailedToActivate)
                {
                    logger.Warn(ErrorCode.Dispatcher_InvalidActivation,
                        "Cannot enqueue message to activation that failed in OnActivate {0} : {1}", this.ToDetailedString(), message);
                    return EnqueueMessageResult.ErrorActivateFailed;
                }
                if (State == ActivationState.Deactivating)
                {
                    var deactivatingTime = DateTime.UtcNow - deactivationStartTime;
                    if (deactivatingTime > maxRequestProcessingTime)
                    {
                        logger.Error(ErrorCode.Dispatcher_StuckActivation,
                            $"Current activation {ToDetailedString()} marked as Deactivating for {deactivatingTime}. Trying to enqueue {message}.");
                        return EnqueueMessageResult.ErrorStuckActivation;
                    }
                }
                if (this.Blocking != null)
                {
                    var currentRequestActiveTime = DateTime.UtcNow - currentRequestStartTime;
                    if (currentRequestActiveTime > maxRequestProcessingTime)
                    {
                        logger.Error(ErrorCode.Dispatcher_StuckActivation,
                            $"Current request has been active for {currentRequestActiveTime} for activation {ToDetailedString()}. Currently executing {this.Blocking}. Trying to enqueue {message}.");
                        return EnqueueMessageResult.ErrorStuckActivation;
                    }
                    // Consider: Handle long request detection for reentrant activations -- this logic only works for non-reentrant activations
                    else if (currentRequestActiveTime > maxWarningRequestProcessingTime)
                    {
                        logger.Warn(ErrorCode.Dispatcher_ExtendedMessageProcessing,
                             "Current request has been active for {0} for activation {1}. Currently executing {2}. Trying  to enqueue {3}.",
                             currentRequestActiveTime, this.ToDetailedString(), this.Blocking, message);
                    }
                }

                if (!message.QueuedTime.HasValue)
                {
                    message.QueuedTime = DateTime.UtcNow;
                }

                waiting ??= new List<Message>();
                waiting.Add(message);

                return EnqueueMessageResult.Success;
            }
        }

        /// <summary>
        /// Check whether this activation is overloaded. 
        /// Returns LimitExceededException if overloaded, otherwise <c>null</c>c>
        /// </summary>
        /// <returns>Returns LimitExceededException if overloaded, otherwise <c>null</c>c></returns>
        public LimitExceededException CheckOverloaded()
        {
            string limitName = LimitNames.LIMIT_MAX_ENQUEUED_REQUESTS;
            int maxRequestsHardLimit = this.messagingOptions.MaxEnqueuedRequestsHardLimit;
            int maxRequestsSoftLimit = this.messagingOptions.MaxEnqueuedRequestsSoftLimit;
            if (IsStatelessWorker)
            {
                limitName = LimitNames.LIMIT_MAX_ENQUEUED_REQUESTS_STATELESS_WORKER;
                maxRequestsHardLimit = this.messagingOptions.MaxEnqueuedRequestsHardLimit_StatelessWorker;
                maxRequestsSoftLimit = this.messagingOptions.MaxEnqueuedRequestsSoftLimit_StatelessWorker;
            }

            if (maxRequestsHardLimit <= 0 && maxRequestsSoftLimit <= 0) return null; // No limits are set

            int count = GetRequestCount();

            if (maxRequestsHardLimit > 0 && count > maxRequestsHardLimit) // Hard limit
            {
                this.logger.LogWarning(
                    (int)ErrorCode.Catalog_Reject_ActivationTooManyRequests,
                    "Overload - {Count} enqueued requests for activation {Activation}, exceeding hard limit rejection threshold of {HardLimit}",
                    count,
                    this,
                    maxRequestsHardLimit);

                return new LimitExceededException(limitName, count, maxRequestsHardLimit, this.ToString());
            }

            if (maxRequestsSoftLimit > 0 && count > maxRequestsSoftLimit) // Soft limit
            {
                this.logger.LogWarning(
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
                long numInDispatcher = EnqueuedOnDispatcherCount;
                long numActive = InFlightCount;
                long numWaiting = WaitingCount;
                return (int)(numInDispatcher + numActive + numWaiting);
            }
        }

        public Message PeekNextWaitingMessage()
        {
            if (waiting != null && waiting.Count > 0) return waiting[0];
            return null;
        }

        public void DequeueNextWaitingMessage()
        {
            if (waiting != null && waiting.Count > 0)
                waiting.RemoveAt(0);
        }

        internal List<Message> DequeueAllWaitingMessages()
        {
            lock (this)
            {
                if (waiting == null) return null;
                List<Message> tmp = waiting;
                waiting = null;
                return tmp;
            }
        }

        public bool IsInactive
        {
            get
            {
                return !IsCurrentlyExecuting && (waiting == null || waiting.Count == 0);
            }
        }

        public bool IsCurrentlyExecuting
        {
            get
            {
                return RunningRequests.Count > 0;
            }
        }

        /// <summary>
        /// Returns how long this activation has been idle.
        /// </summary>
        public TimeSpan GetIdleness(DateTime now)
        {
            if (now == default(DateTime))
                throw new ArgumentException("default(DateTime) is not allowed; Use DateTime.UtcNow instead.", "now");
            
            return now - becameIdle;
        }

        /// <summary>
        /// Returns whether this activation has been idle long enough to be collected.
        /// </summary>
        public bool IsStale(DateTime now)
        {
            return GetIdleness(now) >= CollectionAgeLimit;
        }

        private DateTime keepAliveUntil;

        public bool ShouldBeKeptAlive { get { return keepAliveUntil >= DateTime.UtcNow; } }

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


        public List<Action> OnInactive { get; set; } // ActivationData

        public void AddOnInactive(Action action) // ActivationData
        {
            lock (this)
            {
                if (OnInactive == null)
                {
                    OnInactive = new List<Action>();
                }
                OnInactive.Add(action);
                if (!IsCurrentlyExecuting)
                {
                    RunOnInactive();
                }
            }
        }

        public void RunOnInactive()
        {
            lock (this)
            {
                if (OnInactive == null) return;

                var actions = OnInactive;
                OnInactive = null;
                foreach (var action in actions)
                {
                    action();
                }
            }
        }

        internal void AddTimer(IGrainTimer timer)
        {
            lock(this)
            {
                if (timers == null)
                {
                    timers = new HashSet<IGrainTimer>();
                }
                timers.Add(timer);
            }
        }

        private void StopAllTimers()
        {
            lock (this)
            {
                if (timers == null) return;

                foreach (var timer in timers)
                {
                    timer.Stop();
                }
            }
        }

        public void OnTimerDisposed(IGrainTimer orleansTimerInsideGrain)
        {
            lock (this) // need to lock since dispose can be called on finalizer thread, outside grain context (not single threaded).
            {
                timers.Remove(orleansTimerInsideGrain);
            }
        }

        internal Task WaitForAllTimersToFinish()
        {
            lock(this)
            { 
                if (timers == null)
                {
                    return Task.CompletedTask;
                }
                var tasks = new List<Task>();
                var timerCopy = timers.ToList(); // need to copy since OnTimerDisposed will change the timers set.
                foreach (var timer in timerCopy)
                {
                    // first call dispose, then wait to finish.
                    Utils.SafeExecute(timer.Dispose, logger, "timer.Dispose has thrown");
                    tasks.Add(timer.GetCurrentlyExecutingTickTask());
                }
                return Task.WhenAll(tasks);
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

                if (this.Blocking is object)
                {
                    var message = this.Blocking;
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
                    if (ReferenceEquals(message, this.Blocking)) continue;

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

                if (waiting is object)
                {
                    var queueLength = 1;
                    foreach (var message in waiting)
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
            }

            void GetStatusList(ref List<string> diagnostics)
            {
                if (diagnostics is object) return;

                diagnostics = new List<string>
                {
                    this.ToDetailedString(),
                    $"TaskScheduler status: {this.WorkItemGroup.DumpStatus()}"
                };
            }
        }

        public string DumpStatus()
        {
            var sb = new StringBuilder();
            lock (this)
            {
                sb.AppendFormat("   {0}", ToDetailedString());

                if (this.Blocking != null)
                {
                    sb.AppendFormat("   Processing message: {0}", this.Blocking);
                }

                foreach (var msg in RunningRequests)
                {
                    if (ReferenceEquals(msg.Key, this.Blocking)) continue;
                    sb.AppendFormat("   Processing message: {0}", msg);
                }

                if (waiting!=null && waiting.Count > 0)
                {
                    sb.AppendFormat("   Messages queued within ActivationData: {0}", PrintWaitingQueue());
                }
            }
            return sb.ToString();
        }

        public override string ToString()
        {
            return String.Format("[Activation: {0}/{1}{2}{3} State={4}]",
                 Silo,
                 this.GrainId,
                 this.ActivationId,
                 GetActivationInfoString(),
                 State);
        }

        internal string ToDetailedString(bool includeExtraDetails = false)
        {
            return
                String.Format(
                    "[Activation: {0}/{1}{2} {3} State={4} NonReentrancyQueueSize={5} EnqueuedOnDispatcher={6} InFlightCount={7} NumRunning={8} IdlenessTimeSpan={9} CollectionAgeLimit={10}{11}]",
                    Silo.ToLongString(),
                    this.GrainId.ToString(),
                    this.ActivationId,
                    GetActivationInfoString(),
                    State,                          // 4
                    WaitingCount,                   // 5 NonReentrancyQueueSize
                    EnqueuedOnDispatcherCount,      // 6 EnqueuedOnDispatcher
                    InFlightCount,                  // 7 InFlightCount
                    RunningRequests.Count,          // 8 NumRunning
                    GetIdleness(DateTime.UtcNow),   // 9 IdlenessTimeSpan
                    CollectionAgeLimit,             // 10 CollectionAgeLimit
                    (includeExtraDetails && this.Blocking != null) ? " CurrentlyExecuting=" + this.Blocking : "");  // 11: Running
        }

        public string Name
        {
            get
            {
                return String.Format("[Activation: {0}{1}{2}{3}]",
                     Silo,
                     this.GrainId,
                     this.ActivationId,
                     GetActivationInfoString());
            }
        }

        /// <summary>
        /// Return string containing dump of the queue of waiting work items
        /// </summary>
        /// <returns></returns>
        /// <remarks>Note: Caller must be holding lock on this activation while calling this method.</remarks>
        internal string PrintWaitingQueue()
        {
            return Utils.EnumerableToString(waiting);
        }

        private string GetActivationInfoString()
        {
            var placement = PlacedUsing != null ? PlacedUsing.GetType().Name : String.Empty;
            return GrainInstance is null ? placement : $"#GrainType={GrainInstance.GetType().FullName} Placement={placement}";
        }

        public async ValueTask DisposeAsync()
        {
            var activator = this.GetComponent<IGrainActivator>();
            if (activator != null)
            {
                await activator.DisposeInstance(this, this.GrainInstance);
            } 

            switch (this.serviceScope)
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
            if (this.GetComponent<TExtensionInterface>() is object existing)
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
                this.SetComponent<TExtensionInterface>(implementation);
            }

            var reference = this.GrainReference.Cast<TExtensionInterface>();
            return (implementation, reference);
        }

        public TExtensionInterface GetExtension<TExtensionInterface>()
            where TExtensionInterface : IGrainExtension
        {
            if (this.GetComponent<TExtensionInterface>() is TExtensionInterface result)
            {
                return result;
            }

            var implementation = this.ActivationServices.GetServiceByKey<Type, IGrainExtension>(typeof(TExtensionInterface));
            if (!(implementation is TExtensionInterface typedResult))
            {
                throw new GrainExtensionNotInstalledException($"No extension of type {typeof(TExtensionInterface)} is installed on this instance and no implementations are registered for automated install");
            }

            this.SetComponent<TExtensionInterface>(typedResult);
            return typedResult;
        }

        public void ReceiveMessage(object message)
        {
            var msg = (Message)message;
            lock (this)
            {
                // Get the activation's scheduler or the default task scheduler if the activation is not valid.
                // Requests to an invalid activation are handled later.
                var scheduler = this.WorkItemGroup?.TaskScheduler ?? TaskScheduler.Default;
                this.IncrementEnqueuedOnDispatcherCount();

                // Enqueue the handler on the activation's scheduler
                var task = new Task(_receiveMessageInScheduler, msg);
                task.Start(scheduler);
            }
        }

        private void ReceiveMessageInScheduler(object state)
        {
            try
            {
                _messageScheduler.ReceiveMessage(this, (Message)state);
            }
            finally
            {
                this.DecrementEnqueuedOnDispatcherCount();
            }
        }
    }

    internal static class StreamResourceTestControl
    {
        internal static bool TestOnlySuppressStreamCleanupOnDeactivate;
    }
}
