using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.ConsistentRing;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Scheduler;
using Orleans.Serialization;
using Orleans.Streams;
using Orleans.Timers;

namespace Orleans.Runtime
{
    /// <summary>
    /// Internal class for system grains to get access to runtime object
    /// </summary>
    internal class InsideRuntimeClient : ISiloRuntimeClient
    {
        private static readonly Logger logger = LogManager.GetLogger("InsideRuntimeClient", LoggerType.Runtime);
        private static readonly Logger invokeExceptionLogger = LogManager.GetLogger("Grain.InvokeException", LoggerType.Application);
        private static readonly Logger appLogger = LogManager.GetLogger("Application", LoggerType.Application);

        private readonly Dispatcher dispatcher;
        private readonly ILocalGrainDirectory directory;
        private readonly List<IDisposable> disposables;
        private readonly ConcurrentDictionary<CorrelationId, CallbackData> callbacks;
        private readonly Func<Message, bool> tryResendMessage;
        private readonly Action<Message> unregisterCallback;

        private readonly InterceptedMethodInvokerCache interceptedMethodInvokerCache = new InterceptedMethodInvokerCache();
        public TimeSpan ResponseTimeout { get; private set; }
        private readonly GrainTypeManager typeManager;

        internal readonly IConsistentRingProvider ConsistentRingProvider;

        public InsideRuntimeClient(
            Dispatcher dispatcher,
            Catalog catalog,
            ILocalGrainDirectory directory,
            ClusterConfiguration config,
            IConsistentRingProvider ring,
            GrainTypeManager typeManager,
            TypeMetadataCache typeMetadataCache,
            OrleansTaskScheduler scheduler)
        {
            this.dispatcher = dispatcher;
            MySilo = catalog.LocalSilo;
            this.directory = directory;
            ConsistentRingProvider = ring;
            Catalog = catalog;
            disposables = new List<IDisposable>();
            callbacks = new ConcurrentDictionary<CorrelationId, CallbackData>();
            Config = config;
            config.OnConfigChange("Globals/Message", () => ResponseTimeout = Config.Globals.ResponseTimeout);
            RuntimeClient.Current = this;
            this.typeManager = typeManager;
            this.Scheduler = scheduler;
            this.ConcreteGrainFactory = new GrainFactory(this, typeMetadataCache);
            tryResendMessage = TryResendMessage;
            unregisterCallback = msg => UnRegisterCallback(msg.Id);
            RuntimeClient.Current = this;
        }

        public static InsideRuntimeClient Current { get { return (InsideRuntimeClient)RuntimeClient.Current; } }

        public IStreamProviderManager CurrentStreamProviderManager { get; internal set; }

        public IStreamProviderRuntime CurrentStreamProviderRuntime { get; internal set; }

        public Catalog Catalog { get; private set; }

        public SiloAddress MySilo { get; private set; }

        public ClusterConfiguration Config { get; private set; }

        public OrleansTaskScheduler Scheduler { get; }

        public IGrainFactory GrainFactory => this.ConcreteGrainFactory;

        public IInternalGrainFactory InternalGrainFactory => this.ConcreteGrainFactory;

        public GrainFactory ConcreteGrainFactory { get; private set; }


        #region Implementation of IRuntimeClient

        public void SendRequest(
            GrainReference target,
            InvokeMethodRequest request,
            TaskCompletionSource<object> context,
            Action<Message, TaskCompletionSource<object>> callback,
            string debugContext,
            InvokeMethodOptions options,
            string genericArguments = null)
        {
            var message = Message.CreateMessage(request, options);
            SendRequestMessage(target, message, context, callback, debugContext, options, genericArguments);
        }

