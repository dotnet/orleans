using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
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
using Orleans.Serialization;
using Orleans.Serialization.Invocation;
using static Orleans.Internal.StandardExtensions;

namespace Orleans
{
    internal partial class OutsideRuntimeClient : IRuntimeClient, IDisposable, IClusterConnectionStatusListener
    {
        internal static bool TestOnlyThrowExceptionDuringInit { get; set; }

        private readonly ILogger logger;
        private readonly ClientMessagingOptions clientMessagingOptions;

        private readonly ConcurrentDictionary<CorrelationId, CallbackData> callbacks;
        private InvokableObjectManager localObjects;
        private bool disposing;
        private bool disposed;

        private readonly MessagingTrace messagingTrace;
        private readonly InterfaceToImplementationMappingCache _interfaceToImplementationMapping;
        private IGrainCallCancellationManager _cancellationManager;
        private IClusterConnectionStatusObserver[] _statusObservers;

        public IInternalGrainFactory InternalGrainFactory { get; private set; }

        private ClientClusterManifestProvider _manifestProvider;
        private MessageFactory messageFactory;
        private readonly LocalClientDetails _localClientDetails;
        private readonly ILoggerFactory loggerFactory;

        private readonly SharedCallbackData sharedCallbackData;
        private readonly PeriodicTimer callbackTimer;
        private Task callbackTimerTask;

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
            LocalClientDetails localClientDetails,
            ILoggerFactory loggerFactory,
            IOptions<ClientMessagingOptions> clientMessagingOptions,
            MessagingTrace messagingTrace,
            IServiceProvider serviceProvider,
            TimeProvider timeProvider,
            InterfaceToImplementationMappingCache interfaceToImplementationMapping)
        {
            TimeProvider = timeProvider;
            _interfaceToImplementationMapping = interfaceToImplementationMapping;
            this.ServiceProvider = serviceProvider;
            _localClientDetails = localClientDetails;
            this.loggerFactory = loggerFactory;
            this.messagingTrace = messagingTrace;
            this.logger = loggerFactory.CreateLogger<OutsideRuntimeClient>();
            callbacks = new ConcurrentDictionary<CorrelationId, CallbackData>();
            this.clientMessagingOptions = clientMessagingOptions.Value;
            var period = Max(
                TimeSpan.FromMilliseconds(1),
                Min(
                    this.clientMessagingOptions.ResponseTimeout,
                    TimeSpan.FromSeconds(1)));
            this.callbackTimer = new PeriodicTimer(period, timeProvider);
            this.sharedCallbackData = new SharedCallbackData(
                msg => this.UnregisterCallback(msg.Id),
                this.loggerFactory.CreateLogger<CallbackData>(),
                this.clientMessagingOptions.ResponseTimeout,
                this.clientMessagingOptions.CancelRequestOnTimeout,
                this.clientMessagingOptions.WaitForCancellationAcknowledgement,
                null);
        }

        internal void ConsumeServices()
        {
            try
            {
                _statusObservers = this.ServiceProvider.GetServices<IClusterConnectionStatusObserver>().ToArray();
                _manifestProvider = ServiceProvider.GetRequiredService<ClientClusterManifestProvider>();

                this.InternalGrainFactory = this.ServiceProvider.GetRequiredService<IInternalGrainFactory>();
                _cancellationManager = sharedCallbackData.CancellationManager = ServiceProvider.GetRequiredService<IGrainCallCancellationManager>();
                this.messageFactory = this.ServiceProvider.GetService<MessageFactory>();
                this.localObjects = new InvokableObjectManager(
                    ServiceProvider.GetRequiredService<ClientGrainContext>(),
                    this,
                    ServiceProvider.GetRequiredService<DeepCopier>(),
                    messagingTrace,
                    ServiceProvider.GetRequiredService<DeepCopier<Response>>(),
                    _interfaceToImplementationMapping,
                    loggerFactory.CreateLogger<ClientGrainContext>());

                this.callbackTimerTask = Task.Run(MonitorCallbackExpiry);

                this.GrainReferenceRuntime = this.ServiceProvider.GetRequiredService<IGrainReferenceRuntime>();

                // Client init / sign-on message
                LogStartingClient(logger, RuntimeVersion.Current, _localClientDetails.ClientAddress, _localClientDetails.ClientId);

                if (TestOnlyThrowExceptionDuringInit)
                {
                    throw new InvalidOperationException("TestOnlyThrowExceptionDuringInit");
                }
            }
            catch (Exception exc)
            {
                LogConstructorException(logger, exc);
                ConstructorReset();
                throw;
            }
        }

