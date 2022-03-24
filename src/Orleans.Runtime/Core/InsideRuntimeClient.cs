using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.CodeGeneration;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Scheduler;
using Orleans.Serialization;
using Orleans.Storage;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Threading;
using Orleans.Configuration;
using Orleans.GrainReferences;
using Orleans.Metadata;
using Orleans.Serialization.Invocation;
using Orleans.Runtime.Messaging;

namespace Orleans.Runtime
{
    /// <summary>
    /// Internal class for system grains to get access to runtime object
    /// </summary>
    internal class InsideRuntimeClient : IRuntimeClient, ILifecycleParticipant<ISiloLifecycle>
    {
        private readonly ILogger logger;
        private readonly ILogger invokeExceptionLogger;
        private readonly ILoggerFactory loggerFactory;
        private readonly SiloMessagingOptions messagingOptions;
        private readonly List<IDisposable> disposables;
        private readonly ConcurrentDictionary<(GrainId, CorrelationId), CallbackData> callbacks;
        private readonly SharedCallbackData sharedCallbackData;
        private readonly SharedCallbackData systemSharedCallbackData;
        private SafeTimer callbackTimer;

        private GrainLocator grainLocator;
        private Catalog catalog;
        private MessageCenter messageCenter;
        private List<IIncomingGrainCallFilter> grainCallFilters;
        private DeepCopier _deepCopier;
        private readonly InterfaceToImplementationMappingCache interfaceToImplementationMapping;
        private HostedClient hostedClient;

        private HostedClient HostedClient => this.hostedClient ?? (this.hostedClient = this.ServiceProvider.GetRequiredService<HostedClient>());
        private readonly MessageFactory messageFactory;
        private IGrainReferenceRuntime grainReferenceRuntime;
        private readonly ApplicationRequestsStatisticsGroup appRequestStatistics;
        private readonly MessagingTrace messagingTrace;
        private readonly DeepCopier<Response> responseCopier;

        public InsideRuntimeClient(
            ILocalSiloDetails siloDetails,
            IServiceProvider serviceProvider,
            MessageFactory messageFactory,
            ILoggerFactory loggerFactory,
            IOptions<SiloMessagingOptions> messagingOptions,
            ApplicationRequestsStatisticsGroup appRequestStatistics,
            MessagingTrace messagingTrace,
            GrainReferenceActivator referenceActivator,
            GrainInterfaceTypeResolver interfaceIdResolver,
            GrainInterfaceTypeToGrainTypeResolver interfaceToTypeResolver,
            DeepCopier deepCopier)
        {
            this.interfaceToImplementationMapping = new InterfaceToImplementationMappingCache();
            this._deepCopier = deepCopier;
            this.ServiceProvider = serviceProvider;
            this.MySilo = siloDetails.SiloAddress;
            this.disposables = new List<IDisposable>();
            this.callbacks = new ConcurrentDictionary<(GrainId, CorrelationId), CallbackData>();
            this.messageFactory = messageFactory;
            this.ConcreteGrainFactory = new GrainFactory(this, referenceActivator, interfaceIdResolver, interfaceToTypeResolver);
            this.logger = loggerFactory.CreateLogger<InsideRuntimeClient>();
            this.invokeExceptionLogger = loggerFactory.CreateLogger($"{typeof(Grain).FullName}.InvokeException");
            this.loggerFactory = loggerFactory;
            this.messagingOptions = messagingOptions.Value;
            this.appRequestStatistics = appRequestStatistics;
            this.messagingTrace = messagingTrace;
            this.responseCopier = deepCopier.GetCopier<Response>();

            this.sharedCallbackData = new SharedCallbackData(
                msg => this.UnregisterCallback(msg.TargetGrain, msg.Id),
                this.loggerFactory.CreateLogger<CallbackData>(),
                this.messagingOptions,
                this.appRequestStatistics,
                this.messagingOptions.ResponseTimeout);

            this.systemSharedCallbackData = new SharedCallbackData(
                msg => this.UnregisterCallback(msg.TargetGrain, msg.Id),
                this.loggerFactory.CreateLogger<CallbackData>(),
                this.messagingOptions,
                this.appRequestStatistics,
                this.messagingOptions.SystemResponseTimeout);
        }

        public IServiceProvider ServiceProvider { get; }

        public IInternalGrainFactory InternalGrainFactory => this.ConcreteGrainFactory;

        private SiloAddress MySilo { get; }

        public GrainFactory ConcreteGrainFactory { get; }
        
        private Catalog Catalog => this.catalog ?? (this.catalog = this.ServiceProvider.GetRequiredService<Catalog>());

        private GrainLocator GrainLocator
            => this.grainLocator ?? (this.grainLocator = this.ServiceProvider.GetRequiredService<GrainLocator>());