        private void SendRequestMessage(
            GrainReference target,
            Message message,
            TaskCompletionSource<object> context,
            Action<Message, TaskCompletionSource<object>> callback,
            string debugContext,
            InvokeMethodOptions options,
            string genericArguments = null)
        {
            // fill in sender
            if (message.SendingSilo == null)
                message.SendingSilo = MySilo;
            if (!String.IsNullOrEmpty(genericArguments))
                message.GenericGrainType = genericArguments;

            SchedulingContext schedulingContext = RuntimeContext.Current != null ?
                RuntimeContext.Current.ActivationContext as SchedulingContext : null;

            ActivationData sendingActivation = null;
            if (schedulingContext == null)
            {
                throw new InvalidOperationException(
                    String.Format("Trying to send a message {0} on a silo not from within grain and not from within system target (RuntimeContext is not set to SchedulingContext) "
                        + "RuntimeContext.Current={1} TaskScheduler.Current={2}",
                        message,
                        RuntimeContext.Current == null ? "null" : RuntimeContext.Current.ToString(),
                        TaskScheduler.Current));
            }
            switch (schedulingContext.ContextType)
            {
                case SchedulingContextType.SystemThread:
                    throw new ArgumentException(
                        String.Format("Trying to send a message {0} on a silo not from within grain and not from within system target (RuntimeContext is of SchedulingContextType.SystemThread type)", message), "context");

                case SchedulingContextType.Activation:
                    message.SendingActivation = schedulingContext.Activation.ActivationId;
                    message.SendingGrain = schedulingContext.Activation.Grain;
                    sendingActivation = schedulingContext.Activation;
                    break;

                case SchedulingContextType.SystemTarget:
                    message.SendingActivation = schedulingContext.SystemTarget.ActivationId;
                    message.SendingGrain = ((ISystemTargetBase)schedulingContext.SystemTarget).GrainId;
                    break;
            }

            // fill in destination
            var targetGrainId = target.GrainId;
            message.TargetGrain = targetGrainId;
            if (targetGrainId.IsSystemTarget)
            {
                SiloAddress targetSilo = (target.SystemTargetSilo ?? MySilo);
                message.TargetSilo = targetSilo;
                message.TargetActivation = ActivationId.GetSystemActivation(targetGrainId, targetSilo);
                message.Category = targetGrainId.Equals(Constants.MembershipOracleId) ?
                    Message.Categories.Ping : Message.Categories.System;
            }
            if (target.IsObserverReference)
            {
                message.TargetObserverId = target.ObserverId;
            }

            if (debugContext != null)
                message.DebugContext = debugContext;

            var oneWay = (options & InvokeMethodOptions.OneWay) != 0;
            if (context == null && !oneWay)
                logger.Warn(ErrorCode.IGC_SendRequest_NullContext, "Null context {0}: {1}", message, Utils.GetStackTrace());

            if (message.IsExpirableMessage(Config.Globals))
                message.TimeToLive = ResponseTimeout;

            if (!oneWay)
            {
                var callbackData = new CallbackData(
                    callback,
                    tryResendMessage,
                    context,
                    message,
                    unregisterCallback,
                    Config.Globals);
                callbacks.TryAdd(message.Id, callbackData);
                callbackData.StartTimer(ResponseTimeout);
            }

            if (targetGrainId.IsSystemTarget)
            {
                // Messages to system targets bypass the task system and get sent "in-line"
                dispatcher.TransportMessage(message);
            }
            else
            {
                dispatcher.SendMessage(message, sendingActivation);
            }
        }

        private void SendResponse(Message request, Response response)
        {
            // Don't process messages that have already timed out
            if (request.IsExpired)
            {
                request.DropExpiredMessage(MessagingStatisticsGroup.Phase.Respond);
                return;
            }

            dispatcher.SendResponse(request, response);
        }

        /// <summary>
        /// Reroute a message coming in through a gateway
        /// </summary>
        /// <param name="message"></param>
        internal void RerouteMessage(Message message)
        {
            ResendMessageImpl(message);
        }

        private bool TryResendMessage(Message message)
        {
            if (!message.MayResend(Config.Globals)) return false;

            message.ResendCount = message.ResendCount + 1;
            MessagingProcessingStatisticsGroup.OnIgcMessageResend(message);
            ResendMessageImpl(message);
            return true;
        }

