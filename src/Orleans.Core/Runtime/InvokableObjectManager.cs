#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Invocation;

namespace Orleans
{
    internal sealed partial class InvokableObjectManager : IDisposable
    {
        private readonly CancellationTokenSource disposed = new CancellationTokenSource();
        private readonly ConcurrentDictionary<ObserverGrainId, LocalObjectData> localObjects = new ConcurrentDictionary<ObserverGrainId, LocalObjectData>();

        private readonly InterfaceToImplementationMappingCache _interfaceToImplementationMapping;
        private readonly IGrainContext rootGrainContext;
        private readonly IRuntimeClient runtimeClient;
        private readonly ILogger logger;
        private readonly DeepCopier deepCopier;
        private readonly DeepCopier<Response> _responseCopier;
        private readonly MessagingTrace messagingTrace;
        private List<IIncomingGrainCallFilter>? _grainCallFilters;

        private List<IIncomingGrainCallFilter> GrainCallFilters
            => _grainCallFilters ??= [.. runtimeClient.ServiceProvider.GetServices<IIncomingGrainCallFilter>()];

        public InvokableObjectManager(
            IGrainContext rootGrainContext,
            IRuntimeClient runtimeClient,
            DeepCopier deepCopier,
            MessagingTrace messagingTrace,
            DeepCopier<Response> responseCopier,
            InterfaceToImplementationMappingCache interfaceToImplementationMapping,
            ILogger logger)
        {
            this.rootGrainContext = rootGrainContext;
            this.runtimeClient = runtimeClient;
            this.deepCopier = deepCopier;
            this.messagingTrace = messagingTrace;
            _responseCopier = responseCopier;
            _interfaceToImplementationMapping = interfaceToImplementationMapping;
            this.logger = logger;
        }

        public bool TryRegister(IAddressable obj, ObserverGrainId objectId)
        {
            var result = this.localObjects.TryAdd(objectId, new LocalObjectData(obj, objectId, this));
            if (result)
            {
                LogObserverRegistered(logger, objectId, obj.GetType());
            }
            else
            {
                LogObserverRegistrationFailed(logger, objectId, obj.GetType());
            }
            return result;
        }

        public bool TryDeregister(ObserverGrainId objectId)
        {
            var result = this.localObjects.TryRemove(objectId, out _);
            if (result)
            {
                LogObserverDeregistered(logger, objectId);
            }
            else
            {
                LogObserverDeregistrationFailed(logger, objectId);
            }
            return result;
        }

        public void Dispatch(Message message)
        {
            if (!ObserverGrainId.TryParse(message.TargetGrain, out var observerId))
            {
                LogNotAddressedToAnObserver(logger, message);
                return;
            }

            if (this.localObjects.TryGetValue(observerId, out var objectData))
            {
                objectData.ReceiveMessage(message);
            }
            else
            {
                LogUnexpectedTargetInRequest(logger, message.TargetGrain, message);
            }
        }

        public void Dispose()
        {
            var tokenSource = this.disposed;
            Utils.SafeExecute(() => tokenSource?.Cancel(false));
            Utils.SafeExecute(() => tokenSource?.Dispose());
        }

        public sealed partial class LocalObjectData : IGrainContext, IGrainCallCancellationExtension
        {
            private static readonly Func<object?, Task> HandleFunc = self => ((LocalObjectData)self!).LocalObjectMessagePumpAsync();
            private readonly InvokableObjectManager _manager;
            private readonly HashSet<Message> _runningRequests = [];

            internal LocalObjectData(IAddressable obj, ObserverGrainId observerId, InvokableObjectManager manager)
            {
                this.LocalObject = new WeakReference(obj);
                this.ObserverId = observerId;
                this.Messages = new Queue<Message>();
                this.Running = false;
                _manager = manager;
            }

            internal WeakReference LocalObject { get; }
            internal ObserverGrainId ObserverId { get; }
            internal Queue<Message> Messages { get; }
            internal bool Running { get; set; }

            GrainId IGrainContext.GrainId => this.ObserverId.GrainId;

            GrainReference IGrainContext.GrainReference =>
                _manager.runtimeClient.InternalGrainFactory.GetGrain(ObserverId.GrainId).AsReference();

