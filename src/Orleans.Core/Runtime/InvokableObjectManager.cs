using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
        private List<IIncomingGrainCallFilter> _grainCallFilters;

        private List<IIncomingGrainCallFilter> GrainCallFilters
            => _grainCallFilters ??= new List<IIncomingGrainCallFilter>(runtimeClient.ServiceProvider.GetServices<IIncomingGrainCallFilter>());

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
            return this.localObjects.TryAdd(objectId, new LocalObjectData(obj, objectId, this));
        }

        public bool TryDeregister(ObserverGrainId objectId)
        {
            return this.localObjects.TryRemove(objectId, out _);
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

        public sealed class LocalObjectData : IGrainContext
        {
            private static readonly Func<object, Task> HandleFunc = self => ((LocalObjectData)self).LocalObjectMessagePumpAsync();
            private readonly InvokableObjectManager _manager;

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

            object IGrainContext.GrainInstance => this.LocalObject.Target;

            ActivationId IGrainContext.ActivationId => throw new NotImplementedException();

            GrainAddress IGrainContext.Address => throw new NotImplementedException();

            IServiceProvider IGrainContext.ActivationServices => throw new NotSupportedException();

            IGrainLifecycle IGrainContext.ObservableLifecycle => throw new NotImplementedException();

            public IWorkItemScheduler Scheduler => throw new NotImplementedException();

            void IGrainContext.SetComponent<TComponent>(TComponent value) where TComponent : class
            {
                if (this.LocalObject.Target is TComponent)
                {
                    throw new ArgumentException("Cannot override a component which is implemented by this grain");
                }

                _manager.rootGrainContext.SetComponent(value);
            }

            public TComponent GetComponent<TComponent>() where TComponent : class
            {
                if (this.LocalObject.Target is TComponent component)
                {
                    return component;
                }

                return _manager.rootGrainContext.GetComponent<TComponent>();
            }

            public TTarget GetTarget<TTarget>() where TTarget : class => (TTarget)this.LocalObject.Target;

            bool IEquatable<IGrainContext>.Equals(IGrainContext other) => ReferenceEquals(this, other);

            public void ReceiveMessage(object msg)
            {
                var message = (Message)msg;
                var obj = (IAddressable)this.LocalObject.Target;
                if (obj == null)
                {
                    // Remove from the dictionary record for the garbage collected object? But now we won't be able to detect invalid dispatch IDs anymore.
                    LogObserverGarbageCollected(_manager.logger, this.ObserverId, message);
                    // Try to remove. If it's not there, we don't care.
                    _manager.TryDeregister(this.ObserverId);
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
                while (true)
                {
                    try
                    {
                        Message message;
                        lock (this.Messages)
                        {
                            if (this.Messages.Count == 0)
                            {
                                this.Running = false;
                                break;
                            }

                            message = this.Messages.Dequeue();
                        }

                        if (message.IsExpired)
                        {
                            _manager.messagingTrace.OnDropExpiredMessage(message, MessagingInstruments.Phase.Invoke);
                            continue;
                        }

                        if (message.RequestContextData is { Count: > 0 })
                        {
                            RequestContextExtensions.Import(message.RequestContextData);
                        }

                        IInvokable request = null;
                        try
                        {
                            request = (IInvokable)message.BodyObject;
                        }
                        catch (Exception deserializationException)
                        {
                            LogErrorDeserializingMessageBody(_manager.logger, deserializationException, message);
                            _manager.runtimeClient.SendResponse(message, Response.FromException(deserializationException));
                            continue;
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
                    }
                    catch (Exception outerException)
                    {
                        // ignore, keep looping.
                        LogErrorInMessagePumpLoop(_manager.logger, outerException);
                    }
                }
            }

            [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
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

            [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
            private void ReportException(Message message, Exception exception)
            {
                var request = (IInvokable)message.BodyObject;
                switch (message.Direction)
                {
                    case Message.Directions.OneWay:
                        LogErrorInvokingOneWayRequest(_manager.logger, exception, request.ToString(), message.InterfaceType);
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
                        throw new InvalidOperationException($"Unrecognized direction for message {message}, request {request}, which resulted in exception: {exception}");
                }
            }

            public void Activate(Dictionary<string, object> requestContext, CancellationToken cancellationToken) { }
            public void Deactivate(DeactivationReason deactivationReason, CancellationToken cancellationToken) { }

            public void Rehydrate(IRehydrationContext context)
            {
                // Migration is not supported, but we need to dispose of the context if it's provided
                (context as IDisposable)?.Dispose();
            }

            public void Migrate(Dictionary<string, object> requestContext, CancellationToken cancellationToken)
            {
                // Migration is not supported. Do nothing: the contract is that this method attempts migration, but does not guarantee it will occur.
            }

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
        private static partial void LogErrorInvokingOneWayRequest(ILogger logger, Exception exception, string request, GrainInterfaceType @interface);

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
    }
}
