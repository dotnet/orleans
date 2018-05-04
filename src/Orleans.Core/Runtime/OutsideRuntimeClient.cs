using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.CodeGeneration;
using Orleans.Configuration;
using Orleans.Messaging;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Serialization;
using Orleans.Streams;

namespace Orleans
{
    internal class OutsideRuntimeClient : IRuntimeClient, IDisposable, IClusterConnectionStatusListener
    {
        internal static bool TestOnlyThrowExceptionDuringInit { get; set; }

        private ILogger logger;
        private ILogger callBackDataLogger;
        private ILogger timerLogger;
        private ClientMessagingOptions clientMessagingOptions;

        private readonly ConcurrentDictionary<CorrelationId, CallbackData> callbacks;
        private readonly ConcurrentDictionary<GuidId, LocalObjectData> localObjects;

        private ClientMessageCenter transport;
        private bool listenForMessages;
        private CancellationTokenSource listeningCts;
        private bool firstMessageReceived;
        private bool disposing;

        private ClientProviderRuntime clientProviderRuntime;

        internal ClientStatisticsManager ClientStatistics;
        private GrainId clientId;
        private readonly GrainId handshakeClientId;
        private ThreadTrackingStatistic incomingMessagesThreadTimeTracking;
        private readonly Func<Message, bool> tryResendMessage;
        private readonly Action<Message> unregisterCallback;

        private TimeSpan typeMapRefreshInterval;
        private AsyncTaskSafeTimer typeMapRefreshTimer = null;

        // initTimeout used to be AzureTableDefaultPolicies.TableCreationTimeout, which was 3 min
        private static readonly TimeSpan initTimeout = TimeSpan.FromMinutes(1);

        private static readonly TimeSpan resetTimeout = TimeSpan.FromMinutes(1);

        private const string BARS = "----------";
        
        public IInternalGrainFactory InternalGrainFactory { get; private set; }

        /// <summary>
        /// Response timeout.
        /// </summary>
        private TimeSpan responseTimeout;
        
        private MessageFactory messageFactory;
        private IPAddress localAddress;
        private IGatewayListProvider gatewayListProvider;
        private readonly ILoggerFactory loggerFactory;

        private SerializationManager serializationManager;

        public ActivationAddress CurrentActivationAddress
        {
            get;
            private set;
        }
        
        public string CurrentActivationIdentity
        {
            get { return CurrentActivationAddress.ToString(); }
        }

        internal Task<IList<Uri>> GetGateways() =>
            this.transport.GatewayManager.ListProvider.GetGateways();

        public IStreamProviderRuntime CurrentStreamProviderRuntime
        {
            get { return clientProviderRuntime; }
        }

