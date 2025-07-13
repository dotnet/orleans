using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.CodeGeneration;
using Orleans.Configuration;
using Orleans.GrainReferences;
using Orleans.Metadata;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Messaging;
using Orleans.Serialization;
using Orleans.Serialization.Invocation;
using Orleans.Storage;
using static Orleans.Internal.StandardExtensions;

namespace Orleans.Runtime
{
    /// <summary>
    /// Internal class for system grains to get access to runtime object
    /// </summary>
    internal sealed partial class InsideRuntimeClient : IRuntimeClient, ILifecycleParticipant<ISiloLifecycle>
    {
        private readonly ILogger logger;
        private readonly ILogger invokeExceptionLogger;
        private readonly ILoggerFactory loggerFactory;
        private readonly SiloMessagingOptions messagingOptions;
        private readonly ConcurrentDictionary<(GrainId, CorrelationId), CallbackData> callbacks;
        private readonly InterfaceToImplementationMappingCache interfaceToImplementationMapping;
        private readonly SharedCallbackData sharedCallbackData;
        private readonly SharedCallbackData systemSharedCallbackData;
        private readonly PeriodicTimer callbackTimer;

        private GrainLocator grainLocator;
        private MessageCenter messageCenter;
        private List<IIncomingGrainCallFilter> grainCallFilters;
        private readonly DeepCopier _deepCopier;
        private IGrainCallCancellationManager _cancellationManager;
        private HostedClient hostedClient;

        private HostedClient HostedClient => this.hostedClient ??= this.ServiceProvider.GetRequiredService<HostedClient>();
        private readonly MessageFactory messageFactory;
        private IGrainReferenceRuntime grainReferenceRuntime;
        private Task callbackTimerTask;
        private readonly MessagingTrace messagingTrace;
        private readonly DeepCopier<Response> responseCopier;

        public InsideRuntimeClient(
            ILocalSiloDetails siloDetails,
            IServiceProvider serviceProvider,
            MessageFactory messageFactory,
            ILoggerFactory loggerFactory,
            IOptions<SiloMessagingOptions> messagingOptions,
            MessagingTrace messagingTrace,
            GrainReferenceActivator referenceActivator,
            GrainInterfaceTypeResolver interfaceIdResolver,
            GrainInterfaceTypeToGrainTypeResolver interfaceToTypeResolver,
            DeepCopier deepCopier,
            TimeProvider timeProvider,
            InterfaceToImplementationMappingCache interfaceToImplementationMapping)
        {
            TimeProvider = timeProvider;
            this.interfaceToImplementationMapping = interfaceToImplementationMapping;
            this._deepCopier = deepCopier;
            this.ServiceProvider = serviceProvider;
            this.MySilo = siloDetails.SiloAddress;
            this.callbacks = new ConcurrentDictionary<(GrainId, CorrelationId), CallbackData>();
            this.messageFactory = messageFactory;
            this.ConcreteGrainFactory = new GrainFactory(this, referenceActivator, interfaceIdResolver, interfaceToTypeResolver);
            this.logger = loggerFactory.CreateLogger<InsideRuntimeClient>();
            this.invokeExceptionLogger = loggerFactory.CreateLogger($"{typeof(Grain).FullName}.InvokeException");
            this.loggerFactory = loggerFactory;
            this.messagingOptions = messagingOptions.Value;
            this.messagingTrace = messagingTrace;
            this.responseCopier = deepCopier.GetCopier<Response>();
            var period = Max(TimeSpan.FromMilliseconds(1), Min(this.messagingOptions.ResponseTimeout, TimeSpan.FromSeconds(1)));
            this.callbackTimer = new PeriodicTimer(period, timeProvider);

            var callbackDataLogger = loggerFactory.CreateLogger<CallbackData>();
            this.sharedCallbackData = new SharedCallbackData(
                msg => this.UnregisterCallback(msg.SendingGrain, msg.Id),
                callbackDataLogger,
                this.messagingOptions.ResponseTimeout,
                this.messagingOptions.CancelRequestOnTimeout,
                this.messagingOptions.WaitForCancellationAcknowledgement,
                cancellationManager: null);

            this.systemSharedCallbackData = new SharedCallbackData(
                msg => this.UnregisterCallback(msg.SendingGrain, msg.Id),
                callbackDataLogger,
                this.messagingOptions.SystemResponseTimeout,
                cancelOnTimeout: false,
                waitForCancellationAcknowledgement: false,
                cancellationManager: null);
        }

