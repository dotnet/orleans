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
using Orleans.Internal;
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

        private bool disposing;

        private ClientProviderRuntime clientProviderRuntime;

        internal readonly ClientStatisticsManager ClientStatistics;
        private readonly MessagingTrace messagingTrace;
        private readonly GrainId clientId;
        private ThreadTrackingStatistic incomingMessagesThreadTimeTracking;

        private readonly TimeSpan typeMapRefreshInterval;
        private AsyncTaskSafeTimer typeMapRefreshTimer = null;

        private static readonly TimeSpan ResetTimeout = TimeSpan.FromMinutes(1);

        private const string BARS = "----------";
        
        public IInternalGrainFactory InternalGrainFactory { get; private set; }

        private MessageFactory messageFactory;
        private IPAddress localAddress;
        private readonly ILoggerFactory loggerFactory;
        private readonly IOptions<StatisticsOptions> statisticsOptions;
        private readonly ApplicationRequestsStatisticsGroup appRequestStatistics;

        private readonly StageAnalysisStatisticsGroup schedulerStageStatistics;
        private readonly SharedCallbackData sharedCallbackData;
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

        public IStreamProviderRuntime CurrentStreamProviderRuntime
        {
            get { return clientProviderRuntime; }
        }

        public IGrainReferenceRuntime GrainReferenceRuntime { get; private set; }

        internal ClientMessageCenter MessageCenter { get; private set; }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope",
            Justification = "MessageCenter is IDisposable but cannot call Dispose yet as it lives past the end of this method call.")]
        public OutsideRuntimeClient(
            ILoggerFactory loggerFactory, 
            IOptions<ClientMessagingOptions> clientMessagingOptions,
            IOptions<TypeManagementOptions> typeManagementOptions,
            IOptions<StatisticsOptions> statisticsOptions,
            ApplicationRequestsStatisticsGroup appRequestStatistics,
            StageAnalysisStatisticsGroup schedulerStageStatistics,
            ClientStatisticsManager clientStatisticsManager,
            MessagingTrace messagingTrace)
        {
            this.loggerFactory = loggerFactory;
            this.statisticsOptions = statisticsOptions;
            this.appRequestStatistics = appRequestStatistics;
            this.schedulerStageStatistics = schedulerStageStatistics;
            this.ClientStatistics = clientStatisticsManager;
            this.messagingTrace = messagingTrace;
            this.logger = loggerFactory.CreateLogger<OutsideRuntimeClient>();
            this.clientId = GrainId.NewClientId();
            callbacks = new ConcurrentDictionary<CorrelationId, CallbackData>();
            this.clientMessagingOptions = clientMessagingOptions.Value;
            this.typeMapRefreshInterval = typeManagementOptions.Value.TypeMapRefreshInterval;

            this.sharedCallbackData = new SharedCallbackData(
                msg => this.UnregisterCallback(msg.Id),
                this.loggerFactory.CreateLogger<CallbackData>(),
                this.clientMessagingOptions,
                this.appRequestStatistics,
                this.clientMessagingOptions.ResponseTimeout);
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

                var gatewayCountChangedHandlers = this.ServiceProvider.GetServices<GatewayCountChangedHandler>();
                foreach (var handler in gatewayCountChangedHandlers)
                {
                    this.GatewayCountChanged += handler;
                }

                var clientInvokeCallbacks = this.ServiceProvider.GetServices<ClientInvokeCallback>();
                foreach (var handler in clientInvokeCallbacks)
                {
                    this.ClientInvokeCallback += handler;
                }

                this.InternalGrainFactory = this.ServiceProvider.GetRequiredService<IInternalGrainFactory>();
                this.messageFactory = this.ServiceProvider.GetService<MessageFactory>();

                var serializationManager = this.ServiceProvider.GetRequiredService<SerializationManager>();
                this.localObjects = new InvokableObjectManager(
                    this,
                    serializationManager,
                    this.messagingTrace,
                    this.loggerFactory.CreateLogger<InvokableObjectManager>());

                var timerLogger = this.loggerFactory.CreateLogger<SafeTimer>();
                var minTicks = Math.Min(this.clientMessagingOptions.ResponseTimeout.Ticks, TimeSpan.FromSeconds(1).Ticks);
                var period = TimeSpan.FromTicks(minTicks);
                this.callbackTimer = new SafeTimer(timerLogger, this.OnCallbackExpiryTick, null, period, period);
                
                this.GrainReferenceRuntime = this.ServiceProvider.GetRequiredService<IGrainReferenceRuntime>();

                BufferPool.InitGlobalBufferPool(this.clientMessagingOptions);

                this.clientProviderRuntime = this.ServiceProvider.GetRequiredService<ClientProviderRuntime>();

                this.localAddress = this.clientMessagingOptions.LocalAddress ?? ConfigUtilities.GetLocalIPAddress(this.clientMessagingOptions.PreferredFamily, this.clientMessagingOptions.NetworkInterfaceName);

                // Client init / sign-on message
                logger.Info(ErrorCode.ClientInitializing, string.Format(
                    "{0} Initializing OutsideRuntimeClient on {1} at {2} Client Id = {3} {0}",
                    BARS, Dns.GetHostName(), localAddress,  clientId));
                string startMsg = string.Format("{0} Starting OutsideRuntimeClient with runtime Version='{1}' in AppDomain={2}",
                    BARS, RuntimeVersion.Current, PrintAppDomainDetails());
                logger.Info(ErrorCode.ClientStarting, startMsg);

                if (TestOnlyThrowExceptionDuringInit)
                {
                    throw new InvalidOperationException("TestOnlyThrowExceptionDuringInit");
                }

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
            var implicitSubscriberTable = await MessageCenter.GetImplicitStreamSubscriberTable(this.InternalGrainFactory);
            clientProviderRuntime.StreamingInitialize(implicitSubscriberTable);
        }

        public async Task Start(Func<Exception, Task<bool>> retryFilter = null)
        {
            // Deliberately avoid capturing the current synchronization context during startup and execute on the default scheduler.
            // This helps to avoid any issues (such as deadlocks) caused by executing with the client's synchronization context/scheduler.
            await Task.Run(() => this.StartInternal(retryFilter)).ConfigureAwait(false);

            logger.Info(ErrorCode.ProxyClient_StartDone, "{0} Started OutsideRuntimeClient with Global Client ID: {1}", BARS, CurrentActivationAddress.ToString() + ", client GUID ID: " + clientId);
        }
        
        // used for testing to (carefully!) allow two clients in the same process
        private async Task StartInternal(Func<Exception, Task<bool>> retryFilter)
        {
            var gatewayManager = this.ServiceProvider.GetRequiredService<GatewayManager>();
            await ExecuteWithRetries(async () => await gatewayManager.StartAsync(CancellationToken.None), retryFilter);

            var generation = -SiloAddress.AllocateNewGeneration(); // Client generations are negative
            MessageCenter = ActivatorUtilities.CreateInstance<ClientMessageCenter>(this.ServiceProvider, localAddress, generation, clientId);
            MessageCenter.RegisterLocalMessageHandler(this.HandleMessage);
            MessageCenter.Start();
            CurrentActivationAddress = ActivationAddress.NewActivationAddress(MessageCenter.MyAddress, clientId);

            await ExecuteWithRetries(
                async () => this.GrainTypeResolver = await MessageCenter.GetGrainTypeResolver(this.InternalGrainFactory),
                retryFilter);

            this.typeMapRefreshTimer = new AsyncTaskSafeTimer(
                this.logger, 
                RefreshGrainTypeResolver, 
                null,
                this.typeMapRefreshInterval,
                this.typeMapRefreshInterval);

            ClientStatistics.Start(MessageCenter, clientId);
            
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
                GrainTypeResolver = await MessageCenter.GetGrainTypeResolver(this.InternalGrainFactory);
            }
            catch(Exception ex)
            {
                this.logger.Warn(ErrorCode.TypeManager_GetClusterGrainTypeResolverError, "Refresh the GrainTypeResolver failed. Will be retried after", ex);
            }
        }

        private void HandleMessage(Message message)
        {
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

        public void SendResponse(Message request, Response response)
        {
            var message = this.messageFactory.CreateResponseMessage(request);
            OrleansOutsideRuntimeClientEvent.Log.SendResponse(message);
            message.BodyObject = response;

            MessageCenter.SendMessage(message);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope",
            Justification = "CallbackData is IDisposable but instances exist beyond lifetime of this method so cannot Dispose yet.")]
        public void SendRequest(GrainReference target, InvokeMethodRequest request, TaskCompletionSource<object> context, InvokeMethodOptions options, string genericArguments)
        {
            var message = this.messageFactory.CreateMessage(request, options);
            OrleansOutsideRuntimeClientEvent.Log.SendRequest(message);
            SendRequestMessage(target, message, context, options, genericArguments);
        }

        private void SendRequestMessage(GrainReference target, Message message, TaskCompletionSource<object> context, InvokeMethodOptions options, string genericArguments)
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
            MessageCenter.SendMessage(message);
        }

        public void ReceiveResponse(Message response)
        {
            OrleansOutsideRuntimeClientEvent.Log.ReceiveResponse(response);

            if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Received {0}", response);

            // ignore duplicate requests
            if (response.Result == Message.ResponseTypes.Rejection
                && (response.RejectionType == Message.RejectionTypes.DuplicateRequest
                 || response.RejectionType == Message.RejectionTypes.CacheInvalidation))
            {
                return;
            }
            
            CallbackData callbackData;
            var found = callbacks.TryRemove(response.Id, out callbackData);
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
                    if (MessageCenter != null)
                    {
                        MessageCenter.Stop();
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
            
            Utils.SafeExecute(() => MessageCenter?.Dispose());
            if (ClientStatistics != null)
            {
                Utils.SafeExecute(() => ClientStatistics.Dispose());
            }

            Utils.SafeExecute(() => (this.ServiceProvider as IDisposable)?.Dispose());

            Utils.SafeExecute(() => this.ClusterConnectionLost = null);
            Utils.SafeExecute(() => this.GatewayCountChanged = null);
            Utils.SafeExecute(() => this.ClientInvokeCallback = null);

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
        public event GatewayCountChangedHandler GatewayCountChanged;

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

        /// <inheritdoc />
        public void NotifyGatewayCountChanged(int currentNumberOfGateways, int previousNumberOfGateways)
        {
            try
            {
                this.GatewayCountChanged?.Invoke(this, new GatewayCountChangedEventArgs(currentNumberOfGateways, previousNumberOfGateways));
            }
            catch (Exception ex)
            {
                this.logger.Error(ErrorCode.ClientError, "Error when sending gateway count changed notification", ex);
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