            object? IGrainContext.GrainInstance => this.LocalObject.Target;

            ActivationId IGrainContext.ActivationId => throw new NotImplementedException();

            GrainAddress IGrainContext.Address => throw new NotImplementedException();

            IServiceProvider IGrainContext.ActivationServices => throw new NotSupportedException();

            IGrainLifecycle IGrainContext.ObservableLifecycle => throw new NotImplementedException();

            public IWorkItemScheduler Scheduler => throw new NotImplementedException();

            void IGrainContext.SetComponent<TComponent>(TComponent? value) where TComponent : class
            {
                if (this.LocalObject.Target is TComponent)
                {
                    throw new ArgumentException("Cannot override a component which is implemented by this grain");
                }

                _manager.rootGrainContext.SetComponent(value);
            }

            public TComponent? GetComponent<TComponent>() where TComponent : class
            {
                if (this.LocalObject.Target is TComponent component)
                {
                    return component;
                }
                else if (this is TComponent thisAsComponent)
                {
                    return thisAsComponent;
                }

                return _manager.rootGrainContext.GetComponent<TComponent>();
            }

            public TTarget? GetTarget<TTarget>() where TTarget : class => this.LocalObject.Target as TTarget;

            bool IEquatable<IGrainContext>.Equals(IGrainContext? other) => ReferenceEquals(this, other);

            public void ReceiveMessage(object msg)
            {
                var message = (Message)msg;
                var obj = this.LocalObject.Target;
                if (obj is null)
                {
                    // Remove from the dictionary record for the garbage collected object? But now we won't be able to detect invalid dispatch IDs anymore.
                    LogObserverGarbageCollected(_manager.logger, this.ObserverId, message);
                    // Try to remove. If it's not there, we don't care.
                    _manager.TryDeregister(this.ObserverId);
                    return;
                }

                // Handle AlwaysInterleave messages (like cancellation requests) immediately without queueing.
                // These messages need to be processed right away, even if another request is currently running.
                if (message.IsAlwaysInterleave)
                {
                    // Track the running request so it can be cancelled.
                    lock (Messages)
                    {
                        _runningRequests.Add(message);
                    }

                    Task.Factory.StartNew(
                        static state =>
                        {
                            var (self, msg) = ((LocalObjectData, Message))state!;
                            return self.ProcessMessageAsync(msg);
                        },
                        (this, message),
                        CancellationToken.None,
                        TaskCreationOptions.DenyChildAttach,
                        TaskScheduler.Default).Ignore();
                    return;
                }

                bool start;
                lock (this.Messages)
                {
                    this.Messages.Enqueue(message);
                    start = !this.Running;
                    this.Running = true;
                }

                LogInvokeLocalObjectAsync(_manager.logger, message, start);

                if (start)
                {
                    // we want to ensure that the message pump operates asynchronously
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
                    // We pass these options to Task.Factory.StartNew as they make the call identical
                    // to Task.Run. See: https://blogs.msdn.microsoft.com/pfxteam/2011/10/24/task-run-vs-task-factory-startnew/
                    Task.Factory.StartNew(
                            HandleFunc,
                            this,
                            CancellationToken.None,
                            TaskCreationOptions.DenyChildAttach,
                            TaskScheduler.Default).Ignore();
                }
            }

            private async Task LocalObjectMessagePumpAsync()
            {
                while (TryDequeueMessage(out var message))
                {
                    await ProcessMessageAsync(message);
                }

                bool TryDequeueMessage([NotNullWhen(true)] out Message? message)
                {
                    lock (Messages)
                    {
                        var result = Messages.TryDequeue(out message);
                        if (!result)
                        {
                            Running = false;
                        }
                        else
                        {
                            _runningRequests.Add(message!);
                        }

                        return result;
                    }
                }
            }