        public IServiceProvider ServiceProvider { get; private set; }

        public TimeProvider TimeProvider { get; }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            // Deliberately avoid capturing the current synchronization context during startup and execute on the default scheduler.
            // This helps to avoid any issues (such as deadlocks) caused by executing with the client's synchronization context/scheduler.
            await Task.Run(() => this.StartInternal(cancellationToken)).ConfigureAwait(false);

            LogStartedClient(logger, CurrentActivationAddress, _localClientDetails.ClientId);
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            this.callbackTimer.Dispose();

            if (this.callbackTimerTask is { } task)
            {
                await task.WaitAsync(cancellationToken);
            }

            if (MessageCenter is { } messageCenter)
            {
                await messageCenter.StopAsync(cancellationToken);
            }

            if (_manifestProvider is { } provider)
            {
                await provider.StopAsync(cancellationToken);
            }

            ConstructorReset();
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

            MessageCenter = ActivatorUtilities.CreateInstance<ClientMessageCenter>(this.ServiceProvider);
            MessageCenter.RegisterLocalMessageHandler(this.HandleMessage);
            await ExecuteWithRetries(
                async () => await MessageCenter.StartAsync(cancellationToken),
                retryFilter,
                cancellationToken);
            CurrentActivationAddress = GrainAddress.NewActivationAddress(MessageCenter.MyAddress, _localClientDetails.ClientId.GrainId);

            this.gatewayObserver = new ClientGatewayObserver(gatewayManager);
            this.InternalGrainFactory.CreateObjectReference<IClientGatewayObserver>(this.gatewayObserver);

            await ExecuteWithRetries(
                _manifestProvider.StartAsync,
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
                    LogMessageNotSupported(logger, message);
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
            var cancellationToken = request.GetCancellationToken();
            cancellationToken.ThrowIfCancellationRequested();
            var message = this.messageFactory.CreateMessage(request, options);
            OrleansOutsideRuntimeClientEvent.Log.SendRequest(message);

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

            if (this.clientMessagingOptions.DropExpiredMessages && message.IsExpirableMessage())
            {
                // don't set expiration for system target messages.
                var ttl = request.GetDefaultResponseTimeout() ?? this.clientMessagingOptions.ResponseTimeout;
                message.TimeToLive = ttl;
            }

            if (!oneWay)
            {
                var callbackData = new CallbackData(this.sharedCallbackData, context, message);
                callbackData.SubscribeForCancellation(cancellationToken);
                callbacks.TryAdd(message.Id, callbackData);
            }
            else
            {
                context?.Complete();
            }

            LogSendingMessage(logger, message);
            MessageCenter.SendMessage(message);
        }

