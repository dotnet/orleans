using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.CodeGeneration;
using Orleans.Configuration;
using Orleans.Core;
using Orleans.GrainDirectory;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Scheduler;
using Orleans.Storage;

namespace Orleans.Runtime
{
    /// <summary>
    /// Maintains additional per-activation state that is required for Orleans internal operations.
    /// MUST lock this object for any concurrent access
    /// Consider: compartmentalize by usage, e.g., using separate interfaces for data for catalog, etc.
    /// </summary>
    internal class ActivationData : IGrainActivationContext, IActivationData, IInvokable, IDisposable
    {
        internal class GrainActivationContextFactory
        {
            public IGrainActivationContext Context { get; set; }
        }

        // This is the maximum amount of time we expect a request to continue processing
        private readonly TimeSpan maxRequestProcessingTime;
        private readonly TimeSpan maxWarningRequestProcessingTime;
        private readonly SiloMessagingOptions messagingOptions;
        public readonly TimeSpan CollectionAgeLimit;
        private readonly ILogger logger;
        private IGrainMethodInvoker lastInvoker;
        private IServiceScope serviceScope;
        private HashSet<IGrainTimer> timers;
        
        public ActivationData(
            ActivationAddress addr,
            string genericArguments,
            PlacementStrategy placedUsing,
            IActivationCollector collector,
            TimeSpan ageLimit,
            IOptions<SiloMessagingOptions> messagingOptions,
            TimeSpan maxWarningRequestProcessingTime,
			TimeSpan maxRequestProcessingTime,
            IRuntimeClient runtimeClient,
            ILoggerFactory loggerFactory)
        {
            if (null == addr) throw new ArgumentNullException(nameof(addr));
            if (null == placedUsing) throw new ArgumentNullException(nameof(placedUsing));
            if (null == collector) throw new ArgumentNullException(nameof(collector));

            logger = loggerFactory.CreateLogger<ActivationData>();
            this.lifecycle = new GrainLifecycle(loggerFactory.CreateLogger<LifecycleSubject>());
            this.maxRequestProcessingTime = maxRequestProcessingTime;
            this.maxWarningRequestProcessingTime = maxWarningRequestProcessingTime;
            this.messagingOptions = messagingOptions.Value;
            ResetKeepAliveRequest();
            Address = addr;
            State = ActivationState.Create;
            PlacedUsing = placedUsing;
            if (!Grain.IsSystemTarget())
            {
                this.collector = collector;
            }

            CollectionAgeLimit = ageLimit;

            GrainReference = GrainReference.FromGrainId(addr.Grain, runtimeClient.GrainReferenceRuntime, genericArguments);
        }

        public Type GrainType => GrainTypeData.Type;

        public IGrainIdentity GrainIdentity => (LegacyGrainId)this.GrainId;

        public IServiceProvider ActivationServices => this.serviceScope.ServiceProvider;

        internal WorkItemGroup WorkItemGroup { get; set; }

        private ExtensionInvoker extensionInvoker;
        internal ExtensionInvoker ExtensionInvoker
        {
            get
            {
                this.lastInvoker = null;
                return this.extensionInvoker ?? (this.extensionInvoker = new ExtensionInvoker());
            }
        }

        public IGrainMethodInvoker GetInvoker(GrainTypeManager typeManager, int interfaceId, string genericGrainType = null)
        {
            // Return previous cached invoker, if applicable
            if (lastInvoker != null && interfaceId == lastInvoker.InterfaceId) // extension invoker returns InterfaceId==0, so this condition will never be true if an extension is installed
                return lastInvoker;

            if (extensionInvoker != null && extensionInvoker.IsExtensionInstalled(interfaceId))
            {
                // Shared invoker for all extensions installed on this grain
                lastInvoker = extensionInvoker;
            }
            else
            {
                // Find the specific invoker for this interface / grain type
                lastInvoker = typeManager.GetInvoker(interfaceId, genericGrainType);
            }

            return lastInvoker;
        }