            private async Task ProcessMessageAsync(Message message)
            {
                try
                {
                    if (message.IsExpired)
                    {
                        _manager.messagingTrace.OnDropExpiredMessage(message, MessagingInstruments.Phase.Invoke);
                        return;
                    }

                    if (message.RequestContextData is { Count: > 0 })
                    {
                        RequestContextExtensions.Import(message.RequestContextData);
                    }

                    IInvokable? request;
                    try
                    {
                        if (message.BodyObject is not IInvokable invokableBody)
                        {
                            _manager.runtimeClient.SendResponse(
                                message,
                                Response.FromException(new InvalidOperationException("Message body is not an invokable request")));
                            return;
                        }

                        request = invokableBody;
                    }
                    catch (Exception deserializationException)
                    {
                        LogErrorDeserializingMessageBody(_manager.logger, deserializationException, message);
                        _manager.runtimeClient.SendResponse(message, Response.FromException(deserializationException));
                        return;
                    }

                    try
                    {
                        request.SetTarget(this);
                        var filters = _manager.GrainCallFilters;
                        Response response;
                        if (filters is { Count: > 0 } || LocalObject is IIncomingGrainCallFilter)
                        {
                            var invoker = new GrainMethodInvoker(message, this, request, filters, _manager._interfaceToImplementationMapping, _manager._responseCopier);
                            await invoker.Invoke();
                            response = invoker.Response;
                        }
                        else
                        {
                            response = await request.Invoke();
                            response = _manager._responseCopier.Copy(response);
                        }

                        if (message.Direction != Message.Directions.OneWay)
                        {
                            this.SendResponseAsync(message, response);
                        }
                    }
                    catch (Exception exc)
                    {
                        this.ReportException(message, exc);
                    }
                    finally
                    {
                        // Clear the running request when done.
                        lock (Messages)
                        {
                            _runningRequests.Remove(message);
                        }
                    }
                }
                catch (Exception outerException)
                {
                    // ignore, keep looping.
                    LogErrorInMessagePumpLoop(_manager.logger, outerException);
                }
            }

            private void SendResponseAsync(Message message, Response resultObject)
            {
                if (message.IsExpired)
                {
                    _manager.messagingTrace.OnDropExpiredMessage(message, MessagingInstruments.Phase.Respond);
                    return;
                }

                Response deepCopy;
                try
                {
                    // we're expected to notify the caller if the deep copy failed.
                    deepCopy = _manager.deepCopier.Copy(resultObject);
                }
                catch (Exception exc2)
                {
                    _manager.runtimeClient.SendResponse(message, Response.FromException(exc2));
                    LogErrorSendingResponse(_manager.logger, exc2);
                    return;
                }

                // the deep-copy succeeded.
                _manager.runtimeClient.SendResponse(message, deepCopy);
                return;
            }

            private void ReportException(Message message, Exception exception)
            {
                switch (message.Direction)
                {
                    case Message.Directions.OneWay:
                        LogErrorInvokingOneWayRequest(_manager.logger, exception, message.BodyObject?.ToString(), message.InterfaceType);
                        break;

                    case Message.Directions.Request:
                        Exception deepCopy;
                        try
                        {
                            // we're expected to notify the caller if the deep copy failed.
                            deepCopy = (Exception)_manager.deepCopier.Copy(exception);
                        }
                        catch (Exception ex2)
                        {
                            _manager.runtimeClient.SendResponse(message, Response.FromException(ex2));
                            LogErrorSendingExceptionResponse(_manager.logger, ex2);
                            return;
                        }

                        // the deep-copy succeeded.
                        var response = Response.FromException(deepCopy);
                        _manager.runtimeClient.SendResponse(message, response);
                        break;

                    default:
                        throw new InvalidOperationException($"Unrecognized direction for message {message}, which resulted in exception: {exception}");
                }
            }

            public void Activate(Dictionary<string, object>? requestContext, CancellationToken cancellationToken) { }
            public void Deactivate(DeactivationReason deactivationReason, CancellationToken cancellationToken) { }

            public void Rehydrate(IRehydrationContext context)
            {
                // Migration is not supported, but we need to dispose of the context if it's provided
                (context as IDisposable)?.Dispose();
            }

            public void Migrate(Dictionary<string, object>? requestContext, CancellationToken cancellationToken)
            {
                // Migration is not supported. Do nothing: the contract is that this method attempts migration, but does not guarantee it will occur.
            }