        public IGrainReferenceRuntime GrainReferenceRuntime { get; private set; }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope",
            Justification = "MessageCenter is IDisposable but cannot call Dispose yet as it lives past the end of this method call.")]
        public OutsideRuntimeClient(
            ILoggerFactory loggerFactory, 
            IOptions<ClientMessagingOptions> clientMessagingOptions,
            IOptions<TypeManagementOptions> typeManagementOptions)
        {
            this.loggerFactory = loggerFactory;
            this.logger = loggerFactory.CreateLogger<OutsideRuntimeClient>();
            this.handshakeClientId = GrainId.NewClientId();
            tryResendMessage = TryResendMessage;
            unregisterCallback = msg => UnRegisterCallback(msg.Id);
            callbacks = new ConcurrentDictionary<CorrelationId, CallbackData>();
            localObjects = new ConcurrentDictionary<GuidId, LocalObjectData>();
            this.callBackDataLogger = loggerFactory.CreateLogger<CallbackData>();
            this.timerLogger = loggerFactory.CreateLogger<SafeTimer>();
            this.clientMessagingOptions = clientMessagingOptions.Value;
            this.typeMapRefreshInterval = typeManagementOptions.Value.TypeMapRefreshInterval;
            this.responseTimeout = clientMessagingOptions.Value.ResponseTimeout;
        }

        internal void ConsumeServices(IServiceProvider services)
        {
            this.ServiceProvider = services;

            var connectionLostHandlers = this.ServiceProvider.GetServices<ConnectionToClusterLostHandler>();
            foreach (var handler in connectionLostHandlers)
            {
                this.ClusterConnectionLost += handler;
            }

            var clientInvokeCallbacks = this.ServiceProvider.GetServices<ClientInvokeCallback>();
            foreach (var handler in clientInvokeCallbacks)
            {
                this.ClientInvokeCallback += handler;
            }

            this.InternalGrainFactory = this.ServiceProvider.GetRequiredService<IInternalGrainFactory>();
            this.ClientStatistics = this.ServiceProvider.GetRequiredService<ClientStatisticsManager>();
            this.serializationManager = this.ServiceProvider.GetRequiredService<SerializationManager>();
            this.messageFactory = this.ServiceProvider.GetService<MessageFactory>();

            this.GrainReferenceRuntime = this.ServiceProvider.GetRequiredService<IGrainReferenceRuntime>();

            var statisticsOptions = this.ServiceProvider.GetRequiredService<IOptions<ClientStatisticsOptions>>().Value;
            StatisticsCollector.Initialize(statisticsOptions.CollectionLevel);

            BufferPool.InitGlobalBufferPool(this.clientMessagingOptions);

            try
            {
                AppDomain.CurrentDomain.DomainUnload += CurrentDomain_DomainUnload;

                clientProviderRuntime = this.ServiceProvider.GetRequiredService<ClientProviderRuntime>();

                this.localAddress = ConfigUtilities.GetLocalIPAddress(this.clientMessagingOptions.PreferredFamily, this.clientMessagingOptions.NetworkInterfaceName);

                // Client init / sign-on message
                logger.Info(ErrorCode.ClientInitializing, string.Format(
                    "{0} Initializing OutsideRuntimeClient on {1} at {2} Client Id = {3} {0}",
                    BARS, Dns.GetHostName(), localAddress, handshakeClientId));
                string startMsg = string.Format("{0} Starting OutsideRuntimeClient with runtime Version='{1}' in AppDomain={2}",
                    BARS, RuntimeVersion.Current, PrintAppDomainDetails());
                logger.Info(ErrorCode.ClientStarting, startMsg);

                if (TestOnlyThrowExceptionDuringInit)
                {
                    throw new InvalidOperationException("TestOnlyThrowExceptionDuringInit");
                }

                this.gatewayListProvider = this.ServiceProvider.GetRequiredService<IGatewayListProvider>();

                if (StatisticsCollector.CollectThreadTimeTrackingStats)
                {
                    incomingMessagesThreadTimeTracking = new ThreadTrackingStatistic("ClientReceiver", this.loggerFactory);
                }
            }
            catch (Exception exc)
            {
                if (logger != null) logger.Error(ErrorCode.Runtime_Error_100319, "OutsideRuntimeClient constructor failed.", exc);
                ConstructorReset();
                throw;
            }
        }

        public IServiceProvider ServiceProvider { get; private set; }

        private async Task StreamingInitialize()
        {
            var implicitSubscriberTable = await transport.GetImplicitStreamSubscriberTable(this.InternalGrainFactory);
            clientProviderRuntime.StreamingInitialize(implicitSubscriberTable);
        }

        public async Task Start(Func<Exception, Task<bool>> retryFilter = null)
        {
            // Deliberately avoid capturing the current synchronization context during startup and execute on the default scheduler.
            // This helps to avoid any issues (such as deadlocks) caused by executing with the client's synchronization context/scheduler.
            await Task.Run(() => this.StartInternal(retryFilter)).ConfigureAwait(false);

            logger.Info(ErrorCode.ProxyClient_StartDone, "{0} Started OutsideRuntimeClient with Global Client ID: {1}", BARS, CurrentActivationAddress.ToString() + ", client GUID ID: " + handshakeClientId);
        }
        
        // used for testing to (carefully!) allow two clients in the same process
        private async Task StartInternal(Func<Exception, Task<bool>> retryFilter)
        {
            // Initialize the gateway list provider, since information from the cluster is required to successfully
            // initialize subsequent services.
            var initializedGatewayProvider = new[] {false};
            await ExecuteWithRetries(async () =>
                {
                    if (!initializedGatewayProvider[0])
                    {
                        await this.gatewayListProvider.InitializeGatewayListProvider();
                        initializedGatewayProvider[0] = true;
                    }

                    var gateways = await this.gatewayListProvider.GetGateways();
                    if (gateways.Count == 0)
                    {
                        var gatewayProviderType = this.gatewayListProvider.GetType().GetParseableName();
                        var err = $"Could not find any gateway in {gatewayProviderType}. Orleans client cannot initialize.";
                        logger.Error(ErrorCode.GatewayManager_NoGateways, err);
                        throw new SiloUnavailableException(err);
                    }
                },
                retryFilter);

            var generation = -SiloAddress.AllocateNewGeneration(); // Client generations are negative
            transport = ActivatorUtilities.CreateInstance<ClientMessageCenter>(this.ServiceProvider, localAddress, generation, handshakeClientId);
            transport.Start();
            CurrentActivationAddress = ActivationAddress.NewActivationAddress(transport.MyAddress, handshakeClientId);

            listeningCts = new CancellationTokenSource();
            var ct = listeningCts.Token;
            listenForMessages = true;

            // Keeping this thread handling it very simple for now. Just queue task on thread pool.
            Task.Run(
                () =>
                {
                    while (listenForMessages && !ct.IsCancellationRequested)
                    {
                        try
                        {
                            RunClientMessagePump(ct);
                        }
                        catch (Exception exc)
                        {
                            logger.Error(ErrorCode.Runtime_Error_100326, "RunClientMessagePump has thrown exception", exc);
                        }
                    }
                },
                ct).Ignore();

            await ExecuteWithRetries(
                async () => this.GrainTypeResolver = await transport.GetGrainTypeResolver(this.InternalGrainFactory),
                retryFilter);

            this.typeMapRefreshTimer = new AsyncTaskSafeTimer(
                this.logger, 
                RefreshGrainTypeResolver, 
                null,
                this.typeMapRefreshInterval,
                this.typeMapRefreshInterval);

            ClientStatistics.Start(transport, clientId);
            
            await ExecuteWithRetries(StreamingInitialize, retryFilter);

            async Task ExecuteWithRetries(Func<Task> task, Func<Exception, Task<bool>> shouldRetry)
            {
                while (true)
                {
                    try
                    {
                        await task();
                        return;
                    }
                    catch (Exception exception) when (shouldRetry != null)
                    {
                        var retry = await shouldRetry(exception);
                        if (!retry) throw;
                    }
                }
            }
        }

        private async Task RefreshGrainTypeResolver(object _)
        {
            try
            {
                GrainTypeResolver = await transport.GetGrainTypeResolver(this.InternalGrainFactory);
            }
            catch(Exception ex)
            {
                this.logger.Warn(ErrorCode.TypeManager_GetClusterGrainTypeResolverError, "Refresh the GrainTypeResolver failed. WIll be retried after", ex);
            }
        }

        private void RunClientMessagePump(CancellationToken ct)
        {
            if (StatisticsCollector.CollectThreadTimeTrackingStats)
            {
                incomingMessagesThreadTimeTracking.OnStartExecution();
            }
            while (listenForMessages)
            {
                var message = transport.WaitMessage(Message.Categories.Application, ct);

                if (message == null) // if wait was cancelled
                    break;
#if TRACK_DETAILED_STATS
                        if (StatisticsCollector.CollectThreadTimeTrackingStats)
                        {
                            incomingMessagesThreadTimeTracking.OnStartProcessing();
                        }
#endif

                // when we receive the first message, we update the
                // clientId for this client because it may have been modified to
                // include the cluster name
                if (!firstMessageReceived)
                {
                    firstMessageReceived = true;
                    if (!handshakeClientId.Equals(message.TargetGrain))
                    {
                        clientId = message.TargetGrain;
                        transport.UpdateClientId(clientId);
                        CurrentActivationAddress = ActivationAddress.GetAddress(transport.MyAddress, clientId, CurrentActivationAddress.Activation);
                    }
                    else
                    {
                        clientId = handshakeClientId;
                    }
                }

                switch (message.Direction)
                {
                    case Message.Directions.Response:
                        {
                            ReceiveResponse(message);
                            break;
                        }
                    case Message.Directions.OneWay:
                    case Message.Directions.Request:
                        {
                            this.DispatchToLocalObject(message);
                            break;
                        }
                    default:
                        logger.Error(ErrorCode.Runtime_Error_100327, $"Message not supported: {message}.");
                        break;
                }
#if TRACK_DETAILED_STATS
                        if (StatisticsCollector.CollectThreadTimeTrackingStats)
                        {
                            incomingMessagesThreadTimeTracking.OnStopProcessing();
                            incomingMessagesThreadTimeTracking.IncrementNumberOfProcessed();
                        }
#endif
            }
            if (StatisticsCollector.CollectThreadTimeTrackingStats)
            {
                incomingMessagesThreadTimeTracking.OnStopExecution();
            }
        }

        private void DispatchToLocalObject(Message message)
        {
            LocalObjectData objectData;
            GuidId observerId = message.TargetObserverId;
            if (observerId == null)
            {
                logger.Error(
                    ErrorCode.ProxyClient_OGC_TargetNotFound_2,
                    $"Did not find TargetObserverId header in the message = {message}. A request message to a client is expected to have an observerId.");
                return;
            }

            if (localObjects.TryGetValue(observerId, out objectData))
                this.InvokeLocalObjectAsync(objectData, message);
            else
            {
                logger.Error(
                    ErrorCode.ProxyClient_OGC_TargetNotFound,
                    $"Unexpected target grain in request: {message.TargetGrain}. Message={message}");
            }
        }

        private void InvokeLocalObjectAsync(LocalObjectData objectData, Message message)
        {
            var obj = (IAddressable)objectData.LocalObject.Target;
            if (obj == null)
            {
                //// Remove from the dictionary record for the garbage collected object? But now we won't be able to detect invalid dispatch IDs anymore.
                logger.Warn(ErrorCode.Runtime_Error_100162,
                    $"Object associated with Observer ID {objectData.ObserverId} has been garbage collected. Deleting object reference and unregistering it. Message = {message}");

                LocalObjectData ignore;
                // Try to remove. If it's not there, we don't care.
                localObjects.TryRemove(objectData.ObserverId, out ignore);
                return;
            }

            bool start;
            lock (objectData.Messages)
            {
                objectData.Messages.Enqueue(message);
                start = !objectData.Running;
                objectData.Running = true;
            }
            if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("InvokeLocalObjectAsync {0} start {1}", message, start);
            if (start)
            {
                // we use Task.Run() to ensure that the message pump operates asynchronously
                // with respect to the current thread. see
                // http://channel9.msdn.com/Events/TechEd/Europe/2013/DEV-B317#fbid=aIWUq0ssW74
                // at position 54:45.
                //
                // according to the information posted at:
                // http://stackoverflow.com/questions/12245935/is-task-factory-startnew-guaranteed-to-use-another-thread-than-the-calling-thr
                // this idiom is dependent upon the a TaskScheduler not implementing the
                // override QueueTask as task inlining (as opposed to queueing). this seems
                // implausible to the author, since none of the .NET schedulers do this and
                // it is considered bad form (the OrleansTaskScheduler does not do this).
                //
                // if, for some reason this doesn't hold true, we can guarantee what we
                // want by passing a placeholder continuation token into Task.StartNew()
                // instead. i.e.:
                //
                // return Task.StartNew(() => ..., new CancellationToken());
                Func<Task> asyncFunc =
                    async () =>
                        await this.LocalObjectMessagePumpAsync(objectData);
                Task.Run(asyncFunc).Ignore();
            }
        }

        private async Task LocalObjectMessagePumpAsync(LocalObjectData objectData)
        {
            while (true)
            {
                try
                {
                    Message message;
                    lock (objectData.Messages)
                    {
                        if (objectData.Messages.Count == 0)
                        {
                            objectData.Running = false;
                            break;
                        }
                        message = objectData.Messages.Dequeue();
                    }

                    if (ExpireMessageIfExpired(message, MessagingStatisticsGroup.Phase.Invoke))
                        continue;

                    RequestContextExtensions.Import(message.RequestContextData);
                    var request = (InvokeMethodRequest)message.GetDeserializedBody(this.serializationManager);
                    var targetOb = (IAddressable)objectData.LocalObject.Target;
                    object resultObject = null;
                    Exception caught = null;
                    try
                    {
                        // exceptions thrown within this scope are not considered to be thrown from user code
                        // and not from runtime code.
                        var resultPromise = objectData.Invoker.Invoke(targetOb, request);
                        if (resultPromise != null) // it will be null for one way messages
                        {
                            resultObject = await resultPromise;
                        }
                    }
                    catch (Exception exc)
                    {
                        // the exception needs to be reported in the log or propagated back to the caller.
                        caught = exc;
                    }
                    if (caught != null)
                        this.ReportException(message, caught);
                    else if (message.Direction != Message.Directions.OneWay)
                        await this.SendResponseAsync(message, resultObject);
                }
                catch (Exception)
                {
                    // ignore, keep looping.
                }
            }
        }

        private static bool ExpireMessageIfExpired(Message message, MessagingStatisticsGroup.Phase phase)
        {
            if (message.IsExpired)
            {
                message.DropExpiredMessage(phase);
                return true;
            }
            return false;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        private Task
            SendResponseAsync(
                Message message,
                object resultObject)
        {
            if (ExpireMessageIfExpired(message, MessagingStatisticsGroup.Phase.Respond))
                return Task.CompletedTask;

            object deepCopy = null;
            try
            {
                // we're expected to notify the caller if the deep copy failed.
                deepCopy = this.serializationManager.DeepCopy(resultObject);
            }
            catch (Exception exc2)
            {
                SendResponse(message, Response.ExceptionResponse(exc2));
                logger.Warn(
                    ErrorCode.ProxyClient_OGC_SendResponseFailed,
                    "Exception trying to send a response.", exc2);
                return Task.CompletedTask;
            }

            // the deep-copy succeeded.
            SendResponse(message, new Response(deepCopy));
            return Task.CompletedTask;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        private void ReportException(Message message, Exception exception)
        {
            var request = (InvokeMethodRequest)message.GetDeserializedBody(this.serializationManager);
            switch (message.Direction)
            {
                default:
                    throw new InvalidOperationException();
                case Message.Directions.OneWay:
                    {
                        logger.Error(
                            ErrorCode.ProxyClient_OGC_UnhandledExceptionInOneWayInvoke,
                            $"Exception during invocation of notification method {request.MethodId}, interface {request.InterfaceId}. Ignoring exception because this is a one way request.",
                            exception);
                        break;
                    }
                case Message.Directions.Request:
                    {
                        Exception deepCopy = null;
                        try
                        {
                            // we're expected to notify the caller if the deep copy failed.
                            deepCopy = (Exception)this.serializationManager.DeepCopy(exception);
                        }
                        catch (Exception ex2)
                        {
                            SendResponse(message, Response.ExceptionResponse(ex2));
                            logger.Warn(
                                ErrorCode.ProxyClient_OGC_SendExceptionResponseFailed,
                                "Exception trying to send an exception response", ex2);
                            return;
                        }
                        // the deep-copy succeeded.
                        var response = Response.ExceptionResponse(deepCopy);
                        SendResponse(message, response);
                        break;
                    }
            }
        }

        private void SendResponse(Message request, Response response)
        {
            var message = this.messageFactory.CreateResponseMessage(request);
            message.BodyObject = response;

            transport.SendMessage(message);
        }

        /// <summary>
        /// For testing only.
        /// </summary>
        public void Disconnect()
        {
            transport.Disconnect();
        }

        /// <summary>
        /// For testing only.
        /// </summary>
        public void Reconnect()
        {
            transport.Reconnect();
        }

        #region Implementation of IRuntimeClient

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope",
            Justification = "CallbackData is IDisposable but instances exist beyond lifetime of this method so cannot Dispose yet.")]
        public void SendRequest(GrainReference target, InvokeMethodRequest request, TaskCompletionSource<object> context, Action<Message, TaskCompletionSource<object>> callback, string debugContext = null, InvokeMethodOptions options = InvokeMethodOptions.None, string genericArguments = null)
        {
            var message = this.messageFactory.CreateMessage(request, options);
            SendRequestMessage(target, message, context, callback, debugContext, options, genericArguments);
        }

        private void SendRequestMessage(GrainReference target, Message message, TaskCompletionSource<object> context, Action<Message, TaskCompletionSource<object>> callback, string debugContext = null, InvokeMethodOptions options = InvokeMethodOptions.None, string genericArguments = null)
        {
            var targetGrainId = target.GrainId;
            var oneWay = (options & InvokeMethodOptions.OneWay) != 0;
            message.SendingGrain = CurrentActivationAddress.Grain;
            message.SendingActivation = CurrentActivationAddress.Activation;
            message.TargetGrain = targetGrainId;
            if (!String.IsNullOrEmpty(genericArguments))
                message.GenericGrainType = genericArguments;

            if (targetGrainId.IsSystemTarget)
            {
                // If the silo isn't be supplied, it will be filled in by the sender to be the gateway silo
                message.TargetSilo = target.SystemTargetSilo;
                if (target.SystemTargetSilo != null)
                {
                    message.TargetActivation = ActivationId.GetSystemActivation(targetGrainId, target.SystemTargetSilo);
                }
            }
            // Client sending messages to another client (observer). Yes, we support that.
            if (target.IsObserverReference)
            {
                message.TargetObserverId = target.ObserverId;
            }

            if (debugContext != null)
            {
                message.DebugContext = debugContext;
            }
            if (message.IsExpirableMessage(this.clientMessagingOptions.DropExpiredMessages))
            {
                // don't set expiration for system target messages.
                message.TimeToLive = responseTimeout;
            }

            if (!oneWay)
            {
                var callbackData = new CallbackData(
                    callback,
                    tryResendMessage,
                    context,
                    message,
                    unregisterCallback,
                    this.clientMessagingOptions,
                    this.callBackDataLogger,
                    this.timerLogger);
                callbacks.TryAdd(message.Id, callbackData);
                callbackData.StartTimer(responseTimeout);
            }

            if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Send {0}", message);
            transport.SendMessage(message);
        }

        private bool TryResendMessage(Message message)
        {
            if (!message.MayResend(this.clientMessagingOptions.MaxResendCount))
            {
                return false;
            }

            if (logger.IsEnabled(LogLevel.Debug)) logger.Debug("Resend {0}", message);

            message.ResendCount = message.ResendCount + 1;
            message.TargetHistory = message.GetTargetHistory();

            if (!message.TargetGrain.IsSystemTarget)
            {
                message.TargetActivation = null;
                message.TargetSilo = null;
                message.ClearTargetAddress();
            }

            transport.SendMessage(message);
            return true;
        }

        public void ReceiveResponse(Message response)
        {
            if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Received {0}", response);

            // ignore duplicate requests
            if (response.Result == Message.ResponseTypes.Rejection && response.RejectionType == Message.RejectionTypes.DuplicateRequest)
                return;

            CallbackData callbackData;
            var found = callbacks.TryGetValue(response.Id, out callbackData);
            if (found)
            {
                // We need to import the RequestContext here as well.
                // Unfortunately, it is not enough, since CallContext.LogicalGetData will not flow "up" from task completion source into the resolved task.
                // RequestContextExtensions.Import(response.RequestContextData);
                callbackData.DoCallback(response);
            }
            else
            {
                logger.Warn(ErrorCode.Runtime_Error_100011, "No callback for response message: " + response);
            }
        }

        private void UnRegisterCallback(CorrelationId id)
        {
            CallbackData ignore;
            callbacks.TryRemove(id, out ignore);
        }

        public void Reset(bool cleanup)
        {
            Utils.SafeExecute(() =>
            {
                if (logger != null)
                {
                    logger.Info("OutsideRuntimeClient.Reset(): client Id " + clientId);
                }
            }, this.logger);

            Utils.SafeExecute(() =>
            {
                if (typeMapRefreshTimer != null)
                {
                    typeMapRefreshTimer.Dispose();
                    typeMapRefreshTimer = null;
                }
            }, logger, "Client.typeMapRefreshTimer.Dispose");
            Utils.SafeExecute(() =>
            {
                if (clientProviderRuntime != null)
                {
                    clientProviderRuntime.Reset(cleanup).WaitWithThrow(resetTimeout);
                }
            }, logger, "Client.clientProviderRuntime.Reset");
            Utils.SafeExecute(() =>
            {
                if (StatisticsCollector.CollectThreadTimeTrackingStats)
                {
                    incomingMessagesThreadTimeTracking.OnStopExecution();
                }
            }, logger, "Client.incomingMessagesThreadTimeTracking.OnStopExecution");
            Utils.SafeExecute(() =>
            {
                if (transport != null)
                {
                    transport.PrepareToStop();
                }
            }, logger, "Client.PrepareToStop-Transport");

            listenForMessages = false;
            Utils.SafeExecute(() =>
            {
                if (listeningCts != null)
                {
                    listeningCts.Cancel();
                }
            }, logger, "Client.Stop-ListeningCTS");
            Utils.SafeExecute(() =>
                {
                    if (transport != null)
                    {
                        transport.Stop();
                    }
                }, logger, "Client.Stop-Transport");
            Utils.SafeExecute(() =>
            {
                if (ClientStatistics != null)
                {
                    ClientStatistics.Stop();
                }
            }, logger, "Client.Stop-ClientStatistics");
            ConstructorReset();
        }

        private void ConstructorReset()
        {
            Utils.SafeExecute(() =>
            {
                if (logger != null)
                {
                    logger.Info("OutsideRuntimeClient.ConstructorReset(): client Id " + clientId);
                }
            });
            
            try
            {
                AppDomain.CurrentDomain.DomainUnload -= CurrentDomain_DomainUnload;
            }
            catch (Exception) { }
            try
            {
                if (clientProviderRuntime != null)
                {
                    clientProviderRuntime.Reset().WaitWithThrow(resetTimeout);
                }
            }
            catch (Exception) { }

            Utils.SafeExecute(() => this.Dispose());
        }

        public void SetResponseTimeout(TimeSpan timeout)
        {
            responseTimeout = timeout;
        }

        public TimeSpan GetResponseTimeout()
        {
            return responseTimeout;
        }

        public GrainReference CreateObjectReference(IAddressable obj, IGrainMethodInvoker invoker)
        {
            if (obj is GrainReference)
                throw new ArgumentException("Argument obj is already a grain reference.", nameof(obj));

            if (obj is Grain)
                throw new ArgumentException("Argument must not be a grain class.", nameof(obj));

            GrainReference gr = GrainReference.NewObserverGrainReference(clientId, GuidId.GetNewGuidId(), this.GrainReferenceRuntime);
            if (!localObjects.TryAdd(gr.ObserverId, new LocalObjectData(obj, gr.ObserverId, invoker)))
            {
                throw new ArgumentException(String.Format("Failed to add new observer {0} to localObjects collection.", gr), "gr");
            }
            return gr;
        }

        public void DeleteObjectReference(IAddressable obj)
        {
            if (!(obj is GrainReference))
                throw new ArgumentException("Argument reference is not a grain reference.");

            var reference = (GrainReference)obj;
            LocalObjectData ignore;
            if (!localObjects.TryRemove(reference.ObserverId, out ignore))
                throw new ArgumentException("Reference is not associated with a local object.", "reference");
        }

        #endregion Implementation of IRuntimeClient

        private void CurrentDomain_DomainUnload(object sender, EventArgs e)
        {
            try
            {
                logger.Warn(ErrorCode.ProxyClient_AppDomain_Unload,
                    $"Current AppDomain={PrintAppDomainDetails()} is unloading.");
            }
            catch (Exception)
            {
                // just ignore, make sure not to throw from here.
            }
        }

        private string PrintAppDomainDetails()
        {
            return string.Format("<AppDomain.Id={0}, AppDomain.FriendlyName={1}>", AppDomain.CurrentDomain.Id, AppDomain.CurrentDomain.FriendlyName);
        }

        private class LocalObjectData
        {
            internal WeakReference LocalObject { get; private set; }
            internal IGrainMethodInvoker Invoker { get; private set; }
            internal GuidId ObserverId { get; private set; }
            internal Queue<Message> Messages { get; private set; }
            internal bool Running { get; set; }

            internal LocalObjectData(IAddressable obj, GuidId observerId, IGrainMethodInvoker invoker)
            {
                LocalObject = new WeakReference(obj);
                ObserverId = observerId;
                Invoker = invoker;
                Messages = new Queue<Message>();
                Running = false;
            }
        }

        public void Dispose()
        {
            if (this.disposing) return;
            this.disposing = true;

            Utils.SafeExecute(() =>
            {
                if (typeMapRefreshTimer != null)
                {
                    typeMapRefreshTimer.Dispose();
                    typeMapRefreshTimer = null;
                }
            });

            if (listeningCts != null)
            {
                Utils.SafeExecute(() => listeningCts.Dispose());
                listeningCts = null;
            }
            
            Utils.SafeExecute(() => transport?.Dispose());
            if (ClientStatistics != null)
            {
                Utils.SafeExecute(() => ClientStatistics.Dispose());
                ClientStatistics = null;
            }

            Utils.SafeExecute(() => (this.ServiceProvider as IDisposable)?.Dispose());
            this.ServiceProvider = null;
            GC.SuppressFinalize(this);
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

        /// <inheritdoc />
        public ClientInvokeCallback ClientInvokeCallback { get; set; }
        
        /// <inheritdoc />
        public event ConnectionToClusterLostHandler ClusterConnectionLost;

        /// <inheritdoc />
        public void NotifyClusterConnectionLost()
        {
            try
            {
                this.ClusterConnectionLost?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                this.logger.Error(ErrorCode.ClientError, "Error when sending cluster disconnection notification", ex);
            }
        }
    }
}