        internal bool TryForwardMessage(Message message, ActivationAddress forwardingAddress)
        {
            if (!message.MayForward(Config.Globals)) return false;

            message.ForwardCount = message.ForwardCount + 1;
            MessagingProcessingStatisticsGroup.OnIgcMessageForwared(message);
            ResendMessageImpl(message, forwardingAddress);
            return true;
        }

        private void ResendMessageImpl(Message message, ActivationAddress forwardingAddress = null)
        {
            if (logger.IsVerbose) logger.Verbose("Resend {0}", message);
            message.TargetHistory = message.GetTargetHistory();

            if (message.TargetGrain.IsSystemTarget)
            {
                dispatcher.SendSystemTargetMessage(message);
            }
            else if (forwardingAddress != null)
            {
                message.TargetAddress = forwardingAddress;
                message.IsNewPlacement = false;
                dispatcher.Transport.SendMessage(message);
            }
            else
            {
                message.TargetActivation = null;
                message.TargetSilo = null;
                message.ClearTargetAddress();
                dispatcher.SendMessage(message);
            }
        }

        /// <summary>
        /// UnRegister a callback.
        /// </summary>
        /// <param name="id"></param>
        private void UnRegisterCallback(CorrelationId id)
        {
            CallbackData ignore;
            callbacks.TryRemove(id, out ignore);
        }

        public void SniffIncomingMessage(Message message)
        {
            try
            {
                if (message.CacheInvalidationHeader != null)
                {
                    foreach (ActivationAddress address in message.CacheInvalidationHeader)
                    {
                        directory.InvalidateCacheEntry(address, message.IsReturnedFromRemoteCluster);
                    }
                }

#if false
                //// 1:
                //// Also record sending activation address for responses only in the cache.
                //// We don't record sending addresses for requests, since it is not clear that this silo ever wants to send messages to the grain sending this request.
                //// However, it is sure that this silo does send messages to the sender of a reply. 
                //// In most cases it will already have its address cached, unless it had a wrong outdated address cached and now this is a fresher address.
                //// It is anyway always safe to cache the replier address.
                //// 2: 
                //// after further thought decided not to do it.
                //// It seems to better not bother caching the sender of a response at all, 
                //// and instead to take a very occasional hit of a full remote look-up instead of this small but non-zero hit on every response.
                //if (message.Direction.Equals(Message.Directions.Response) && message.Result.Equals(Message.ResponseTypes.Success))
                //{
                //    ActivationAddress sender = message.SendingAddress;
                //    // just make sure address we are about to cache is OK and cachable.
                //    if (sender.IsComplete && !sender.Grain.IsClient && !sender.Grain.IsSystemTargetType && !sender.Activation.IsSystemTargetType)
                //    {
                //        directory.AddCacheEntry(sender);
                //    }
                //}
#endif

            }
            catch (Exception exc)
            {
                logger.Warn(ErrorCode.IGC_SniffIncomingMessage_Exc, "SniffIncomingMessage has thrown exception. Ignoring.", exc);
            }
        }

