using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.ClientObservers;
using Orleans.CodeGeneration;
using Orleans.Configuration;
using Orleans.Messaging;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Serialization;
using Orleans.Serialization.Invocation;

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
        private readonly ClientGrainId clientId;
        private ThreadTrackingStatistic incomingMessagesThreadTimeTracking;

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
        public ClientGatewayObserver gatewayObserver { get; private set; }

        public string CurrentActivationIdentity
        {
            get { return CurrentActivationAddress.ToString(); }
        }

        public IGrainReferenceRuntime GrainReferenceRuntime { get; private set; }

        internal ClientMessageCenter MessageCenter { get; private set; }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope",
            Justification = "MessageCenter is IDisposable but cannot call Dispose yet as it lives past the end of this method call.")]
        public OutsideRuntimeClient(
            ILoggerFactory loggerFactory, 
            IOptions<ClientMessagingOptions> clientMessagingOptions,
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
            this.clientId = ClientGrainId.Create();
            callbacks = new ConcurrentDictionary<CorrelationId, CallbackData>();
            this.clientMessagingOptions = clientMessagingOptions.Value;

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

                this.InternalGrainFactory = this.ServiceProvider.GetRequiredService<IInternalGrainFactory>();
                this.messageFactory = this.ServiceProvider.GetService<MessageFactory>();

                var copier = this.ServiceProvider.GetRequiredService<DeepCopier>();
                this.localObjects = new InvokableObjectManager(
                    services.GetRequiredService<ClientGrainContext>(),
                    this,
                    copier,
                    this.messagingTrace,
                    this.loggerFactory.CreateLogger<ClientGrainContext>());

                var timerLogger = this.loggerFactory.CreateLogger<SafeTimer>();
                var minTicks = Math.Min(this.clientMessagingOptions.ResponseTimeout.Ticks, TimeSpan.FromSeconds(1).Ticks);
                var period = TimeSpan.FromTicks(minTicks);
                this.callbackTimer = new SafeTimer(timerLogger, this.OnCallbackExpiryTick, null, period, period);
                
                this.GrainReferenceRuntime = this.ServiceProvider.GetRequiredService<IGrainReferenceRuntime>();

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

        public async Task Start(Func<Exception, Task<bool>> retryFilter = null)
        {
            // Deliberately avoid capturing the current synchronization context during startup and execute on the default scheduler.
            // This helps to avoid any issues (such as deadlocks) caused by executing with the client's synchronization context/scheduler.
            await Task.Run(() => this.StartInternal(retryFilter)).ConfigureAwait(false);

            logger.Info(ErrorCode.ProxyClient_StartDone, "{0} Started OutsideRuntimeClient with Global Client ID: {1}", BARS, CurrentActivationAddress.ToString() + ", client ID: " + clientId);
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
            CurrentActivationAddress = ActivationAddress.NewActivationAddress(MessageCenter.MyAddress, clientId.GrainId);

            this.gatewayObserver = new ClientGatewayObserver(gatewayManager);
            this.InternalGrainFactory.CreateObjectReference<IClientGatewayObserver>(this.gatewayObserver);

            await ExecuteWithRetries(
                async () => await this.ServiceProvider.GetRequiredService<ClientClusterManifestProvider>().StartAsync(),
                retryFilter);

            ClientStatistics.Start(MessageCenter, clientId.GrainId);

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

        public void SendRequest(GrainReference target, IInvokable request, IResponseCompletionSource context, InvokeMethodOptions options)
        {
            var message = this.messageFactory.CreateMessage(request, options);
            OrleansOutsideRuntimeClientEvent.Log.SendRequest(message);

            SendRequestMessage(target, message, context, options);
        }

        private void SendRequestMessage(GrainReference target, Message message, IResponseCompletionSource context, InvokeMethodOptions options)
        {
            message.InterfaceType = target.InterfaceType;
            message.InterfaceVersion = target.InterfaceVersion;
            var targetGrainId = target.GrainId;
            var oneWay = (options & InvokeMethodOptions.OneWay) != 0;
            message.SendingGrain = CurrentActivationAddress.Grain;
            message.SendingActivation = CurrentActivationAddress.Activation;
            message.TargetGrain = targetGrainId;

            if (SystemTargetGrainId.TryParse(targetGrainId, out var systemTargetGrainId))
            {
                // If the silo isn't be supplied, it will be filled in by the sender to be the gateway silo
                message.TargetSilo = systemTargetGrainId.GetSiloAddress();
                message.TargetActivation = ActivationId.GetDeterministic(targetGrainId);
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
            else
            {
                context?.Complete();
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
            else if (response.Result == Message.ResponseTypes.Status)
            {
                var status = (StatusResponse)response.BodyObject;
                callbacks.TryGetValue(response.Id, out var callback);
                var request = callback?.Message;
                if (!(request is null))
                {
                    callback.OnStatusUpdate(status);

                    if (status.Diagnostics != null && status.Diagnostics.Count > 0 && logger.IsEnabled(LogLevel.Information))
                    {
                        var diagnosticsString = string.Join("\n", status.Diagnostics);
                        this.logger.LogInformation("Received status update for pending request, Request: {RequestMessage}. Status: {Diagnostics}", request, diagnosticsString);
                    }
                }
                else
                {
                    if (status.Diagnostics != null && status.Diagnostics.Count > 0 && logger.IsEnabled(LogLevel.Information))
                    {
                        var diagnosticsString = string.Join("\n", status.Diagnostics);
                        this.logger.LogInformation("Received status update for unknown request. Message: {StatusMessage}. Status: {Diagnostics}", response, diagnosticsString);
                    }
                }

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
            callbacks.TryRemove(id, out _);
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
            
            Utils.SafeExecute(() => this.Dispose());
        }

        /// <inheritdoc />
        public TimeSpan GetResponseTimeout() => this.sharedCallbackData.ResponseTimeout;

        /// <inheritdoc />
        public void SetResponseTimeout(TimeSpan timeout) => this.sharedCallbackData.ResponseTimeout = timeout;

        public IAddressable CreateObjectReference(IAddressable obj)
        {
            if (obj is GrainReference)
                throw new ArgumentException("Argument obj is already a grain reference.", nameof(obj));

            if (obj is Grain)
                throw new ArgumentException("Argument must not be a grain class.", nameof(obj));

            var observerId = obj is ClientObserver clientObserver
                ? clientObserver.GetObserverGrainId(this.clientId)
                : ObserverGrainId.Create(this.clientId);
            var reference = this.InternalGrainFactory.GetGrain(observerId.GrainId);

            if (!localObjects.TryRegister(obj, observerId))
            {
                throw new ArgumentException($"Failed to add new observer {reference} to localObjects collection.", "reference");
            }

            return reference;
        }

        public void DeleteObjectReference(IAddressable obj)
        {
            if (!(obj is GrainReference reference))
            {
                throw new ArgumentException("Argument reference is not a grain reference.");
            }

            if (!ObserverGrainId.TryParse(reference.GrainId, out var observerId))
            {
                throw new ArgumentException($"Reference {reference.GrainId} is not an observer reference");
            }

            if (!localObjects.TryDeregister(observerId))
            {
                throw new ArgumentException("Reference is not associated with a local object.", "reference");
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
            
            Utils.SafeExecute(() => MessageCenter?.Dispose());

            this.ClusterConnectionLost = null;
            this.GatewayCountChanged = null;

            GC.SuppressFinalize(this);
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