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
        // This class is used for activations that have extension invokers. It keeps a dictionary of 
        // invoker objects to use with the activation, and extend the default invoker
        // defined for the grain class.
        // Note that in all cases we never have more than one copy of an actual invoker;
        // we may have a ExtensionInvoker per activation, in the worst case.
        private class ExtensionInvoker : IGrainMethodInvoker, IGrainExtensionMap
        {
            // Because calls to ExtensionInvoker are allways made within the activation context,
            // we rely on the single-threading guarantee of the runtime and do not protect the map with a lock.
            private Dictionary<int, Tuple<IGrainExtension, IGrainExtensionMethodInvoker>> extensionMap; // key is the extension interface ID
            
            /// <summary>
            /// Try to add an extension for the specific interface ID.
            /// Fail and return false if there is already an extension for that interface ID.
            /// Note that if an extension invoker handles multiple interface IDs, it can only be associated
            /// with one of those IDs when added, and so only conflicts on that one ID will be detected and prevented.
            /// </summary>
            /// <param name="invoker"></param>
            /// <param name="handler"></param>
            /// <returns></returns>
            internal bool TryAddExtension(IGrainExtensionMethodInvoker invoker, IGrainExtension handler)
            {
                if (extensionMap == null)
                {
                    extensionMap = new Dictionary<int, Tuple<IGrainExtension, IGrainExtensionMethodInvoker>>(1);
                }

                if (extensionMap.ContainsKey(invoker.InterfaceId)) return false;

                extensionMap.Add(invoker.InterfaceId, new Tuple<IGrainExtension, IGrainExtensionMethodInvoker>(handler, invoker));
                return true;
            }

            /// <summary>
            /// Removes all extensions for the specified interface id.
            /// Returns true if the chained invoker no longer has any extensions and may be safely retired.
            /// </summary>
            /// <param name="extension"></param>
            /// <returns>true if the chained invoker is now empty, false otherwise</returns>
            public bool Remove(IGrainExtension extension)
            {
                int interfaceId = 0;

                foreach(int iface in extensionMap.Keys)
                    if (extensionMap[iface].Item1 == extension)
                    {
                        interfaceId = iface;
                        break;
                    }

                if (interfaceId == 0) // not found
                    throw new InvalidOperationException(String.Format("Extension {0} is not installed",
                        extension.GetType().FullName));

                extensionMap.Remove(interfaceId);
                return extensionMap.Count == 0;
            }

            public bool TryGetExtensionHandler(Type extensionType, out IGrainExtension result)
            {
                result = null;

                if (extensionMap == null) return false;

                foreach (var ext in extensionMap.Values)
                    if (extensionType == ext.Item1.GetType())
                    {
                        result = ext.Item1;
                        return true;
                    }

                return false;
            }

            /// <summary>
            /// Invokes the appropriate grain or extension method for the request interface ID and method ID.
            /// First each extension invoker is tried; if no extension handles the request, then the base
            /// invoker is used to handle the request.
            /// The base invoker will throw an appropriate exception if the request is not recognized.
            /// </summary>
            /// <param name="grain"></param>
            /// <param name="request"></param>
            /// <returns></returns>
            public Task<object> Invoke(IAddressable grain, InvokeMethodRequest request)
            {
                if (extensionMap == null || !extensionMap.ContainsKey(request.InterfaceId))
                    throw new InvalidOperationException(
                        String.Format("Extension invoker invoked with an unknown inteface ID:{0}.", request.InterfaceId));

                var invoker = extensionMap[request.InterfaceId].Item2;
                var extension = extensionMap[request.InterfaceId].Item1;
                return invoker.Invoke(extension, request);
            }

            public bool IsExtensionInstalled(int interfaceId)
            {
                return extensionMap != null && extensionMap.ContainsKey(interfaceId);
            }

            public int InterfaceId
            {
                get { return 0; } // 0 indicates an extension invoker that may have multiple intefaces inplemented by extensions.
            }

            public ushort InterfaceVersion
            {
                get { return 0; }
            }

            /// <summary>
            /// Gets the extension from this instance if it is available.
            /// </summary>
            /// <param name="interfaceId">The interface id.</param>
            /// <param name="extension">The extension.</param>
            /// <returns>
            /// <see langword="true"/> if the extension is found, <see langword="false"/> otherwise.
            /// </returns>
            public bool TryGetExtension(int interfaceId, out IGrainExtension extension)
            {
                Tuple<IGrainExtension, IGrainExtensionMethodInvoker> value;
                if (extensionMap != null && extensionMap.TryGetValue(interfaceId, out value))
                {
                    extension = value.Item1;
                }
                else
                {
                    extension = null;
                }

                return extension != null;
            }
        }

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
            IMultiClusterRegistrationStrategy registrationStrategy,
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
            RegistrationStrategy = registrationStrategy;
            if (!Grain.IsSystemTarget)
            {
                this.collector = collector;
            }

            CollectionAgeLimit = ageLimit;

            GrainReference = GrainReference.FromGrainId(addr.Grain, runtimeClient.GrainReferenceRuntime, genericArguments, Grain.IsSystemTarget ? addr.Silo : null);
            this.SchedulingContext = new SchedulingContext(this);
        }

        public Type GrainType => GrainTypeData.Type;

        public IGrainIdentity GrainIdentity => this.Identity;

        public IServiceProvider ActivationServices => this.serviceScope.ServiceProvider;

        #region Method invocation

        private ExtensionInvoker extensionInvoker;
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

        internal bool TryAddExtension(IGrainExtensionMethodInvoker invoker, IGrainExtension extension)
        {
            if(extensionInvoker == null)
                extensionInvoker = new ExtensionInvoker();

            return extensionInvoker.TryAddExtension(invoker, extension);
        }

        internal void RemoveExtension(IGrainExtension extension)
        {
            if (extensionInvoker != null)
            {
                if (extensionInvoker.Remove(extension))
                    extensionInvoker = null;
            }
            else
                throw new InvalidOperationException("Grain extensions not installed.");
        }

        internal bool TryGetExtensionHandler(Type extensionType, out IGrainExtension result)
        {
            result = null;
            return extensionInvoker != null && extensionInvoker.TryGetExtensionHandler(extensionType, out result);
        }

        #endregion

        public HashSet<ActivationId> RunningRequestsSenders { get; } = new HashSet<ActivationId>();

        public ISchedulingContext SchedulingContext { get; }

        public string GrainTypeName
        {
            get
            {
                if (GrainInstanceType == null)
                {
                    throw new ArgumentNullException("GrainInstanceType", "GrainInstanceType has not been set.");
                }
                return GrainInstanceType.FullName;
            }
        }

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

        #region IActivationData
        GrainReference IActivationData.GrainReference
        {
            get { return GrainReference; }
        }
        
        public GrainId Identity
        {
            get { return Grain; }
        }

        public GrainTypeData GrainTypeData { get; private set; }

        public Grain GrainInstance { get; private set; }

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

        #endregion

        #region Catalog

        internal readonly GrainReference GrainReference;

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

        #endregion

        #region Dispatcher

        public PlacementStrategy PlacedUsing { get; private set; }

        public IMultiClusterRegistrationStrategy RegistrationStrategy { get; private set; }

        // Currently, the only supported multi-activation grain is one using the StatelessWorkerPlacement strategy.
        internal bool IsStatelessWorker { get { return PlacedUsing is StatelessWorkerPlacement; } }

        // Currently, the only grain type that is not registered in the Grain Directory is StatelessWorker. 
        internal bool IsUsingGrainDirectory { get { return !IsStatelessWorker; } }

        public Message Running { get; private set; }

        // the number of requests that are currently executing on this activation.
        // includes reentrant and non-reentrant requests.
        private int numRunning;

        private DateTime currentRequestStartTime;
        private DateTime becameIdle;
        private DateTime deactivationStartTime;

        public void RecordRunning(Message message)
        {
            // Note: This method is always called while holding lock on this activation, so no need for additional locks here

            numRunning++;
            if (message.Direction != Message.Directions.OneWay 
                && message.SendingActivation != null
                && !message.SendingGrain?.IsClient == true)
            {
                RunningRequestsSenders.Add(message.SendingActivation);
            }

            if (Running != null) return;

            // This logic only works for non-reentrant activations
            // Consider: Handle long request detection for reentrant activations.
            Running = message;
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
            if (Running != null && !message.Equals(Running)) return;

            Running = null;
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

        /// <summary>Increment the number of messages currently in the prcess of being received.</summary>
        public void IncrementEnqueuedOnDispatcherCount() { Interlocked.Increment(ref enqueuedOnDispatcherCount); }

        /// <summary>Decrement the number of messages currently in the prcess of being received.</summary>
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
                if (Running != null)
                {
                    var currentRequestActiveTime = DateTime.UtcNow - currentRequestStartTime;
                    if (currentRequestActiveTime > maxRequestProcessingTime)
                    {
                        logger.Error(ErrorCode.Dispatcher_StuckActivation,
                            $"Current request has been active for {currentRequestActiveTime} for activation {ToDetailedString()}. Currently executing {Running}.  Trying  to enqueue {message}.");
                        return EnqueueMessageResult.ErrorStuckActivation;
                    }
                    // Consider: Handle long request detection for reentrant activations -- this logic only works for non-reentrant activations
                    else if (currentRequestActiveTime > maxWarningRequestProcessingTime)
                    {
                        logger.Warn(ErrorCode.Dispatcher_ExtendedMessageProcessing,
                             "Current request has been active for {0} for activation {1}. Currently executing {2}. Trying  to enqueue {3}.",
                             currentRequestActiveTime, this.ToDetailedString(), Running, message);
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

        #endregion
        
        #region Activation collection

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

        #endregion

        #region In-grain Timers
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
        #endregion

        #region Printing functions

        public string DumpStatus()
        {
            var sb = new StringBuilder();
            lock (this)
            {
                sb.AppendFormat("   {0}", ToDetailedString());
                if (Running != null)
                {
                    sb.AppendFormat("   Processing message: {0}", Running);
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
                 ActivationId,
                 GetActivationInfoString(),
                 State);
        }

        internal string ToDetailedString(bool includeExtraDetails = false)
        {
            return
                String.Format(
                    "[Activation: {0}{1}{2}{3} State={4} NonReentrancyQueueSize={5} EnqueuedOnDispatcher={6} InFlightCount={7} NumRunning={8} IdlenessTimeSpan={9} CollectionAgeLimit={10}{11}]",
                    Silo.ToLongString(),
                    Grain.ToDetailedString(),
                    ActivationId,
                    GetActivationInfoString(),
                    State,                          // 4
                    WaitingCount,                   // 5 NonReentrancyQueueSize
                    EnqueuedOnDispatcherCount,      // 6 EnqueuedOnDispatcher
                    InFlightCount,                  // 7 InFlightCount
                    numRunning,                     // 8 NumRunning
                    GetIdleness(DateTime.UtcNow),   // 9 IdlenessTimeSpan
                    CollectionAgeLimit,             // 10 CollectionAgeLimit
                    (includeExtraDetails && Running != null) ? " CurrentlyExecuting=" + Running : "");  // 11: Running
        }

        public string Name
        {
            get
            {
                return String.Format("[Activation: {0}{1}{2}{3}]",
                     Silo,
                     Grain,
                     ActivationId,
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

        #endregion

        public void Dispose()
        {
            IDisposable disposable = serviceScope;
            if (disposable != null) disposable.Dispose();
            this.serviceScope = null;
        }
    }

    internal static class StreamResourceTestControl
    {
        internal static bool TestOnlySuppressStreamCleanupOnDeactivate;
    }
}