        internal async Task Invoke(IAddressable target, IInvokable invokable, Message message)
        {
            try
            {
                // Don't process messages that have already timed out
                if (message.IsExpired)
                {
                    message.DropExpiredMessage(MessagingStatisticsGroup.Phase.Invoke);
                    return;
                }

                RequestContext.Import(message.RequestContextData);
                if (Config.Globals.PerformDeadlockDetection && !message.TargetGrain.IsSystemTarget)
                {
                    UpdateDeadlockInfoInRequestContext(new RequestInvocationHistory(message));
                    // RequestContext is automatically saved in the msg upon send and propagated to the next hop
                    // in RuntimeClient.CreateMessage -> RequestContext.ExportToMessage(message);
                }

                object resultObject;
                try
                {
                    var request = (InvokeMethodRequest)message.BodyObject;
                    if (request.Arguments != null)
                    {
                        CancellationSourcesExtension.RegisterCancellationTokens(target, request, logger, this);
                    }

                    var invoker = invokable.GetInvoker(typeManager, request.InterfaceId, message.GenericGrainType);

                    if (invoker is IGrainExtensionMethodInvoker
                        && !(target is IGrainExtension))
                    {
                        // We are trying the invoke a grain extension method on a grain 
                        // -- most likely reason is that the dynamic extension is not installed for this grain
                        // So throw a specific exception here rather than a general InvalidCastException
                        var error = String.Format(
                            "Extension not installed on grain {0} attempting to invoke type {1} from invokable {2}",
                            target.GetType().FullName, invoker.GetType().FullName, invokable.GetType().FullName);
                        var exc = new GrainExtensionNotInstalledException(error);
                        string extraDebugInfo = null;
#if DEBUG
                        extraDebugInfo = Utils.GetStackTrace();
#endif
                        logger.Warn(ErrorCode.Stream_ExtensionNotInstalled,
                            string.Format("{0} for message {1} {2}", error, message, extraDebugInfo), exc);

                        throw exc;
                    }

                    resultObject = await InvokeWithInterceptors(target, request, invoker);
                }
                catch (Exception exc1)
                {
                    if (invokeExceptionLogger.IsVerbose || message.Direction == Message.Directions.OneWay)
                    {
                        invokeExceptionLogger.Warn(ErrorCode.GrainInvokeException,
                            "Exception during Grain method call of message: " + message, exc1);
                    }
                    if (message.Direction != Message.Directions.OneWay)
                    {
                        SafeSendExceptionResponse(message, exc1);
                    }
                    return;
                }

                if (message.Direction == Message.Directions.OneWay) return;

                SafeSendResponse(message, resultObject);
            }
            catch (Exception exc2)
            {
                logger.Warn(ErrorCode.Runtime_Error_100329, "Exception during Invoke of message: " + message, exc2);
                if (message.Direction != Message.Directions.OneWay)
                    SafeSendExceptionResponse(message, exc2);
            }
        }

        private Task<object> InvokeWithInterceptors(IAddressable target, InvokeMethodRequest request, IGrainMethodInvoker invoker)
        {
            // If the target has a grain-level interceptor or there is a silo-level interceptor, intercept the
            // call.
            var siloWideInterceptor = this.CurrentStreamProviderRuntime.GetInvokeInterceptor();
            var grainWithInterceptor = target as IGrainInvokeInterceptor;

            // Silo-wide interceptors do not operate on system targets.
            var hasSiloWideInterceptor = siloWideInterceptor != null && target is IGrain;
            var hasGrainLevelInterceptor = grainWithInterceptor != null;

            if (!hasGrainLevelInterceptor && !hasSiloWideInterceptor)
            {
                // The call is not intercepted at either the silo or the grain level, so call the invoker
                // directly.
                return invoker.Invoke(target, request);
            }

            // If the request is intended for an extension object, use that as the implementation type, otherwise use
            // the target object.
            Type implementationType;
            var extensionMap = invoker as IGrainExtensionMap;
            IGrainExtension extension;
            if (extensionMap != null && extensionMap.TryGetExtension(request.InterfaceId, out extension))
            {
                implementationType = extension.GetType();
            }
            else
            {
                implementationType = target.GetType();
            }

            // Get an invoker which delegates to the grain's IGrainInvocationInterceptor implementation.
            // If the grain does not implement IGrainInvocationInterceptor, then the invoker simply delegates
            // calls to the provided invoker.
            var interceptedMethodInvoker = interceptedMethodInvokerCache.GetOrCreate(
                implementationType,
                request.InterfaceId,
                invoker);
            var methodInfo = interceptedMethodInvoker.GetMethodInfo(request.MethodId);
            if (hasSiloWideInterceptor)
            {
                // There is a silo-level interceptor and possibly a grain-level interceptor.
                // As a minor optimization, only pass the intercepted invoker if there is a grain-level
                // interceptor.
                return siloWideInterceptor(
                    methodInfo,
                    request,
                    (IGrain)target,
                    hasGrainLevelInterceptor ? interceptedMethodInvoker : invoker);
            }

            // The grain has an invoke method, but there is no silo-wide interceptor.
            return grainWithInterceptor.Invoke(methodInfo, request, invoker);
        }