        public IServiceProvider ServiceProvider { get; }

        public IInternalGrainFactory InternalGrainFactory => this.ConcreteGrainFactory;

        private SiloAddress MySilo { get; }

        public GrainFactory ConcreteGrainFactory { get; }

        private GrainLocator GrainLocator
            => this.grainLocator ?? (this.grainLocator = this.ServiceProvider.GetRequiredService<GrainLocator>());

        private List<IIncomingGrainCallFilter> GrainCallFilters
            => this.grainCallFilters ??= new List<IIncomingGrainCallFilter>(this.ServiceProvider.GetServices<IIncomingGrainCallFilter>());

        private MessageCenter MessageCenter => this.messageCenter ?? (this.messageCenter = this.ServiceProvider.GetRequiredService<MessageCenter>());

        public IGrainReferenceRuntime GrainReferenceRuntime => this.grainReferenceRuntime ?? (this.grainReferenceRuntime = this.ServiceProvider.GetRequiredService<IGrainReferenceRuntime>());

        public void SendRequest(
            GrainReference target,
            IInvokable request,
            IResponseCompletionSource context,
            InvokeMethodOptions options)
        {
            var cancellationToken = request.GetCancellationToken();
            cancellationToken.ThrowIfCancellationRequested();

            var message = this.messageFactory.CreateMessage(request, options);
            message.InterfaceType = target.InterfaceType;
            message.InterfaceVersion = target.InterfaceVersion;

            // fill in sender
            if (message.SendingSilo == null)
                message.SendingSilo = MySilo;

            IGrainContext sendingActivation = RuntimeContext.Current;

            if (sendingActivation == null)
            {
                var clientAddress = this.HostedClient.Address;
                message.SendingGrain = clientAddress.GrainId;
            }
            else
            {
                message.SendingGrain = sendingActivation.GrainId;
            }

            // fill in destination
            var targetGrainId = target.GrainId;
            message.TargetGrain = targetGrainId;
            SharedCallbackData sharedData;
            if (SystemTargetGrainId.TryParse(targetGrainId, out var systemTargetGrainId))
            {
                message.TargetSilo = systemTargetGrainId.GetSiloAddress();
                message.IsSystemMessage = true;
                sharedData = this.systemSharedCallbackData;
            }
            else
            {
                sharedData = this.sharedCallbackData;
            }

            if (this.messagingOptions.DropExpiredMessages && message.IsExpirableMessage())
            {
                message.TimeToLive = request.GetDefaultResponseTimeout() ?? sharedData.ResponseTimeout;
            }

            var oneWay = (options & InvokeMethodOptions.OneWay) != 0;
            if (!oneWay)
            {
                Debug.Assert(context is not null);

                // Register a callback for the request.
                var callbackData = new CallbackData(sharedData, context, message);
                callbacks.TryAdd((message.SendingGrain, message.Id), callbackData);
                callbackData.SubscribeForCancellation(cancellationToken);
            }
            else
            {
                context?.Complete();
            }

            this.messagingTrace.OnSendRequest(message);
            this.MessageCenter.AddressAndSendMessage(message);
        }

        public void SendResponse(Message request, Response response)
        {
            OrleansInsideRuntimeClientEvent.Log.SendResponse(request);

            // Don't process messages that have already timed out
            if (request.IsExpired)
            {
                this.messagingTrace.OnDropExpiredMessage(request, MessagingInstruments.Phase.Respond);
                return;
            }

            this.MessageCenter.SendResponse(request, response);
        }

        /// <summary>
        /// UnRegister a callback.
        /// </summary>
        private void UnregisterCallback(GrainId grainId, CorrelationId correlationId)
        {
            callbacks.TryRemove((grainId, correlationId), out _);
        }

