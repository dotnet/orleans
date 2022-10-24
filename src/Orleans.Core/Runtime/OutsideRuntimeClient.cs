using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.ClientObservers;
using Orleans.CodeGeneration;
using Orleans.Configuration;
using Orleans.Messaging;
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
        private bool disposed;

        private readonly MessagingTrace messagingTrace;
        private readonly ClientGrainId clientId;

        public IInternalGrainFactory InternalGrainFactory { get; private set; }

        private MessageFactory messageFactory;
        private IPAddress localAddress;
        private readonly ILoggerFactory loggerFactory;

        private readonly SharedCallbackData sharedCallbackData;
        private SafeTimer callbackTimer;
        public GrainAddress CurrentActivationAddress
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
            MessagingTrace messagingTrace,
            IServiceProvider serviceProvider)
        {
            this.ServiceProvider = serviceProvider;
            this.loggerFactory = loggerFactory;
            this.messagingTrace = messagingTrace;
            this.logger = loggerFactory.CreateLogger<OutsideRuntimeClient>();
            this.clientId = ClientGrainId.Create();
            callbacks = new ConcurrentDictionary<CorrelationId, CallbackData>();
            this.clientMessagingOptions = clientMessagingOptions.Value;

            this.sharedCallbackData = new SharedCallbackData(
                msg => this.UnregisterCallback(msg.Id),
                this.loggerFactory.CreateLogger<CallbackData>(),
                this.clientMessagingOptions,
                this.clientMessagingOptions.ResponseTimeout);
        }

        internal void ConsumeServices()
        {
            try
            {
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
                    ServiceProvider.GetRequiredService<ClientGrainContext>(),
                    this,
                    copier,
                    this.messagingTrace,
                    this.loggerFactory.CreateLogger<ClientGrainContext>());

                var timerLogger = this.loggerFactory.CreateLogger<SafeTimer>();
                var minTicks = Math.Min(this.clientMessagingOptions.ResponseTimeout.Ticks, TimeSpan.FromSeconds(1).Ticks);
                var period = TimeSpan.FromTicks(minTicks);
                this.callbackTimer = new SafeTimer(timerLogger, this.OnCallbackExpiryTick, null, period, period);

                this.GrainReferenceRuntime = this.ServiceProvider.GetRequiredService<IGrainReferenceRuntime>();

                this.localAddress = this.clientMessagingOptions.LocalAddress ?? ConfigUtilities.GetLocalIPAddress(this.clientMessagingOptions.PreferredFamily, this.clientMessagingOptions.NetworkInterfaceName);

                // Client init / sign-on message
                logger.LogInformation((int)ErrorCode.ClientStarting, "Starting Orleans client with runtime version \"{RuntimeVersion}\", local address {LocalAddress} and client id {ClientId}", RuntimeVersion.Current, localAddress, clientId);

                if (TestOnlyThrowExceptionDuringInit)
                {
                    throw new InvalidOperationException("TestOnlyThrowExceptionDuringInit");
                }
            }
            catch (Exception exc)
            {
                if (logger != null) logger.LogError((int)ErrorCode.Runtime_Error_100319, exc, "OutsideRuntimeClient constructor failed.");
                ConstructorReset();
                throw;
            }
        }

        public IServiceProvider ServiceProvider { get; private set; }

        public async Task Start(CancellationToken cancellationToken)
        {
            ConsumeServices();

            // Deliberately avoid capturing the current synchronization context during startup and execute on the default scheduler.
            // This helps to avoid any issues (such as deadlocks) caused by executing with the client's synchronization context/scheduler.
            await Task.Run(() => this.StartInternal(cancellationToken)).ConfigureAwait(false);

            logger.LogInformation((int)ErrorCode.ProxyClient_StartDone, "Started client with address {ActivationAddress} and id {ClientId}", CurrentActivationAddress.ToString(), clientId);
        }

        // used for testing to (carefully!) allow two clients in the same process
        private async Task StartInternal(CancellationToken cancellationToken)
        {
            var retryFilter = ServiceProvider.GetService<IClientConnectionRetryFilter>();

            var gatewayManager = this.ServiceProvider.GetRequiredService<GatewayManager>();
            await ExecuteWithRetries(
                async () => await gatewayManager.StartAsync(cancellationToken),
                retryFilter,
                cancellationToken);

            var generation = -SiloAddress.AllocateNewGeneration(); // Client generations are negative
            MessageCenter = ActivatorUtilities.CreateInstance<ClientMessageCenter>(this.ServiceProvider, localAddress, generation, clientId);
            MessageCenter.RegisterLocalMessageHandler(this.HandleMessage);
            await ExecuteWithRetries(
                async () => await MessageCenter.StartAsync(cancellationToken),
                retryFilter,
                cancellationToken);
            CurrentActivationAddress = GrainAddress.NewActivationAddress(MessageCenter.MyAddress, clientId.GrainId);

            this.gatewayObserver = new ClientGatewayObserver(gatewayManager);
            this.InternalGrainFactory.CreateObjectReference<IClientGatewayObserver>(this.gatewayObserver);

            await ExecuteWithRetries(
                async () => await this.ServiceProvider.GetRequiredService<ClientClusterManifestProvider>().StartAsync(),
                retryFilter,
                cancellationToken);

            static async Task ExecuteWithRetries(Func<Task> task, IClientConnectionRetryFilter retryFilter, CancellationToken cancellationToken)
            {
                do
                {
                    try
                    {
                        await task();
                        return;
                    }
                    catch (Exception exception) when (retryFilter is not null && !cancellationToken.IsCancellationRequested)
                    {
                        var shouldRetry = await retryFilter.ShouldRetryConnectionAttempt(exception, cancellationToken);
                        if (cancellationToken.IsCancellationRequested || !shouldRetry)
                        {
                            throw;
                        }
                    }
                }
                while (!cancellationToken.IsCancellationRequested);
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
                    logger.LogError((int)ErrorCode.Runtime_Error_100327, "Message not supported: {Message}.", message);
                    break;
            }
        }

        public void SendResponse(Message request, Response response)
        {
            ThrowIfDisposed();
            var message = this.messageFactory.CreateResponseMessage(request);
            OrleansOutsideRuntimeClientEvent.Log.SendResponse(message);
            message.BodyObject = response;

            MessageCenter.SendMessage(message);
        }

        public void SendRequest(GrainReference target, IInvokable request, IResponseCompletionSource context, InvokeMethodOptions options)
        {
            ThrowIfDisposed();
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
            message.SendingGrain = CurrentActivationAddress.GrainId;
            message.TargetGrain = targetGrainId;

            if (SystemTargetGrainId.TryParse(targetGrainId, out var systemTargetGrainId))
            {
                // If the silo isn't be supplied, it will be filled in by the sender to be the gateway silo
                message.TargetSilo = systemTargetGrainId.GetSiloAddress();
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

            if (logger.IsEnabled(LogLevel.Trace)) logger.LogTrace("Send {Message}", message);
            MessageCenter.SendMessage(message);
        }

        public void ReceiveResponse(Message response)
        {
            OrleansOutsideRuntimeClientEvent.Log.ReceiveResponse(response);

            if (logger.IsEnabled(LogLevel.Trace)) logger.LogTrace("Received {Message}", response);

            if (response.Result is Message.ResponseTypes.Status)
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
                logger.LogWarning((int)ErrorCode.Runtime_Error_100011, "No callback for response message {ResponseMessage}", response);
            }
        }

        private void UnregisterCallback(CorrelationId id)
        {
            callbacks.TryRemove(id, out _);
        }

        public void Reset()
        {
            Utils.SafeExecute(() =>
                {
                    if (MessageCenter != null)
                    {
                        MessageCenter.Stop();
                    }
                }, logger, "Client.Stop-Transport");
            ConstructorReset();
        }

        private void ConstructorReset()
        {
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

            if (obj is IGrainBase)
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

        public void Dispose()
        {
            if (this.disposing) return;
            this.disposing = true;

            Utils.SafeExecute(() => this.callbackTimer?.Dispose());

            Utils.SafeExecute(() => MessageCenter?.Dispose());

            this.ClusterConnectionLost = null;
            this.GatewayCountChanged = null;

            GC.SuppressFinalize(this);
            disposed = true;
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
                this.logger.LogError((int)ErrorCode.ClientError, ex, "Error when sending cluster disconnection notification");
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
                this.logger.LogError((int)ErrorCode.ClientError, ex, "Error when sending gateway count changed notification");
            }
        }

        private void OnCallbackExpiryTick(object state)
        {
            var currentStopwatchTicks = ValueStopwatch.GetTimestamp();
            foreach (var pair in callbacks)
            {
                var callback = pair.Value;
                if (callback.IsCompleted) continue;
                if (callback.IsExpired(currentStopwatchTicks)) callback.OnTimeout(this.clientMessagingOptions.ResponseTimeout);
            }
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                ThrowObjectDisposedException();
            }

            void ThrowObjectDisposedException() => throw new ObjectDisposedException(nameof(OutsideRuntimeClient));
        }
    }
}