            ValueTask IGrainCallCancellationExtension.CancelRequestAsync(GrainId senderGrainId, CorrelationId messageId)
            {
                if (!TryCancelRequest())
                {
                    // The message being canceled may not have arrived yet, so retry a few times.
                    LogRequestNotFoundRetrying(_manager.logger, messageId, senderGrainId, ObserverId);
                    return RetryCancellationAfterDelay();
                }

                LogRequestCancelledSuccessfully(_manager.logger, messageId, senderGrainId, ObserverId);
                return ValueTask.CompletedTask;

                async ValueTask RetryCancellationAfterDelay()
                {
                    var attemptsRemaining = 3;
                    var attemptNumber = 1;
                    do
                    {
                        await Task.Delay(1_000);

                        if (TryCancelRequest())
                        {
                            LogRequestCancelledAfterRetry(_manager.logger, messageId, senderGrainId, ObserverId, attemptNumber);
                            return;
                        }

                        LogCancellationRetryAttemptFailed(_manager.logger, messageId, senderGrainId, ObserverId, attemptNumber, attemptsRemaining);
                        attemptNumber++;
                    } while (--attemptsRemaining > 0);

                    LogCancellationFailedAllRetriesExhausted(_manager.logger, messageId, senderGrainId, ObserverId);
                }

                bool TryCancelRequest()
                {
                    Message? message = null;
                    var wasWaiting = false;
                    var initialQueueCount = 0;
                    var finalQueueCount = 0;
                    var runningRequestCount = 0;
                    lock (Messages)
                    {
                        runningRequestCount = _runningRequests.Count;
                        initialQueueCount = Messages.Count;

                        // Check the running requests.
                        foreach (var runningRequest in _runningRequests)
                        {
                            if (runningRequest.Id == messageId && runningRequest.SendingGrain == senderGrainId)
                            {
                                message = runningRequest;
                                break;
                            }
                        }

                        if (message is null)
                        {
                            // Check the waiting requests.
                            foreach (var waitingRequest in Messages)
                            {
                                var waiting = waitingRequest;
                                if (waiting.Id == messageId && waiting.SendingGrain == senderGrainId)
                                {
                                    message = waiting;
                                    wasWaiting = true;

                                    // Remove the message, since it will be rejected immediately (outside the lock) without being executed.
                                    for (var i = 0; i < initialQueueCount; i++)
                                    {
                                        var current = Messages.Dequeue();
                                        if (!ReferenceEquals(current, message))
                                        {
                                            Messages.Enqueue(current);
                                        }
                                    }

                                    finalQueueCount = Messages.Count;
                                    break;
                                }
                            }
                        }
                    }

                    var didCancel = false;
                    if (message is not null)
                    {
                        // The message never began executing, so send a canceled response immediately.
                        // If the message did begin executing, wait for it to observe the cancellation token and respond itself.
                        if (wasWaiting)
                        {
                            LogCancellingWaitingRequest(_manager.logger, messageId, senderGrainId, ObserverId, initialQueueCount, finalQueueCount);
                            _manager.runtimeClient.SendResponse(message, Response.FromException(new OperationCanceledException()));
                            didCancel = true;
                        }
                        else if (message.BodyObject is IInvokable invokableRequest)
                        {
                            didCancel = TryCancelInvokable(invokableRequest) || !invokableRequest.IsCancellable;
                            LogCancellingRequestWithInvokable(_manager.logger, messageId, senderGrainId, ObserverId, wasWaiting);
                        }
                        else
                        {
                            LogCancellingRequestWithoutInvokable(_manager.logger, messageId, senderGrainId, ObserverId, wasWaiting);

                            // Assume the request is not cancellable.
                            return true;
                        }
                    }
                    else
                    {
                        LogRequestNotFoundInCancelAttempt(_manager.logger, messageId, senderGrainId, ObserverId, runningRequestCount, initialQueueCount);
                    }

                    return didCancel;
                }

                bool TryCancelInvokable(IInvokable request)
                {
                    try
                    {
                        return request.TryCancel();
                    }
                    catch (Exception exception)
                    {
                        LogErrorCancellationCallbackFailed(_manager.logger, exception);
                        return true;
                    }
                }
            }