        public void SniffIncomingMessage(Message message)
        {
            try
            {
                if (message.CacheInvalidationHeader != null)
                {
                    foreach (var update in message.CacheInvalidationHeader)
                    {
                        GrainLocator.UpdateCache(update);
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
                LogWarningSniffIncomingMessage(this.logger, exc);
            }
        }

        public async Task Invoke(IGrainContext target, Message message)
        {
            try
            {
                // Don't process messages that have already timed out
                if (message.IsExpired)
                {
                    this.messagingTrace.OnDropExpiredMessage(message, MessagingInstruments.Phase.Invoke);
                    return;
                }

                if (message.RequestContextData is { Count: > 0 })
                {
                    RequestContextExtensions.Import(message.RequestContextData);
                }

                Response response;
                try
                {
                    switch (message.BodyObject)
                    {
                        case IInvokable invokable:
                            {
                                invokable.SetTarget(target);

                                CancellationSourcesExtension.RegisterCancellationTokens(target, invokable);
                                if (GrainCallFilters is { Count: > 0 } || target.GrainInstance is IIncomingGrainCallFilter)
                                {
                                    var invoker = new GrainMethodInvoker(message, target, invokable, GrainCallFilters, this.interfaceToImplementationMapping, this.responseCopier);
                                    await invoker.Invoke();
                                    response = invoker.Response;
                                }
                                else
                                {
                                    response = await invokable.Invoke();
                                    response = this.responseCopier.Copy(response);
                                }

                                invokable.Dispose();
                                break;
                            }
                        default:
                            throw new NotSupportedException($"Request {message.BodyObject} of type {message.BodyObject?.GetType()} is not supported");
                    }
                }
                catch (Exception exc1)
                {
                    response = Response.FromException(exc1);
                }

                if (response.Exception is { } invocationException)
                {
                    LogGrainInvokeException(this.invokeExceptionLogger, message.Direction != Message.Directions.OneWay ? LogLevel.Debug : LogLevel.Warning, invocationException, message);

                    // If a grain allowed an inconsistent state exception to escape and the exception originated from
                    // this activation, then deactivate it.
                    if (invocationException is InconsistentStateException ise && ise.IsSourceActivation)
                    {
                        // Mark the exception so that it doesn't deactivate any other activations.
                        ise.IsSourceActivation = false;

                        LogDeactivatingInconsistentState(this.invokeExceptionLogger, target, invocationException);
                        target.Deactivate(new DeactivationReason(DeactivationReasonCode.ApplicationError, LogFormatter.PrintException(invocationException)));
                    }
                }

                if (message.Direction != Message.Directions.OneWay)
                {
                    SafeSendResponse(message, response);
                }

                return;
            }
            catch (Exception exc2)
            {
                LogWarningInvokeException(this.logger, exc2, message);

                if (message.Direction != Message.Directions.OneWay)
                {
                    SafeSendExceptionResponse(message, exc2);
                }
            }
        }

        private void SafeSendResponse(Message message, Response response)
        {
            try
            {
                SendResponse(message, (Response)this._deepCopier.Copy(response));
            }
            catch (Exception exc)
            {
                LogWarningResponseFailed(this.logger, exc);
                SendResponse(message, Response.FromException(exc));
            }
        }

        private void SafeSendExceptionResponse(Message message, Exception ex)
        {
            try
            {
                SendResponse(message, Response.FromException(ex));
            }
            catch (Exception exc1)
            {
                try
                {
                    LogWarningSendExceptionResponseFailed(this.logger, exc1);
                    SendResponse(message, Response.FromException(exc1));
                }
                catch (Exception exc2)
                {
                    LogWarningUnhandledExceptionInInvoke(this.logger, exc2);
                }
            }
        }

        public void ReceiveResponse(Message message)
        {
            OrleansInsideRuntimeClientEvent.Log.ReceiveResponse(message);
            if (message.Result is Message.ResponseTypes.Rejection)
            {
                if (!message.TargetSilo.Matches(this.MySilo))
                {
                    // gatewayed message - gateway back to sender
                    LogTraceNoCallbackForRejection(this.logger, message);
                    this.MessageCenter.AddressAndSendMessage(message);
                    return;
                }

                LogHandleMessage(this.logger, message);
                var rejection = (RejectionResponse)message.BodyObject;
                switch (rejection.RejectionType)
                {
                    case Message.RejectionTypes.Overloaded:
                        break;
                    case Message.RejectionTypes.Unrecoverable:
                    // Fall through & reroute
                    case Message.RejectionTypes.Transient:
                        if (message.CacheInvalidationHeader is null)
                        {
                            // Remove from local directory cache. Note that SendingGrain is the original target, since message is the rejection response.
                            // If CacheInvalidationHeader is present, we already did this. Otherwise, we left this code for backward compatibility.
                            // It should be retired as we move to use CacheMgmtHeader in all relevant places.
                            this.GrainLocator.InvalidateCache(message.SendingGrain);
                        }
                        break;
                    case Message.RejectionTypes.CacheInvalidation when message.HasCacheInvalidationHeader:
                        // The message targeted an invalid (eg, defunct) activation and this response serves only to invalidate this silo's activation cache.
                        return;
                    default:
                        LogErrorUnsupportedRejectionType(this.logger, rejection.RejectionType);
                        break;
                }
            }
            else if (message.Result == Message.ResponseTypes.Status)
            {
                var status = (StatusResponse)message.BodyObject;
                callbacks.TryGetValue((message.TargetGrain, message.Id), out var callback);
                var request = callback?.Message;
                if (request is not null)
                {
                    callback.OnStatusUpdate(status);
                    if (status.Diagnostics != null && status.Diagnostics.Count > 0)
                    {
                        LogInformationReceivedStatusUpdate(this.logger, request, status.Diagnostics);
                    }
                }
                else
                {
                    if (messagingOptions.CancelRequestOnTimeout)
                    {
                        // Cancel the call since the caller has abandoned it.
                        // Note that the target and sender arguments are swapped because this is a response to the original request.
                        _cancellationManager.SignalCancellation(
                            message.SendingSilo,
                            targetGrainId: message.SendingGrain,
                            sendingGrainId: message.TargetGrain,
                            messageId: message.Id);
                    }

                    if (status.Diagnostics != null && status.Diagnostics.Count > 0 && logger.IsEnabled(LogLevel.Debug))
                    {
                        var diagnosticsString = string.Join("\n", status.Diagnostics);
                        this.logger.LogDebug("Received status update for unknown request. Message: {StatusMessage}. Status: {Diagnostics}", message, diagnosticsString);
                    }
                }

                return;
            }

            CallbackData callbackData;
            bool found = callbacks.TryRemove((message.TargetGrain, message.Id), out callbackData);
            if (found)
            {
                // IMPORTANT: we do not schedule the response callback via the scheduler, since the only thing it does
                // is to resolve/break the resolver. The continuations/waits that are based on this resolution will be scheduled as work items.
                callbackData.DoCallback(message);
            }
            else
            {
                LogDebugNoCallbackForResponse(this.logger, message);
            }
        }

        public string CurrentActivationIdentity => RuntimeContext.Current?.Address.ToString() ?? this.HostedClient.ToString();

        public TimeProvider TimeProvider { get; }

        /// <inheritdoc />
        public TimeSpan GetResponseTimeout() => this.sharedCallbackData.ResponseTimeout;

        /// <inheritdoc />
        public void SetResponseTimeout(TimeSpan timeout) => this.sharedCallbackData.ResponseTimeout = timeout;

        public IAddressable CreateObjectReference(IAddressable obj)
        {
            if (RuntimeContext.Current is null) return this.HostedClient.CreateObjectReference(obj);
            throw new InvalidOperationException("Cannot create a local object reference from a grain.");
        }

        public void DeleteObjectReference(IAddressable obj)
        {
            if (RuntimeContext.Current is null)
            {
                this.HostedClient.DeleteObjectReference(obj);
            }
            else
            {
                throw new InvalidOperationException("Cannot delete a local object reference from a grain.");
            }
        }

        private async Task OnRuntimeInitializeStop(CancellationToken tc)
        {
            this.callbackTimer.Dispose();
            if (this.callbackTimerTask is { } task)
            {
                await task.WaitAsync(tc);
            }
        }

        private Task OnRuntimeInitializeStart(CancellationToken tc)
        {
            var stopWatch = ValueStopwatch.StartNew();
            this.callbackTimerTask = Task.Run(MonitorCallbackExpiry);

            LogDebugSiloStartPerfMeasure(this.logger, new(stopWatch));

            return Task.CompletedTask;
        }

        private readonly struct ValueStopwatchLogValue(ValueStopwatch stopWatch)
        {
            override public string ToString()
            {
                stopWatch.Stop();
                return stopWatch.Elapsed.ToString();
            }
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

        public void Participate(ISiloLifecycle lifecycle)
        {
            _cancellationManager = this.ServiceProvider.GetRequiredService<IGrainCallCancellationManager>();
            sharedCallbackData.CancellationManager = _cancellationManager;
            lifecycle.Subscribe<InsideRuntimeClient>(ServiceLifecycleStage.RuntimeInitialize, OnRuntimeInitializeStart, OnRuntimeInitializeStop);
        }

        public int GetRunningRequestsCount(GrainInterfaceType grainInterfaceType)
            => this.callbacks.Count(c => c.Value.Message.InterfaceType == grainInterfaceType);

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
                    LogWarningWhileProcessingCallbackExpiry(this.logger, ex);
                }
            }
        }

