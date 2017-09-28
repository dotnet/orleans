using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.CodeGeneration;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.ConsistentRing;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Scheduler;
using Orleans.Serialization;
using Orleans.Storage;
using Orleans.Streams;
using Orleans.Transactions;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime
{
    /// <summary>
    /// Internal class for system grains to get access to runtime object
    /// </summary>
    internal class InsideRuntimeClient : ISiloRuntimeClient
    {
        private readonly ILogger logger;
        private readonly Logger callbackDataLogger;
        private readonly ILogger timerLogger;
        private readonly ILogger invokeExceptionLogger;
        private readonly ILoggerFactory loggerFactory;
        private readonly List<IDisposable> disposables;
        private readonly ConcurrentDictionary<CorrelationId, CallbackData> callbacks;
        private readonly Func<Message, bool> tryResendMessage;
        private readonly Action<Message> unregisterCallback;

        private ILocalGrainDirectory directory;
        private Catalog catalog;
        private Dispatcher dispatcher;

        private readonly InterfaceToImplementationMappingCache interfaceToImplementationMapping = new InterfaceToImplementationMappingCache();
        public TimeSpan ResponseTimeout { get; private set; }
        private readonly GrainTypeManager typeManager;
        private readonly MessageFactory messageFactory;
        private readonly List<IGrainCallFilter> siloInterceptors;
        private readonly Lazy<ITransactionAgent> transactionAgent;
        private IGrainReferenceRuntime grainReferenceRuntime;
        
        public InsideRuntimeClient(
            ILocalSiloDetails siloDetails,
            ClusterConfiguration config,
            GrainTypeManager typeManager,
            TypeMetadataCache typeMetadataCache,
            OrleansTaskScheduler scheduler,
            IServiceProvider serviceProvider,
            SerializationManager serializationManager,
            MessageFactory messageFactory,
            IEnumerable<IGrainCallFilter> registeredInterceptors,
            Factory<ITransactionAgent> transactionAgent,
            ILoggerFactory loggerFactory)
        {
            this.ServiceProvider = serviceProvider;
            this.SerializationManager = serializationManager;
            MySilo = siloDetails.SiloAddress;
            disposables = new List<IDisposable>();
            callbacks = new ConcurrentDictionary<CorrelationId, CallbackData>();
            Config = config;
            config.OnConfigChange("Globals/Message", () => ResponseTimeout = Config.Globals.ResponseTimeout);
            this.typeManager = typeManager;
            this.messageFactory = messageFactory;
            this.transactionAgent = new Lazy<ITransactionAgent>(() => transactionAgent());
            this.Scheduler = scheduler;
            this.ConcreteGrainFactory = new GrainFactory(this, typeMetadataCache);
            tryResendMessage = msg => this.Dispatcher.TryResendMessage(msg);
            unregisterCallback = msg => UnRegisterCallback(msg.Id);
            this.siloInterceptors = new List<IGrainCallFilter>(registeredInterceptors);
            this.logger = loggerFactory.CreateLogger<InsideRuntimeClient>();
            this.invokeExceptionLogger =loggerFactory.CreateLogger($"{typeof(Grain).FullName}.InvokeException");
            this.loggerFactory = loggerFactory;
            this.callbackDataLogger = new LoggerWrapper<CallbackData>(loggerFactory);
            this.timerLogger = loggerFactory.CreateLogger<SafeTimer>();
        }
        
        public IServiceProvider ServiceProvider { get; }

        /// <inheritdoc />
        public ClientInvokeCallback ClientInvokeCallback { get; set; }

        public IStreamProviderManager CurrentStreamProviderManager { get; internal set; }

        public IStreamProviderRuntime CurrentStreamProviderRuntime { get; internal set; }

        public OrleansTaskScheduler Scheduler { get; }

        public IInternalGrainFactory InternalGrainFactory => this.ConcreteGrainFactory;

        private SiloAddress MySilo { get; }

        private ClusterConfiguration Config { get; }

        public GrainFactory ConcreteGrainFactory { get; }

        public SerializationManager SerializationManager { get; }

        private Catalog Catalog => this.catalog ?? (this.catalog = this.ServiceProvider.GetRequiredService<Catalog>());

        private ILocalGrainDirectory Directory
            => this.directory ?? (this.directory = this.ServiceProvider.GetRequiredService<ILocalGrainDirectory>());

        private Dispatcher Dispatcher => this.dispatcher ?? (this.dispatcher = this.ServiceProvider.GetRequiredService<Dispatcher>());

        #region Implementation of IRuntimeClient

        public IGrainReferenceRuntime GrainReferenceRuntime => this.grainReferenceRuntime ?? (this.grainReferenceRuntime = this.ServiceProvider.GetRequiredService<IGrainReferenceRuntime>());

        public void SendRequest(
            GrainReference target,
            InvokeMethodRequest request,
            TaskCompletionSource<object> context,
            Action<Message, TaskCompletionSource<object>> callback,
            string debugContext,
            InvokeMethodOptions options,
            string genericArguments = null)
        {
            var message = this.messageFactory.CreateMessage(request, options);
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

            if (message.IsExpirableMessage(Config.Globals.DropExpiredMessages))
                message.TimeToLive = ResponseTimeout;

            if (!oneWay)
            {
                var callbackData = new CallbackData(
                    callback,
                    tryResendMessage,
                    context,
                    message,
                    unregisterCallback,
                    Config.Globals,
                    this.callbackDataLogger,
                    this.timerLogger);
                callbacks.TryAdd(message.Id, callbackData);
                callbackData.StartTimer(ResponseTimeout);
            }

            if (targetGrainId.IsSystemTarget)
            {
                // Messages to system targets bypass the task system and get sent "in-line"
                this.Dispatcher.TransportMessage(message);
            }
            else
            {
                this.Dispatcher.SendMessage(message, sendingActivation);
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

            this.Dispatcher.SendResponse(request, response);
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
                        this.Directory.InvalidateCacheEntry(address, message.IsReturnedFromRemoteCluster);
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

        public async Task Invoke(IAddressable target, IInvokable invokable, Message message)
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
                    UpdateDeadlockInfoInRequestContext(new RequestInvocationHistory(message.TargetGrain, message.TargetActivation, message.DebugContext));
                    // RequestContext is automatically saved in the msg upon send and propagated to the next hop
                    // in RuntimeClient.CreateMessage -> RequestContext.ExportToMessage(message);
                }

                bool startNewTransaction = false;
                TransactionInfo transactionInfo = message.TransactionInfo;

                if (message.IsTransactionRequired && transactionInfo == null)
                {
                    // TODO: this should be a configurable parameter
                    var transactionTimeout = Debugger.IsAttached ? TimeSpan.FromMinutes(30) : TimeSpan.FromSeconds(10);

                    // Start a new transaction
                    transactionInfo = await this.transactionAgent.Value.StartTransaction(message.IsReadOnly, transactionTimeout);
                    startNewTransaction = true;
                }

                if (transactionInfo != null)
                {
                    TransactionContext.SetTransactionInfo(transactionInfo);
                }

                object resultObject;
                try
                {
                    var request = (InvokeMethodRequest) message.GetDeserializedBody(this.SerializationManager);
                    if (request.Arguments != null)
                    {
                        CancellationSourcesExtension.RegisterCancellationTokens(target, request, this.loggerFactory, logger, this);
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

#pragma warning disable 618
                    var invokeInterceptor = this.CurrentStreamProviderRuntime?.GetInvokeInterceptor();
#pragma warning restore 618
                    var requestInvoker = new GrainMethodInvoker(target, request, invoker, siloInterceptors, interfaceToImplementationMapping, invokeInterceptor);
                    await requestInvoker.Invoke();
                    resultObject = requestInvoker.Result;
                }
                catch (Exception exc1)
                {
                    if (invokeExceptionLogger.IsEnabled(LogLevel.Debug) || message.Direction == Message.Directions.OneWay)
                    {
                        invokeExceptionLogger.Warn(ErrorCode.GrainInvokeException,
                            "Exception during Grain method call of message: " + message, exc1);
                    }

                    transactionInfo = TransactionContext.GetTransactionInfo();
                    if (transactionInfo != null)
                    {
                        // Must abort the transaction on exceptions
                        transactionInfo.IsAborted = true;
                        if (startNewTransaction)
                        {
                            var abortException = (exc1 as OrleansTransactionAbortedException) ?? 
                                new OrleansTransactionAbortedException(transactionInfo.TransactionId, exc1);
                            this.transactionAgent.Value.Abort(transactionInfo, abortException);
                            exc1 = abortException;
                        }
                    }

                    // If a grain allowed an inconsistent state exception to escape and the exception originated from
                    // this activation, then deactivate it.
                    var ise = exc1 as InconsistentStateException;
                    if (ise != null && ise.IsSourceActivation)
                    {
                        // Mark the exception so that it doesn't deactivate any other activations.
                        ise.IsSourceActivation = false;

                        var activation = (target as Grain)?.Data;
                        if (activation != null)
                        {
                            invokeExceptionLogger.Info($"Deactivating {activation} due to inconsistent state.");
                            this.DeactivateOnIdle(activation.ActivationId);
                        }
                    }

                    if (message.Direction != Message.Directions.OneWay)
                    {
                        SafeSendExceptionResponse(message, exc1);
                    }
                    return;
                }

                transactionInfo = TransactionContext.GetTransactionInfo();
                if (transactionInfo != null && transactionInfo.ReconcilePending() > 0)
                {
                    var abortException = new OrleansOrphanCallException(transactionInfo.TransactionId, transactionInfo.PendingCalls);
                    // Can't exit before the transaction completes.
                    TransactionContext.GetTransactionInfo().IsAborted = true;
                    if (startNewTransaction)
                    {
                        this.transactionAgent.Value.Abort(TransactionContext.GetTransactionInfo(), abortException);
                    }
 

                    if (message.Direction != Message.Directions.OneWay)
                    {
                        SafeSendExceptionResponse(message, abortException);
                    }

                    return;
                }

                if (startNewTransaction)
                {
                    // This request started the transaction, so we try to commit before returning.
                    await this.transactionAgent.Value.Commit(transactionInfo);
                }

                if (message.Direction == Message.Directions.OneWay) return;

                SafeSendResponse(message, resultObject);
            }
            catch (Exception exc2)
            {
                logger.Warn(ErrorCode.Runtime_Error_100329, "Exception during Invoke of message: " + message, exc2);
                if (message.Direction != Message.Directions.OneWay)
                    SafeSendExceptionResponse(message, exc2);

                if (exc2 is OrleansTransactionInDoubtException)
                {
                    // TODO: log an error message?
                }
                else if (TransactionContext.GetTransactionInfo() != null)
                {
                    // Must abort the transaction on exceptions
                    TransactionContext.GetTransactionInfo().IsAborted = true;
                    var abortException = (exc2 as OrleansTransactionAbortedException) ?? 
                        new OrleansTransactionAbortedException(TransactionContext.GetTransactionInfo().TransactionId, exc2);
                    this.transactionAgent.Value.Abort(TransactionContext.GetTransactionInfo(), abortException);
                }
            }
            finally
            {
                TransactionContext.Clear();
            }
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
                    if (logger.IsEnabled(LogLevel.Trace)) logger.Trace(ErrorCode.Dispatcher_NoCallbackForRejectionResp, "No callback for rejection response message: {0}", message);
                    this.Dispatcher.Transport.SendMessage(message);
                    return;
                }

                if (logger.IsEnabled(LogLevel.Debug)) logger.Debug(ErrorCode.Dispatcher_HandleMsg, "HandleMessage {0}", message);
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
                            this.Directory.InvalidateCacheEntry(message.SendingAddress);
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
                if (message.TransactionInfo != null)
                {
                    // NOTE: Not clear if thread-safe, revise
                    callbackData.TransactionInfo.Join(message.TransactionInfo);
                }
                // IMPORTANT: we do not schedule the response callback via the scheduler, since the only thing it does
                // is to resolve/break the resolver. The continuations/waits that are based on this resolution will be scheduled as work items. 
                callbackData.DoCallback(message);
            }
            else
            {
                if (logger.IsEnabled(LogLevel.Debug)) logger.Debug(ErrorCode.Dispatcher_NoCallbackForResp,
                    "No callback for response message: " + message);
            }
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
            var invoker = TryGetExtensionInvoker(this.typeManager, handler.GetType());
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

        internal static IGrainExtensionMethodInvoker TryGetExtensionInvoker(GrainTypeManager typeManager, Type handlerType)
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