        public HashSet<ActivationId> RunningRequestsSenders { get; } = new HashSet<ActivationId>();

        internal Type GrainInstanceType => GrainTypeData?.Type;

        internal void SetGrainInstance(Grain grainInstance)
        {
            GrainInstance = grainInstance;
        }

        internal void SetupContext(GrainTypeData typeData, IServiceProvider grainServices)
        {
            this.GrainTypeData = typeData;
            this.Items = new Dictionary<object, object>();
            this.serviceScope = grainServices.CreateScope();

            SetGrainActivationContextInScopedServices(this.ActivationServices, this);

            if (typeData != null)
            {
                var grainType = typeData.Type;

                // Don't ever collect system grains or reminder table grain or memory store grains.
                bool doNotCollect = typeof(IReminderTableGrain).IsAssignableFrom(grainType) || typeof(IMemoryStorageGrain).IsAssignableFrom(grainType);
                if (doNotCollect)
                {
                    this.collector = null;
                }
            }
        }

        private static void SetGrainActivationContextInScopedServices(IServiceProvider sp, IGrainActivationContext context)
        {
            var contextFactory = sp.GetRequiredService<GrainActivationContextFactory>();
            contextFactory.Context = context;
        }
        
        private Streams.StreamDirectory streamDirectory;
        internal Streams.StreamDirectory GetStreamDirectory()
        {
            return streamDirectory ?? (streamDirectory = new Streams.StreamDirectory());
        }

        internal bool IsUsingStreams 
        {
            get { return streamDirectory != null; }
        }

        internal async Task DeactivateStreamResources()
        {
            if (streamDirectory == null) return; // No streams - Nothing to do.
            if (extensionInvoker == null) return; // No installed extensions - Nothing to do.

            if (StreamResourceTestControl.TestOnlySuppressStreamCleanupOnDeactivate)
            {
                logger.Warn(0, "Suppressing cleanup of stream resources during tests for {0}", this);
                return;
            }

            await streamDirectory.Cleanup(true, false);
        }

        public GrainId GrainId
        {
            get { return Grain; }
        }

        public GrainTypeData GrainTypeData { get; private set; }

        public Grain GrainInstance { get; private set; }

        IAddressable IGrainContext.GrainInstance => this.GrainInstance;

        public ActivationId ActivationId { get { return Address.Activation; } }

        public ActivationAddress Address { get; private set; }

        public IServiceProvider ServiceProvider => this.serviceScope?.ServiceProvider;

        public IDictionary<object, object> Items { get; private set; }

        private readonly GrainLifecycle lifecycle;

        public IGrainLifecycle ObservableLifecycle => lifecycle;

        internal ILifecycleObserver Lifecycle => lifecycle;

        public void OnTimerCreated(IGrainTimer timer)
        {
            AddTimer(timer);
        }

        public GrainReference GrainReference { get; }

        public SiloAddress Silo { get { return Address.Silo;  } }

        public GrainId Grain { get { return Address.Grain; } }

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

        // the number of requests that are currently executing on this activation.
        // includes reentrant and non-reentrant requests.
        private int numRunning;

        private DateTime currentRequestStartTime;
        private DateTime becameIdle;
        private DateTime deactivationStartTime;

        public void RecordRunning(Message message, bool isInterleavable)
        {
            // Note: This method is always called while holding lock on this activation, so no need for additional locks here

            numRunning++;
            if (message.Direction != Message.Directions.OneWay 
                && !(message.SendingActivation is null)
                && !message.SendingGrain.IsClient())
            {
                RunningRequestsSenders.Add(message.SendingActivation);
            }

            if (this.Blocking != null || isInterleavable) return;

            // This logic only works for non-reentrant activations
            // Consider: Handle long request detection for reentrant activations.
            this.Blocking = message;
            currentRequestStartTime = DateTime.UtcNow;
        }