        [LoggerMessage(
            Level = LogLevel.Warning,
            EventId = (int)ErrorCode.IGC_SniffIncomingMessage_Exc,
            Message = "SniffIncomingMessage has thrown exception. Ignoring.")]
        private static partial void LogWarningSniffIncomingMessage(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = (int)ErrorCode.GrainInvokeException,
            Message = "Exception during Grain method call of message {Message}: ")]
        private static partial void LogGrainInvokeException(ILogger logger, LogLevel level, Exception exception, Message message);

        [LoggerMessage(
            Level = LogLevel.Warning,
            EventId = (int)ErrorCode.Runtime_Error_100329,
            Message = "Exception during Invoke of message {Message}")]
        private static partial void LogWarningInvokeException(ILogger logger, Exception exception, Message message);

        [LoggerMessage(
            Level = LogLevel.Warning,
            EventId = (int)ErrorCode.IGC_SendResponseFailed,
            Message = "Exception trying to send a response")]
        private static partial void LogWarningResponseFailed(ILogger logger, Exception exception);

        [LoggerMessage(
            Level = LogLevel.Warning,
            EventId = (int)ErrorCode.IGC_SendExceptionResponseFailed,
            Message = "Exception trying to send an exception response")]
        private static partial void LogWarningSendExceptionResponseFailed(ILogger logger, Exception exception);