        public void ReceiveResponse(Message response)
        {
            OrleansOutsideRuntimeClientEvent.Log.ReceiveResponse(response);

            LogReceivedMessage(logger, response);

            if (response.Result is Message.ResponseTypes.Status)
            {
                var status = (StatusResponse)response.BodyObject;
                callbacks.TryGetValue(response.Id, out var callback);
                var request = callback?.Message;
                if (request is not null)
                {
                    callback.OnStatusUpdate(status);
                    if (status.Diagnostics != null && status.Diagnostics.Count > 0)
                    {
                        LogReceivedStatusUpdateForPendingRequest(logger, request, new(status.Diagnostics));
                    }
                }
                else
                {
                    if (clientMessagingOptions.CancelRequestOnTimeout)
                    {
                        // Cancel the call since the caller has abandoned it.
                        // Note that the target and sender arguments are swapped because this is a response to the original request.
                        _cancellationManager?.SignalCancellation(
                            response.SendingSilo,
                            targetGrainId: response.SendingGrain,
                            sendingGrainId: response.TargetGrain,
                            messageId: response.Id);
                    }

                    if (status.Diagnostics != null && status.Diagnostics.Count > 0)
                    {
                        LogReceivedStatusUpdateForUnknownRequest(logger, response, new(status.Diagnostics));
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
                LogWarningNoCallbackForResponseMessage(logger, response);
            }
        }

        private void UnregisterCallback(CorrelationId id)
        {
            callbacks.TryRemove(id, out _);
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
                ? clientObserver.GetObserverGrainId(_localClientDetails.ClientId)
                : ObserverGrainId.Create(_localClientDetails.ClientId);
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

            Utils.SafeExecute(() => this.callbackTimer.Dispose());

            Utils.SafeExecute(() => MessageCenter?.Dispose());

            GC.SuppressFinalize(this);
            disposed = true;
        }

        public void BreakOutstandingMessagesToSilo(SiloAddress deadSilo)
        {
            foreach (var callback in callbacks)
            {
                if (deadSilo.Equals(callback.Value.Message.TargetSilo))
                {
                    callback.Value.OnTargetSiloFail();
                }
            }
        }

        public int GetRunningRequestsCount(GrainInterfaceType grainInterfaceType)
            => this.callbacks.Count(c => c.Value.Message.InterfaceType == grainInterfaceType);

        /// <inheritdoc />
        public void NotifyClusterConnectionLost()
        {
            foreach (var observer in _statusObservers)
            {
                try
                {
                    observer.NotifyClusterConnectionLost();
                }
                catch (Exception ex)
                {
                    LogErrorSendingClusterDisconnectionNotification(logger, ex);
                }
            }
        }

        /// <inheritdoc />
        public void NotifyGatewayCountChanged(int currentNumberOfGateways, int previousNumberOfGateways)
        {
            foreach (var observer in _statusObservers)
            {
                try
                {
                    observer.NotifyGatewayCountChanged(
                        currentNumberOfGateways,
                        previousNumberOfGateways,
                        currentNumberOfGateways > 0 && previousNumberOfGateways <= 0);
                }
                catch (Exception ex)
                {
                    LogErrorSendingGatewayCountChangedNotification(logger, ex);
                }
            }
        }

        private async Task MonitorCallbackExpiry()
        {
            while (await callbackTimer.WaitForNextTickAsync())
            {
                try
                {
                    var currentStopwatchTicks = ValueStopwatch.GetTimestamp();
                    foreach (var (_, callback) in callbacks)
                    {
                        if (callback.IsCompleted)
                        {
                            continue;
                        }

                        if (callback.IsExpired(currentStopwatchTicks))
                        {
                            callback.OnTimeout();
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogErrorWhileProcessingCallbackExpiry(logger, ex);
                }
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

        [LoggerMessage(
            Level = LogLevel.Information,
            EventId = (int)ErrorCode.ClientStarting,
            Message = "Starting Orleans client with runtime version '{RuntimeVersion}', local address '{LocalAddress}' and client id '{ClientId}'."
        )]
        private static partial void LogStartingClient(ILogger logger, string runtimeVersion, SiloAddress localAddress, ClientGrainId clientId);

        [LoggerMessage(
            Level = LogLevel.Error,
            EventId = (int)ErrorCode.Runtime_Error_100319,
            Message = "OutsideRuntimeClient constructor failed."
        )]
        private static partial void LogConstructorException(ILogger logger, Exception exc);

        [LoggerMessage(
            Level = LogLevel.Information,
            EventId = (int)ErrorCode.ProxyClient_StartDone,
            Message = "Started client with address '{ActivationAddress}' and id '{ClientId}'."
        )]
        private static partial void LogStartedClient(ILogger logger, GrainAddress activationAddress, ClientGrainId clientId);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Send '{Message}'."
        )]
        private static partial void LogSendingMessage(ILogger logger, Message message);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Received '{Message}'."
        )]
        private static partial void LogReceivedMessage(ILogger logger, Message message);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Message not supported: '{Message}'."
        )]
        private static partial void LogMessageNotSupported(ILogger logger, Message message);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Error sending cluster disconnection notification."
        )]
        private static partial void LogErrorSendingClusterDisconnectionNotification(ILogger logger, Exception ex);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Error sending gateway count changed notification."
        )]
        private static partial void LogErrorSendingGatewayCountChangedNotification(ILogger logger, Exception ex);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Error while processing callback expiry."
        )]
        private static partial void LogErrorWhileProcessingCallbackExpiry(ILogger logger, Exception ex);

        [LoggerMessage(
            Level = LogLevel.Warning,
            EventId = (int)ErrorCode.Runtime_Error_100011,
            Message = "No callback for response message '{ResponseMessage}'"
        )]
        private static partial void LogWarningNoCallbackForResponseMessage(ILogger logger, Message responseMessage);

        private readonly struct DiagnosticsLogData(List<string> diagnostics)
        {
            public override string ToString() => string.Join("\n", diagnostics);
        }

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Received status update for pending request, Request: '{RequestMessage}'. Status: '{Diagnostics}'."
        )]
        private static partial void LogReceivedStatusUpdateForPendingRequest(ILogger logger, Message requestMessage, DiagnosticsLogData diagnostics);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Received status update for unknown request. Message: '{StatusMessage}'. Status: '{Diagnostics}'."
        )]
        private static partial void LogReceivedStatusUpdateForUnknownRequest(ILogger logger, Message statusMessage, DiagnosticsLogData diagnostics);

    }
}