        private List<IIncomingGrainCallFilter> GrainCallFilters
            => this.grainCallFilters ?? (this.grainCallFilters = new List<IIncomingGrainCallFilter>(this.ServiceProvider.GetServices<IIncomingGrainCallFilter>()));

        private MessageCenter MessageCenter => this.messageCenter ?? (this.messageCenter = this.ServiceProvider.GetRequiredService<MessageCenter>());

        public IGrainReferenceRuntime GrainReferenceRuntime => this.grainReferenceRuntime ?? (this.grainReferenceRuntime = this.ServiceProvider.GetRequiredService<IGrainReferenceRuntime>());

        public void SendRequest(
            GrainReference target,
            IInvokable request,
            IResponseCompletionSource context,
            InvokeMethodOptions options)
        {
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
                message.SendingActivation = clientAddress.ActivationId;
            }
            else
            {
                message.SendingActivation = sendingActivation.ActivationId;
                message.SendingGrain = sendingActivation.GrainId;
            }

            // fill in destination
            var targetGrainId = target.GrainId;
            message.TargetGrain = targetGrainId;
            SharedCallbackData sharedData;
            if (SystemTargetGrainId.TryParse(targetGrainId, out var systemTargetGrainId))
            {
                message.TargetSilo = systemTargetGrainId.GetSiloAddress();
                message.TargetActivation = ActivationId.GetDeterministic(targetGrainId);
                message.Category = targetGrainId.Type.Equals(Constants.MembershipServiceType) ?
                    Message.Categories.Ping : Message.Categories.System;
                sharedData = this.systemSharedCallbackData;
            }
            else
            {
                sharedData = this.sharedCallbackData;
            }

            var oneWay = (options & InvokeMethodOptions.OneWay) != 0;
            if (context is null && !oneWay)
            {
                this.logger.Warn(ErrorCode.IGC_SendRequest_NullContext, "Null context {0}: {1}", message, Utils.GetStackTrace());
            }

            if (message.IsExpirableMessage(this.messagingOptions.DropExpiredMessages))
            {
                message.TimeToLive = sharedData.ResponseTimeout;
            }

            if (!oneWay)
            {
                var callbackData = new CallbackData(sharedData, context, message);
                callbacks.TryAdd((message.SendingGrain, message.Id), callbackData);
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
                this.messagingTrace.OnDropExpiredMessage(request, MessagingStatisticsGroup.Phase.Respond);
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
                    foreach (GrainAddress address in message.CacheInvalidationHeader)
                    {
                        GrainLocator.InvalidateCache(address);
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
                this.logger.Warn(ErrorCode.IGC_SniffIncomingMessage_Exc, "SniffIncomingMessage has thrown exception. Ignoring.", exc);
            }
        }