        private void SafeSendResponse(Message message, object resultObject)
        {
            try
            {
                SendResponse(message, new Response(SerializationManager.DeepCopy(resultObject)));
            }
            catch (Exception exc)
            {
                logger.Warn(ErrorCode.IGC_SendResponseFailed,
                    "Exception trying to send a response: " + exc.Message, exc);
                SendResponse(message, Response.ExceptionResponse(exc));
            }
        }

        private static readonly Lazy<Func<Exception, Exception>> prepForRemotingLazy =
            new Lazy<Func<Exception, Exception>>(CreateExceptionPrepForRemotingMethod);


        private static Func<Exception, Exception> CreateExceptionPrepForRemotingMethod()
        {
            var methodInfo = typeof(Exception).GetMethod(
                "PrepForRemoting",
                BindingFlags.Instance | BindingFlags.NonPublic);

            //This was added to avoid failure on .Net Core since Remoting APIs aren't available there.
            if (methodInfo == null)
                return exc => exc;

            var method = new DynamicMethod(
                "PrepForRemoting",
                typeof(Exception),
                new[] { typeof(Exception) },
                typeof(SerializationManager).GetTypeInfo().Module,
                true);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, methodInfo);
            il.Emit(OpCodes.Ret);
            return (Func<Exception, Exception>)method.CreateDelegate(typeof(Func<Exception, Exception>));
        }

        private static Exception PrepareForRemoting(Exception exception)
        {
            // Call the Exception.PrepForRemoting internal method, which preserves the original stack when the exception
            // is rethrown at the remote site (and appends the call site stacktrace). If this is not done, then when the
            // exception is rethrown the original stacktrace is entire replaced.
            // Note: another commonly used approach since .NET 4.5 is to use ExceptionDispatchInfo.Capture(ex).Throw()
            // but that involves rethrowing the exception in-place, which is not what we want here, but could in theory
            // be done at the receiving end with some rework (could be tackled when we reopen #875 Avoid unnecessary use of TCS).
            prepForRemotingLazy.Value.Invoke(exception);
            return exception;
        }

        private void SafeSendExceptionResponse(Message message, Exception ex)
        {
            try
            {
                var copiedException = PrepareForRemoting((Exception)SerializationManager.DeepCopy(ex));
                SendResponse(message, Response.ExceptionResponse(copiedException));
            }
            catch (Exception exc1)
            {
                try
                {
                    logger.Warn(ErrorCode.IGC_SendExceptionResponseFailed,
                        "Exception trying to send an exception response: " + exc1.Message, exc1);
                    SendResponse(message, Response.ExceptionResponse(exc1));
                }
                catch (Exception exc2)
                {
                    logger.Warn(ErrorCode.IGC_UnhandledExceptionInInvoke,
                        "Exception trying to send an exception. Ignoring and not trying to send again. Exc: " + exc2.Message, exc2);
                }
            }
        }

        // assumes deadlock information was already loaded into RequestContext from the message
        private static void UpdateDeadlockInfoInRequestContext(RequestInvocationHistory thisInvocation)
        {
            IList prevChain;
            object obj = RequestContext.Get(RequestContext.CALL_CHAIN_REQUEST_CONTEXT_HEADER);
            if (obj != null)
            {
                prevChain = ((IList)obj);
            }
            else
            {
                prevChain = new List<RequestInvocationHistory>();
                RequestContext.Set(RequestContext.CALL_CHAIN_REQUEST_CONTEXT_HEADER, prevChain);
            }
            // append this call to the end of the call chain. Update in place.
            prevChain.Add(thisInvocation);
        }