        public void ResetRunning(Message message)
        {
            // Note: This method is always called while holding lock on this activation, so no need for additional locks here
            numRunning--;
            RunningRequestsSenders.Remove(message.SendingActivation);
            if (numRunning == 0)
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
                            $"Current activation {ToDetailedString()} marked as Deactivating for {deactivatingTime}. Trying  to enqueue {message}.");
                        return EnqueueMessageResult.ErrorStuckActivation;
                    }
                }
                if (this.Blocking != null)
                {
                    var currentRequestActiveTime = DateTime.UtcNow - currentRequestStartTime;
                    if (currentRequestActiveTime > maxRequestProcessingTime)
                    {
                        logger.Error(ErrorCode.Dispatcher_StuckActivation,
                            $"Current request has been active for {currentRequestActiveTime} for activation {ToDetailedString()}. Currently executing {this.Blocking}.  Trying  to enqueue {message}.");
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

                waiting = waiting ?? new List<Message>();
                waiting.Add(message);
                return EnqueueMessageResult.Success;
            }
        }

        /// <summary>
        /// Check whether this activation is overloaded. 
        /// Returns LimitExceededException if overloaded, otherwise <c>null</c>c>
        /// </summary>
        /// <param name="log">Logger to use for reporting any overflow condition</param>
        /// <returns>Returns LimitExceededException if overloaded, otherwise <c>null</c>c></returns>
        public LimitExceededException CheckOverloaded(ILogger log)
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
                log.Warn(ErrorCode.Catalog_Reject_ActivationTooManyRequests, 
                    String.Format("Overload - {0} enqueued requests for activation {1}, exceeding hard limit rejection threshold of {2}",
                        count, this, maxRequestsHardLimit));

                return new LimitExceededException(limitName, count, maxRequestsHardLimit, this.ToString());
            }
            if (maxRequestsSoftLimit > 0 && count > maxRequestsSoftLimit) // Soft limit
            {
                log.Warn(ErrorCode.Catalog_Warn_ActivationTooManyRequests,
                    String.Format("Hot - {0} enqueued requests for activation {1}, exceeding soft limit warning threshold of {2}",
                        count, this, maxRequestsSoftLimit));
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
                return numRunning > 0 ;
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
            lock (this) // need to lock since dispose can be called on finalizer thread, outside garin context (not single threaded).
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

                if (waiting!=null && waiting.Count > 0)
                {
                    sb.AppendFormat("   Messages queued within ActivationData: {0}", PrintWaitingQueue());
                }
            }
            return sb.ToString();
        }

        public override string ToString()
        {
            return String.Format("[Activation: {0}{1}{2}{3} State={4}]",
                 Silo,
                 Grain,
                 this.ActivationId,
                 GetActivationInfoString(),
                 State);
        }

        internal string ToDetailedString(bool includeExtraDetails = false)
        {
            return
                String.Format(
                    "[Activation: {0}{1}{2}{3} State={4} NonReentrancyQueueSize={5} EnqueuedOnDispatcher={6} InFlightCount={7} NumRunning={8} IdlenessTimeSpan={9} CollectionAgeLimit={10}{11}]",
                    Silo.ToLongString(),
                    Grain.ToString(),
                    this.ActivationId,
                    GetActivationInfoString(),
                    State,                          // 4
                    WaitingCount,                   // 5 NonReentrancyQueueSize
                    EnqueuedOnDispatcherCount,      // 6 EnqueuedOnDispatcher
                    InFlightCount,                  // 7 InFlightCount
                    numRunning,                     // 8 NumRunning
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
                     Grain,
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
            return GrainInstanceType == null ? placement :
                String.Format(" #GrainType={0} Placement={1}", GrainInstanceType.FullName, placement);
        }

        public void Dispose()
        {
            IDisposable disposable = serviceScope;
            if (disposable != null) disposable.Dispose();
            this.serviceScope = null;
        }

        bool IEquatable<IGrainContext>.Equals(IGrainContext other) => ReferenceEquals(this, other);
    }

    internal static class StreamResourceTestControl
    {
        internal static bool TestOnlySuppressStreamCleanupOnDeactivate;
    }
}
