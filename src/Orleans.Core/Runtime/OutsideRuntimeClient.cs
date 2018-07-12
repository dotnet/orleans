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

        private readonly ILogger logger;
        private readonly ClientMessagingOptions clientMessagingOptions;

        private readonly ConcurrentDictionary<CorrelationId, CallbackData> callbacks;
        private InvokableObjectManager localObjects;

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

        private readonly TimeSpan typeMapRefreshInterval;
        private AsyncTaskSafeTimer typeMapRefreshTimer = null;

        private static readonly TimeSpan ResetTimeout = TimeSpan.FromMinutes(1);

        private const string BARS = "----------";
        
        public IInternalGrainFactory InternalGrainFactory { get; private set; }

        private MessageFactory messageFactory;
        private IPAddress localAddress;
        private IGatewayListProvider gatewayListProvider;
        private readonly ILoggerFactory loggerFactory;
        private readonly IOptions<StatisticsOptions> statisticsOptions;
        private readonly ApplicationRequestsStatisticsGroup appRequestStatistics;

        private readonly StageAnalysisStatisticsGroup schedulerStageStatistics;
        private SharedCallbackData sharedCallbackData;
        private SafeTimer callbackTimer;
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
            IOptions<TypeManagementOptions> typeManagementOptions,
            IOptions<StatisticsOptions> statisticsOptions,
            ApplicationRequestsStatisticsGroup appRequestStatistics,
            StageAnalysisStatisticsGroup schedulerStageStatistics,
            ClientStatisticsManager clientStatisticsManager)
        {
            this.loggerFactory = loggerFactory;
            this.statisticsOptions = statisticsOptions;
            this.appRequestStatistics = appRequestStatistics;
            this.schedulerStageStatistics = schedulerStageStatistics;
            this.ClientStatistics = clientStatisticsManager;
            this.logger = loggerFactory.CreateLogger<OutsideRuntimeClient>();
            this.handshakeClientId = GrainId.NewClientId();
            callbacks = new ConcurrentDictionary<CorrelationId, CallbackData>();
            this.clientMessagingOptions = clientMessagingOptions.Value;
            this.typeMapRefreshInterval = typeManagementOptions.Value.TypeMapRefreshInterval;
        }

        internal void ConsumeServices(IServiceProvider services)
        {
            try
            {
                AppDomain.CurrentDomain.DomainUnload += CurrentDomain_DomainUnload;

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
                this.messageFactory = this.ServiceProvider.GetService<MessageFactory>();

                var serializationManager = this.ServiceProvider.GetRequiredService<SerializationManager>();
                this.localObjects = new InvokableObjectManager(
                    this,
                    serializationManager,
                    this.loggerFactory.CreateLogger<InvokableObjectManager>());

                this.sharedCallbackData = new SharedCallbackData(
                    this.TryResendMessage,
                    msg => this.UnregisterCallback(msg.Id),
                    this.loggerFactory.CreateLogger<CallbackData>(),
                    this.clientMessagingOptions,
                    serializationManager,
                    this.appRequestStatistics);
                var timerLogger = this.loggerFactory.CreateLogger<SafeTimer>();
                var minTicks = Math.Min(this.clientMessagingOptions.ResponseTimeout.Ticks, TimeSpan.FromSeconds(1).Ticks);
                var period = TimeSpan.FromTicks(minTicks);
                this.callbackTimer = new SafeTimer(timerLogger, this.OnCallbackExpiryTick, null, period, period);
                
                this.GrainReferenceRuntime = this.ServiceProvider.GetRequiredService<IGrainReferenceRuntime>();

                BufferPool.InitGlobalBufferPool(this.clientMessagingOptions);

                this.clientProviderRuntime = this.ServiceProvider.GetRequiredService<ClientProviderRuntime>();

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

                var statisticsLevel = statisticsOptions.Value.CollectionLevel;
                if (statisticsLevel.CollectThreadTimeTrackingStats())
                {
                    incomingMessagesThreadTimeTracking = new ThreadTrackingStatistic("ClientReceiver", this.loggerFactory, this.statisticsOptions, this.schedulerStageStatistics);
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
            incomingMessagesThreadTimeTracking?.OnStartExecution();

            while (listenForMessages)
            {
                var message = transport.WaitMessage(Message.Categories.Application, ct);

                if (message == null) // if wait was cancelled
                    break;

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
                            this.localObjects.Dispatch(message);
                            break;
                        }
                    default:
                        logger.Error(ErrorCode.Runtime_Error_100327, $"Message not supported: {message}.");
                        break;
                }
            }

            incomingMessagesThreadTimeTracking?.OnStopExecution();
        }
        
        public void SendResponse(Message request, Response response)
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

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope",
            Justification = "CallbackData is IDisposable but instances exist beyond lifetime of this method so cannot Dispose yet.")]
        public void SendRequest(GrainReference target, InvokeMethodRequest request, TaskCompletionSource<object> context, string debugContext = null, InvokeMethodOptions options = InvokeMethodOptions.None, string genericArguments = null)
        {
            var message = this.messageFactory.CreateMessage(request, options);
            SendRequestMessage(target, message, context, debugContext, options, genericArguments);
        }

        private void SendRequestMessage(GrainReference target, Message message, TaskCompletionSource<object> context, string debugContext = null, InvokeMethodOptions options = InvokeMethodOptions.None, string genericArguments = null)
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
                message.TimeToLive = this.clientMessagingOptions.ResponseTimeout;
            }

            if (!oneWay)
            {
                var callbackData = new CallbackData(this.sharedCallbackData, context, message);
                callbacks.TryAdd(message.Id, callbackData);
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

        private void UnregisterCallback(CorrelationId id)
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
                    clientProviderRuntime.Reset(cleanup).WaitWithThrow(ResetTimeout);
                }
            }, logger, "Client.clientProviderRuntime.Reset");
            Utils.SafeExecute(() =>
            {
                incomingMessagesThreadTimeTracking?.OnStopExecution();
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
                    clientProviderRuntime.Reset().WaitWithThrow(ResetTimeout);
                }
            }
            catch (Exception) { }

            Utils.SafeExecute(() => this.Dispose());
        }

        /// <inheritdoc />
        public TimeSpan GetResponseTimeout() => this.sharedCallbackData.ResponseTimeout;

        /// <inheritdoc />
        public void SetResponseTimeout(TimeSpan timeout) => this.sharedCallbackData.ResponseTimeout = timeout;

        public GrainReference CreateObjectReference(IAddressable obj, IGrainMethodInvoker invoker)
        {
            if (obj is GrainReference)
                throw new ArgumentException("Argument obj is already a grain reference.", nameof(obj));

            if (obj is Grain)
                throw new ArgumentException("Argument must not be a grain class.", nameof(obj));

            GrainReference gr = GrainReference.NewObserverGrainReference(clientId, GuidId.GetNewGuidId(), this.GrainReferenceRuntime);
            if (!localObjects.TryRegister(obj, gr.ObserverId, invoker))
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
            if (!localObjects.TryDeregister(reference.ObserverId))
                throw new ArgumentException("Reference is not associated with a local object.", "reference");
        }

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

        public void Dispose()
        {
            if (this.disposing) return;
            this.disposing = true;
            
            Utils.SafeExecute(() => this.callbackTimer?.Dispose());
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

        private void OnCallbackExpiryTick(object state)
        {
            var currentStopwatchTicks = Stopwatch.GetTimestamp();
            foreach (var pair in callbacks)
            {
                var callback = pair.Value;
                if (callback.IsCompleted) continue;
                if (callback.IsExpired(currentStopwatchTicks)) callback.OnTimeout(this.clientMessagingOptions.ResponseTimeout);
            }
        }
    }
}