        public void ReceiveResponse(Message message)
        {
            if (message.Result == Message.ResponseTypes.Rejection)
            {
                if (!message.TargetSilo.Matches(this.CurrentSilo))
                {
                    // gatewayed message - gateway back to sender
                    if (logger.IsVerbose2) logger.Verbose2(ErrorCode.Dispatcher_NoCallbackForRejectionResp, "No callback for rejection response message: {0}", message);
                    dispatcher.Transport.SendMessage(message);
                    return;
                }

                if (logger.IsVerbose) logger.Verbose(ErrorCode.Dispatcher_HandleMsg, "HandleMessage {0}", message);
                switch (message.RejectionType)
                {
                    case Message.RejectionTypes.DuplicateRequest:
                        // try to remove from callbackData, just in case it is still there.
                        break;
                    case Message.RejectionTypes.Overloaded:
                        break;

                    case Message.RejectionTypes.Unrecoverable:
                    // fall through & reroute
                    case Message.RejectionTypes.Transient:
                        if (message.CacheInvalidationHeader == null)
                        {
                            // Remove from local directory cache. Note that SendingGrain is the original target, since message is the rejection response.
                            // If CacheMgmtHeader is present, we already did this. Otherwise, we left this code for backward compatability. 
                            // It should be retired as we move to use CacheMgmtHeader in all relevant places.
                            directory.InvalidateCacheEntry(message.SendingAddress);
                        }
                        break;

                    default:
                        logger.Error(ErrorCode.Dispatcher_InvalidEnum_RejectionType,
                            "Missing enum in switch: " + message.RejectionType);
                        break;
                }
            }

            CallbackData callbackData;
            bool found = callbacks.TryGetValue(message.Id, out callbackData);
            if (found)
            {
                // IMPORTANT: we do not schedule the response callback via the scheduler, since the only thing it does
                // is to resolve/break the resolver. The continuations/waits that are based on this resolution will be scheduled as work items. 
                callbackData.DoCallback(message);
            }
            else
            {
                if (logger.IsVerbose) logger.Verbose(ErrorCode.Dispatcher_NoCallbackForResp,
                    "No callback for response message: " + message);
            }
        }
        public Logger AppLogger
        {
            get { return appLogger; }
        }

        public string CurrentActivationIdentity
        {
            get
            {
                var currentActivation = this.GetCurrentActivationData();
                return currentActivation.Address.ToString();
            }
        }

        public IActivationData CurrentActivationData
        {
            get
            {
                if (RuntimeContext.Current == null) return null;

                SchedulingContext context = RuntimeContext.Current.ActivationContext as SchedulingContext;
                if (context != null && context.Activation != null)
                {
                    return context.Activation;
                }
                return null;
            }
        }

        public SiloAddress CurrentSilo
        {
            get { return MySilo; }
        }

        public async Task ExecAsync(Func<Task> asyncFunction, ISchedulingContext context, string activityName)
        {
            // Schedule call back to grain context
            await this.Scheduler.QueueNamedTask(asyncFunction, context, activityName);
        }

        public void Reset(bool cleanup)
        {
            throw new InvalidOperationException();
        }

        public TimeSpan GetResponseTimeout()
        {
            return ResponseTimeout;
        }

        public void SetResponseTimeout(TimeSpan timeout)
        {
            ResponseTimeout = timeout;
        }

        public GrainReference CreateObjectReference(IAddressable obj, IGrainMethodInvoker invoker)
        {
            throw new InvalidOperationException("Cannot create a local object reference from a grain.");
        }

        public void DeleteObjectReference(IAddressable obj)
        {
            throw new InvalidOperationException("Cannot delete a local object reference from a grain.");
        }

        public void DeactivateOnIdle(ActivationId id)
        {
            ActivationData data;
            if (!Catalog.TryGetActivationData(id, out data)) return; // already gone

            data.ResetKeepAliveRequest(); // DeactivateOnIdle method would undo / override any current “keep alive” setting, making this grain immideately avaliable for deactivation.
            Catalog.DeactivateActivationOnIdle(data);
        }

        #endregion

        internal void Stop()
        {
            lock (disposables)
            {
                foreach (var disposable in disposables)
                {
                    try
                    {
                        disposable.Dispose();
                    }
                    catch (Exception e)
                    {
                        logger.Warn(ErrorCode.IGC_DisposeError, "Exception while disposing: " + e.Message, e);
                    }
                }
            }
        }

        internal void Start()
        {
            GrainTypeResolver = typeManager.GetTypeCodeMap();
        }

        public IGrainTypeResolver GrainTypeResolver { get; private set; }

