using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Invocation;

namespace Orleans
{
    internal class InvokableObjectManager : IDisposable
    {
        private readonly CancellationTokenSource disposed = new CancellationTokenSource();
        private readonly ConcurrentDictionary<ObserverGrainId, LocalObjectData> localObjects = new ConcurrentDictionary<ObserverGrainId, LocalObjectData>();
        private readonly IGrainContext rootGrainContext;
        private readonly IRuntimeClient runtimeClient;
        private readonly ILogger logger;
        private readonly DeepCopier deepCopier;
        private readonly MessagingTrace messagingTrace;

        public InvokableObjectManager(
            IGrainContext rootGrainContext,
            IRuntimeClient runtimeClient,
            DeepCopier deepCopier,
            MessagingTrace messagingTrace,
            ILogger logger)
        {
            this.rootGrainContext = rootGrainContext;
            this.runtimeClient = runtimeClient;
            this.deepCopier = deepCopier;
            this.messagingTrace = messagingTrace;
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
                this.logger.LogError(
                    (int)ErrorCode.ProxyClient_OGC_TargetNotFound_2,
                    "Message is not addressed to an observer. {Message}",
                    message);
                return;
            }

            if (this.localObjects.TryGetValue(observerId, out var objectData))
            {
                objectData.ReceiveMessage(message);
            }
            else
            {
                this.logger.LogError(
                    (int)ErrorCode.ProxyClient_OGC_TargetNotFound,
                    "Unexpected target grain in request: {TargetGrain}. Message: {Message}",
                    message.TargetGrain,
                    message);
            }
        }

        public void Dispose()
        {
            var tokenSource = this.disposed;
            Utils.SafeExecute(() => tokenSource?.Cancel(false));
            Utils.SafeExecute(() => tokenSource?.Dispose());
        }

        public class LocalObjectData : IGrainContext
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

            GrainReference IGrainContext.GrainReference => (this.LocalObject.Target as IAddressable).AsReference();

            IAddressable IGrainContext.GrainInstance => this.LocalObject.Target as IAddressable;

            ActivationId IGrainContext.ActivationId => throw new NotImplementedException();

            GrainAddress IGrainContext.Address => throw new NotImplementedException();

            IServiceProvider IGrainContext.ActivationServices => throw new NotSupportedException();

            IGrainLifecycle IGrainContext.ObservableLifecycle => throw new NotImplementedException();

            public IWorkItemScheduler Scheduler => throw new NotImplementedException();

            public bool IsExemptFromCollection => true;

            public PlacementStrategy PlacementStrategy => ClientObserversPlacement.Instance;

            void IGrainContext.SetComponent<TComponent>(TComponent value)
            {
                if (this.LocalObject.Target is TComponent)
                {
                    throw new ArgumentException("Cannot override a component which is implemented by this grain");
                }

                _manager.rootGrainContext.SetComponent(value);
            }

            public TComponent GetComponent<TComponent>()
            {
                if (this.LocalObject.Target is TComponent component)
                {
                    return component;
                }

                return _manager.rootGrainContext.GetComponent<TComponent>();
            }

            public TTarget GetTarget<TTarget>() => (TTarget)(object)this.LocalObject.Target;

            bool IEquatable<IGrainContext>.Equals(IGrainContext other) => ReferenceEquals(this, other);

            public void ReceiveMessage(object msg)
            {
                var message = (Message)msg;
                var obj = (IAddressable)this.LocalObject.Target;
                if (obj == null)
                {
                    //// Remove from the dictionary record for the garbage collected object? But now we won't be able to detect invalid dispatch IDs anymore.
                    _manager.logger.LogWarning(
                        (int)ErrorCode.Runtime_Error_100162,
                        "Object associated with Observer ID {ObserverId} has been garbage collected. Deleting object reference and unregistering it. Message = {message}",
                        this.ObserverId,
                        message);

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

                if (_manager.logger.IsEnabled(LogLevel.Trace)) _manager.logger.Trace($"InvokeLocalObjectAsync {message} start {start}");

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
                            _manager.messagingTrace.OnDropExpiredMessage(message, MessagingStatisticsGroup.Phase.Invoke);
                            continue;
                        }

                        RequestContextExtensions.Import(message.RequestContextData);
                        IInvokable request = null;
                        try
                        {
                            request = (IInvokable)message.BodyObject;
                        }
                        catch (Exception deserializationException)
                        {
                            if (_manager.logger.IsEnabled(LogLevel.Warning))
                            {
                                _manager.logger.LogWarning(
                                    deserializationException,
                                    "Exception during message body deserialization in " + nameof(LocalObjectMessagePumpAsync) + " for message: {Message}",
                                    message);
                            }

                            _manager.runtimeClient.SendResponse(message, Response.FromException(deserializationException));
                            continue;
                        }

                        try
                        {
                            request.SetTarget(this);
                            var response = await request.Invoke();
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
                        _manager.logger.LogWarning(
                            outerException,
                            "Exception in " + nameof(LocalObjectMessagePumpAsync));
                    }
                    finally
                    {
                        RequestContext.Clear();
                    }
                }
            }

            [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
            private void SendResponseAsync(Message message, Response resultObject)
            {
                if (message.IsExpired)
                {
                    _manager.messagingTrace.OnDropExpiredMessage(message, MessagingStatisticsGroup.Phase.Respond);
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
                    _manager.logger.LogWarning((int)ErrorCode.ProxyClient_OGC_SendResponseFailed, exc2, "Exception trying to send a response.");
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
                        {
                            _manager.logger.LogError(
                                (int)ErrorCode.ProxyClient_OGC_UnhandledExceptionInOneWayInvoke,
                                exception,
                                "Exception during invocation of notification {Request}, interface {Interface}. Ignoring exception because this is a one way request.",
                                request.ToString(),
                                message.InterfaceType);
                            break;
                        }

                    case Message.Directions.Request:
                        {
                            Exception deepCopy;
                            try
                            {
                                // we're expected to notify the caller if the deep copy failed.
                                deepCopy = (Exception)_manager.deepCopier.Copy(exception);
                            }
                            catch (Exception ex2)
                            {
                                _manager.runtimeClient.SendResponse(message, Response.FromException(ex2));
                                _manager.logger.LogWarning(
                                    (int)ErrorCode.ProxyClient_OGC_SendExceptionResponseFailed,
                                    ex2,
                                    "Exception trying to send an exception response");
                                return;
                            }
                             
                            // the deep-copy succeeded.
                            var response = Response.FromException(deepCopy);
                            _manager.runtimeClient.SendResponse(message, response);
                            break;
                        }

                    default:
                        throw new InvalidOperationException($"Unrecognized direction for message {message}, request {request}, which resulted in exception: {exception}");
                }
            }

            public void Activate(Dictionary<string, object> requestContext, CancellationToken? cancellationToken = null) { }
            public void Deactivate(CancellationToken? cancellationToken = null) { }
            public Task Deactivated => Task.CompletedTask;
        }
    }
}