            [LoggerMessage(
                Level = LogLevel.Warning,
                Message = "One or more cancellation callbacks failed."
            )]
            private static partial void LogErrorCancellationCallbackFailed(ILogger logger, Exception exception);

            [LoggerMessage(
                Level = LogLevel.Debug,
                Message = "Request '{MessageId}' from '{SenderGrainId}' not found in observer '{ObserverId}'. Will retry cancellation."
            )]
            private static partial void LogRequestNotFoundRetrying(ILogger logger, CorrelationId messageId, GrainId senderGrainId, ObserverGrainId observerId);

            [LoggerMessage(
                Level = LogLevel.Debug,
                Message = "Successfully cancelled request '{MessageId}' from '{SenderGrainId}' in observer '{ObserverId}'."
            )]
            private static partial void LogRequestCancelledSuccessfully(ILogger logger, CorrelationId messageId, GrainId senderGrainId, ObserverGrainId observerId);

            [LoggerMessage(
                Level = LogLevel.Debug,
                Message = "Successfully cancelled request '{MessageId}' from '{SenderGrainId}' in observer '{ObserverId}' after {AttemptNumber} retry attempt(s)."
            )]
            private static partial void LogRequestCancelledAfterRetry(ILogger logger, CorrelationId messageId, GrainId senderGrainId, ObserverGrainId observerId, int attemptNumber);

            [LoggerMessage(
                Level = LogLevel.Debug,
                Message = "Cancellation retry attempt {AttemptNumber} failed for request '{MessageId}' from '{SenderGrainId}' in observer '{ObserverId}'. {AttemptsRemaining} attempt(s) remaining."
            )]
            private static partial void LogCancellationRetryAttemptFailed(ILogger logger, CorrelationId messageId, GrainId senderGrainId, ObserverGrainId observerId, int attemptNumber, int attemptsRemaining);

            [LoggerMessage(
                Level = LogLevel.Warning,
                Message = "Failed to cancel request '{MessageId}' from '{SenderGrainId}' in observer '{ObserverId}' after exhausting all retry attempts. The request may have already completed or never arrived."
            )]
            private static partial void LogCancellationFailedAllRetriesExhausted(ILogger logger, CorrelationId messageId, GrainId senderGrainId, ObserverGrainId observerId);

            [LoggerMessage(
                Level = LogLevel.Debug,
                Message = "Cancelling request '{MessageId}' from '{SenderGrainId}' in observer '{ObserverId}'. Request has IInvokable body: calling TryCancel. WasWaiting: {WasWaiting}."
            )]
            private static partial void LogCancellingRequestWithInvokable(ILogger logger, CorrelationId messageId, GrainId senderGrainId, ObserverGrainId observerId, bool wasWaiting);

            [LoggerMessage(
                Level = LogLevel.Debug,
                Message = "Cancelling request '{MessageId}' from '{SenderGrainId}' in observer '{ObserverId}'. Request does not have IInvokable body. WasWaiting: {WasWaiting}."
            )]
            private static partial void LogCancellingRequestWithoutInvokable(ILogger logger, CorrelationId messageId, GrainId senderGrainId, ObserverGrainId observerId, bool wasWaiting);

            [LoggerMessage(
                Level = LogLevel.Debug,
                Message = "Cancelling waiting request '{MessageId}' from '{SenderGrainId}' in observer '{ObserverId}'. Removed from queue (initial count: {InitialQueueCount}, final count: {FinalQueueCount}). Sending OperationCanceledException response."
            )]
            private static partial void LogCancellingWaitingRequest(ILogger logger, CorrelationId messageId, GrainId senderGrainId, ObserverGrainId observerId, int initialQueueCount, int finalQueueCount);

            [LoggerMessage(
                Level = LogLevel.Debug,
                Message = "Request '{MessageId}' from '{SenderGrainId}' not found in observer '{ObserverId}' during cancel attempt. Running requests: {RunningRequestCount}, waiting requests: {WaitingRequestCount}."
            )]
            private static partial void LogRequestNotFoundInCancelAttempt(ILogger logger, CorrelationId messageId, GrainId senderGrainId, ObserverGrainId observerId, int runningRequestCount, int waitingRequestCount);