        public async Task Invoke(IGrainContext target, Message message)
        {
            try
            {
                // Don't process messages that have already timed out
                if (message.IsExpired)
                {
                    this.messagingTrace.OnDropExpiredMessage(message, MessagingStatisticsGroup.Phase.Invoke);
                    return;
                }

                RequestContextExtensions.Import(message.RequestContextData);

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
                    if (message.Direction == Message.Directions.OneWay)
                    {
                        this.invokeExceptionLogger.Warn(ErrorCode.GrainInvokeException,
                            "Exception during Grain method call of message: " + message + ": " + LogFormatter.PrintException(invocationException), invocationException);
                    }
                    else if (invokeExceptionLogger.IsEnabled(LogLevel.Debug))
                    {
                        this.invokeExceptionLogger.Debug(ErrorCode.GrainInvokeException,
                            "Exception during Grain method call of message: " + message + ": " + LogFormatter.PrintException(invocationException), invocationException);
                    }

                    // If a grain allowed an inconsistent state exception to escape and the exception originated from
                    // this activation, then deactivate it.
                    if (invocationException is InconsistentStateException ise && ise.IsSourceActivation)
                    {
                        // Mark the exception so that it doesn't deactivate any other activations.
                        ise.IsSourceActivation = false;

                        var msg = $"Deactivating {target} due to inconsistent state.";
                        this.invokeExceptionLogger.Info(msg);
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
                this.logger.Warn(ErrorCode.Runtime_Error_100329, "Exception during Invoke of message: " + message, exc2);

                if (message.Direction != Message.Directions.OneWay)
                {
                    SafeSendExceptionResponse(message, exc2);
                }
            }
            finally
            {
                RequestContext.Clear();
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
                this.logger.Warn(ErrorCode.IGC_SendResponseFailed,
                    "Exception trying to send a response: " + exc.Message, exc);
                SendResponse(message, Response.FromException(exc));
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
                typeof(InsideRuntimeClient).Module,
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
                var copiedException = PrepareForRemoting((Exception)this._deepCopier.Copy(ex));
                SendResponse(message, Response.FromException(copiedException));
            }
            catch (Exception exc1)
            {
                try
                {
                    this.logger.Warn(ErrorCode.IGC_SendExceptionResponseFailed,
                        "Exception trying to send an exception response: " + exc1.Message, exc1);
                    SendResponse(message, Response.FromException(exc1));
                }
                catch (Exception exc2)
                {
                    this.logger.Warn(ErrorCode.IGC_UnhandledExceptionInInvoke,
                        "Exception trying to send an exception. Ignoring and not trying to send again. Exc: " + exc2.Message, exc2);
                }
            }
        }

        public void ReceiveResponse(Message message)
        {
            OrleansInsideRuntimeClientEvent.Log.ReceiveResponse(message);
            if (message.Result == Message.ResponseTypes.Rejection)
            {
                if (!message.TargetSilo.Matches(this.MySilo))
                {
                    // gatewayed message - gateway back to sender
                    if (logger.IsEnabled(LogLevel.Trace)) this.logger.Trace(ErrorCode.Dispatcher_NoCallbackForRejectionResp, "No callback for rejection response message: {0}", message);
                    this.MessageCenter.AddressAndSendMessage(message);
                    return;
                }

                if (logger.IsEnabled(LogLevel.Debug)) this.logger.Debug(ErrorCode.Dispatcher_HandleMsg, "HandleMessage {0}", message);
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
                            // If CacheInvalidationHeader is present, we already did this. Otherwise, we left this code for backward compatability. 
                            // It should be retired as we move to use CacheMgmtHeader in all relevant places.
                            this.GrainLocator.InvalidateCache(message.SendingAddress);
                        }
                        break;

                    case Message.RejectionTypes.CacheInvalidation when message.HasCacheInvalidationHeader:
                        // The message targeted an invalid (eg, defunct) activation and this response serves only to invalidate this silo's activation cache.
                        return;
                    default:
                        this.logger.Error(ErrorCode.Dispatcher_InvalidEnum_RejectionType,
                            "Missing enum in switch: " + message.RejectionType);
                        break;
                }
            }
            else if (message.Result == Message.ResponseTypes.Status)
            {
                var status = (StatusResponse)message.BodyObject;
                callbacks.TryGetValue((message.TargetGrain, message.Id), out var callback);
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
                        this.logger.LogInformation("Received status update for unknown request. Message: {StatusMessage}. Status: {Diagnostics}", message, diagnosticsString);
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
                if (logger.IsEnabled(LogLevel.Debug)) this.logger.Debug(ErrorCode.Dispatcher_NoCallbackForResp,
                    "No callback for response message: " + message);
            }
        }

        public string CurrentActivationIdentity => RuntimeContext.Current?.Address.ToString() ?? this.HostedClient.ToString();

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

        private Task OnRuntimeInitializeStop(CancellationToken tc)
        {
            lock (disposables)
            {
                foreach (var disposable in disposables)
                {
                    try
                    {
                        disposable?.Dispose();
                    }
                    catch (Exception e)
                    {
                        this.logger.Warn(ErrorCode.IGC_DisposeError, "Exception while disposing: " + e.Message, e);
                    }
                }
            }
            return Task.CompletedTask;
        }

        private Task OnRuntimeInitializeStart(CancellationToken tc)
        {
            var stopWatch = Stopwatch.StartNew();
            var timerLogger = this.loggerFactory.CreateLogger<SafeTimer>();
            var minTicks = Math.Min(this.messagingOptions.ResponseTimeout.Ticks, TimeSpan.FromSeconds(1).Ticks);
            var period = TimeSpan.FromTicks(minTicks);
            this.callbackTimer = new SafeTimer(timerLogger, this.OnCallbackExpiryTick, null, period, period);
            this.disposables.Add(this.callbackTimer);

            stopWatch.Stop();
            this.logger.Info(ErrorCode.SiloStartPerfMeasure, $"Start InsideRuntimeClient took {stopWatch.ElapsedMilliseconds} Milliseconds");
            return Task.CompletedTask;
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

        public void Participate(ISiloLifecycle lifecycle)
        {
            lifecycle.Subscribe<InsideRuntimeClient>(ServiceLifecycleStage.RuntimeInitialize, OnRuntimeInitializeStart, OnRuntimeInitializeStop);
        }

        private void OnCallbackExpiryTick(object state)
        {
            var currentStopwatchTicks = Stopwatch.GetTimestamp();
            var responseTimeout = this.messagingOptions.ResponseTimeout;
            foreach (var pair in callbacks)
            {
                var callback = pair.Value;
                if (callback.IsCompleted) continue;
                if (callback.IsExpired(currentStopwatchTicks)) callback.OnTimeout(responseTimeout);
            }
        }
    }
}