        [LoggerMessage(
            Level = LogLevel.Warning,
            EventId = (int)ErrorCode.IGC_UnhandledExceptionInInvoke,
            Message = "Exception trying to send an exception. Ignoring and not trying to send again.")]
        private static partial void LogWarningUnhandledExceptionInInvoke(ILogger logger, Exception exception);

        [LoggerMessage(
            Level = LogLevel.Trace,
            EventId = (int)ErrorCode.Dispatcher_NoCallbackForRejectionResp,
            Message = "No callback for rejection response message: {Message}")]
        private static partial void LogTraceNoCallbackForRejection(ILogger logger, Message message);

        [LoggerMessage(
            Level = LogLevel.Debug,
            EventId = (int)ErrorCode.Dispatcher_HandleMsg,
            Message = "HandleMessage {Message}")]
        private static partial void LogHandleMessage(ILogger logger, Message message);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Deactivating {Target} due to inconsistent state.")]
        private static partial void LogDeactivatingInconsistentState(ILogger logger, IGrainContext target, Exception exception);

        [LoggerMessage(
            Level = LogLevel.Error,
            EventId = (int)ErrorCode.Dispatcher_InvalidEnum_RejectionType,
            Message = "Unsupported rejection type: {RejectionType}")]
        private static partial void LogErrorUnsupportedRejectionType(ILogger logger, Message.RejectionTypes rejectionType);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Received status update for pending request, Request: {RequestMessage}. Status: {Diagnostics}")]
        private static partial void LogInformationReceivedStatusUpdate(ILogger logger, Message requestMessage, IEnumerable<string> diagnostics);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Received status update for unknown request. Message: {StatusMessage}. Status: {Diagnostics}")]
        private static partial void LogInformationReceivedStatusUpdateUnknownRequest(ILogger logger, Message statusMessage, IEnumerable<string> diagnostics);

        [LoggerMessage(
            Level = LogLevel.Debug,
            EventId = (int)ErrorCode.Dispatcher_NoCallbackForResp,
            Message = "No callback for response message {Message}")]
        private static partial void LogDebugNoCallbackForResponse(ILogger logger, Message message);

        [LoggerMessage(
            Level = LogLevel.Debug,
            EventId = (int)ErrorCode.SiloStartPerfMeasure,
            Message = "Start InsideRuntimeClient took {ElapsedMs} milliseconds"
        )]
        private static partial void LogDebugSiloStartPerfMeasure(ILogger logger, ValueStopwatchLogValue elapsedMs);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Error while processing callback expiry."
        )]
        private static partial void LogWarningWhileProcessingCallbackExpiry(ILogger logger, Exception exception);
    }
}