        private void CheckValidReminderServiceType(string doingWhat)
        {
            var remType = Config.Globals.ReminderServiceType;
            if (remType.Equals(GlobalConfiguration.ReminderServiceProviderType.NotSpecified) ||
                remType.Equals(GlobalConfiguration.ReminderServiceProviderType.Disabled))
            {
                throw new InvalidOperationException(
                    string.Format("Cannot {0} when ReminderServiceProviderType is {1}",
                    doingWhat, remType));
            }
        }

        private SiloAddress MapGrainReferenceToSiloRing(GrainReference grainRef)
        {
            var hashCode = grainRef.GetUniformHashCode();
            return ConsistentRingProvider.GetPrimaryTargetSilo(hashCode);
        }

        public IGrainMethodInvoker GetInvoker(int interfaceId, string genericGrainType = null)
        {
            return typeManager.GetInvoker(interfaceId, genericGrainType);
        }

        public void BreakOutstandingMessagesToDeadSilo(SiloAddress deadSilo)
        {
            foreach (var callback in callbacks)
            {
                if (deadSilo.Equals(callback.Value.Message.TargetSilo))
                {
                    callback.Value.OnTargetSiloFail();
                }
            }
        }

        public StreamDirectory GetStreamDirectory()
        {
            var currentActivation = GetCurrentActivationData();
            return currentActivation.GetStreamDirectory();
        }

        public Task<Tuple<TExtension, TExtensionInterface>> BindExtension<TExtension, TExtensionInterface>(Func<TExtension> newExtensionFunc)
            where TExtension : IGrainExtension
            where TExtensionInterface : IGrainExtension
        {
            TExtension extension;
            if (!TryGetExtensionHandler(out extension))
            {
                extension = newExtensionFunc();
                if (!TryAddExtension(extension))
                    throw new OrleansException("Failed to register " + typeof(TExtension).Name);
            }

            IAddressable currentGrain = this.CurrentActivationData.GrainInstance;
            var currentTypedGrain = currentGrain.AsReference<TExtensionInterface>();

            return Task.FromResult(Tuple.Create(extension, currentTypedGrain));
        }

        public bool TryAddExtension(IGrainExtension handler)
        {
            var currentActivation = GetCurrentActivationData();
            var invoker = TryGetExtensionInvoker(handler.GetType());
            if (invoker == null)
                throw new InvalidOperationException("Extension method invoker was not generated for an extension interface");

            return currentActivation.TryAddExtension(invoker, handler);
        }

        public void RemoveExtension(IGrainExtension handler)
        {
            var currentActivation = GetCurrentActivationData();
            currentActivation.RemoveExtension(handler);
        }

        public bool TryGetExtensionHandler<TExtension>(out TExtension result) where TExtension : IGrainExtension
        {
            var currentActivation = GetCurrentActivationData();
            IGrainExtension untypedResult;
            if (currentActivation.TryGetExtensionHandler(typeof(TExtension), out untypedResult))
            {
                result = (TExtension)untypedResult;
                return true;
            }

            result = default(TExtension);
            return false;
        }

        private ActivationData GetCurrentActivationData()
        {
            var activationData = this.CurrentActivationData;
            if (activationData == null)
                throw new InvalidOperationException("Attempting to GetCurrentActivationData when not in an activation scope");
            return (ActivationData)activationData;
        }

        private IGrainExtensionMethodInvoker TryGetExtensionInvoker(Type handlerType)
        {
            var interfaces = GrainInterfaceUtils.GetRemoteInterfaces(handlerType).Values;
            if (interfaces.Count != 1)
                throw new InvalidOperationException($"Extension type {handlerType.FullName} implements more than one grain interface.");

            var interfaceId = GrainInterfaceUtils.GetGrainInterfaceId(interfaces.First());
            var invoker = typeManager.GetInvoker(interfaceId);
            if (invoker != null)
                return (IGrainExtensionMethodInvoker)invoker;

            throw new ArgumentException(
                $"Provider extension handler type {handlerType} was not found in the type manager",
                nameof(handlerType));
        }
    }
}