            public Task Deactivated => Task.CompletedTask;
        }

        private readonly struct ObserverGrainIdLogValue(ObserverGrainId observerId)
        {
            public override string ToString() => observerId.ToString();
        }

        [LoggerMessage(
            EventId = (int)ErrorCode.ProxyClient_OGC_TargetNotFound_2,
            Level = LogLevel.Error,
            Message = "Message is not addressed to an observer. Message: '{Message}'."
        )]
        private static partial void LogNotAddressedToAnObserver(ILogger logger, Message message);

        [LoggerMessage(
            EventId = (int)ErrorCode.ProxyClient_OGC_TargetNotFound,
            Level = LogLevel.Error,
            Message = "Unexpected target grain in request: {TargetGrain}. Message: '{Message}'."
        )]
        private static partial void LogUnexpectedTargetInRequest(ILogger logger, GrainId targetGrain, Message message);

        [LoggerMessage(
            Level = LogLevel.Warning,
            EventId = (int)ErrorCode.Runtime_Error_100162,
            Message = "Object associated with id '{ObserverId}' has been garbage collected. Deleting object reference and unregistering it. Message: '{Message}'."
        )]
        private static partial void LogObserverGarbageCollected(ILogger logger, ObserverGrainId observerId, Message message);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "InvokeLocalObjectAsync '{Message}' start '{Start}'."
        )]
        private static partial void LogInvokeLocalObjectAsync(ILogger logger, Message message, bool start);

        [LoggerMessage(
            Level = LogLevel.Warning,
            EventId = (int)ErrorCode.ProxyClient_OGC_SendResponseFailed,
            Message = "Error sending a response."
        )]
        private static partial void LogErrorSendingResponse(ILogger logger, Exception exc);

        [LoggerMessage(
            Level = LogLevel.Error,
            EventId = (int)ErrorCode.ProxyClient_OGC_UnhandledExceptionInOneWayInvoke,
            Message = "Exception during invocation of notification '{Request}', interface '{Interface}'. Ignoring exception because this is a one way request."
        )]
        private static partial void LogErrorInvokingOneWayRequest(ILogger logger, Exception exception, string? request, GrainInterfaceType @interface);

        [LoggerMessage(
            Level = LogLevel.Warning,
            EventId = (int)ErrorCode.ProxyClient_OGC_SendExceptionResponseFailed,
            Message = "Error sending an exception response."
        )]
        private static partial void LogErrorSendingExceptionResponse(ILogger logger, Exception exception);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Exception during message body deserialization in LocalObjectMessagePumpAsync for message: '{Message}'."
        )]
        private static partial void LogErrorDeserializingMessageBody(ILogger logger, Exception exception, Message message);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Exception in LocalObjectMessagePumpAsync."
        )]
        private static partial void LogErrorInMessagePumpLoop(ILogger logger, Exception exception);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Registered observer '{ObserverId}' of type '{ObjectType}'."
        )]
        private static partial void LogObserverRegistered(ILogger logger, ObserverGrainId observerId, Type objectType);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Failed to register observer '{ObserverId}' of type '{ObjectType}' - already registered."
        )]
        private static partial void LogObserverRegistrationFailed(ILogger logger, ObserverGrainId observerId, Type objectType);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Deregistration initiated for observer '{ObserverId}'. Actual removal will occur after grace period."
        )]
        private static partial void LogObserverDeregistrationInitiated(ILogger logger, ObserverGrainId observerId);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Deregistered observer '{ObserverId}'."
        )]
        private static partial void LogObserverDeregistered(ILogger logger, ObserverGrainId observerId);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Failed to deregister observer '{ObserverId}' - not found."
        )]
        private static partial void LogObserverDeregistrationFailed(ILogger logger, ObserverGrainId observerId);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Observer '{ObserverId}' is deregistered, rejecting new message: '{Message}'."
        )]
        private static partial void LogObserverDeregisteredRejectingMessage(ILogger logger, ObserverGrainId observerId, Message message);
    }
}
