using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using Orleans.Serialization;

namespace Orleans
{
    internal class InvokableObjectManager : IDisposable
    {
        private readonly CancellationTokenSource disposed = new CancellationTokenSource();
        private readonly ConcurrentDictionary<GuidId, LocalObjectData> localObjects = new ConcurrentDictionary<GuidId, LocalObjectData>();
        private readonly IRuntimeClient runtimeClient;
        private readonly ILogger logger;
        private readonly SerializationManager serializationManager;
        private readonly Func<object, Task> dispatchFunc;

        public InvokableObjectManager(IRuntimeClient runtimeClient, SerializationManager serializationManager, ILogger<InvokableObjectManager> logger)
        {
            this.runtimeClient = runtimeClient;
            this.serializationManager = serializationManager;
            this.logger = logger;

            this.dispatchFunc = o =>
                this.LocalObjectMessagePumpAsync((LocalObjectData) o);
        }

        public bool TryRegister(IAddressable obj, GuidId objectId, IGrainMethodInvoker invoker)
        {
            return this.localObjects.TryAdd(objectId, new LocalObjectData(obj, objectId, invoker));
        }

        public bool TryDeregister(GuidId objectId)
        {
            return this.localObjects.TryRemove(objectId, out LocalObjectData ignored);
        }

        public void Dispatch(Message message)
        {
            GuidId observerId = message.TargetObserverId;
            if (observerId == null)
            {
                this.logger.Error(
                    ErrorCode.ProxyClient_OGC_TargetNotFound_2,
                    string.Format("Did not find TargetObserverId header in the message = {0}. A request message to a client is expected to have an observerId.", message));
                return;
            }

            if (this.localObjects.TryGetValue(observerId, out var objectData))
            {
                this.Invoke(objectData, message);
            }
            else
            {
                this.logger.Error(
                    ErrorCode.ProxyClient_OGC_TargetNotFound,
                    String.Format(
                        "Unexpected target grain in request: {0}. Message={1}",
                        message.TargetGrain,
                        message));
            }
        }

        private void Invoke(LocalObjectData objectData, Message message)
        {
            var obj = (IAddressable)objectData.LocalObject.Target;
            if (obj == null)
            {
                //// Remove from the dictionary record for the garbage collected object? But now we won't be able to detect invalid dispatch IDs anymore.
                this.logger.Warn(
                    ErrorCode.Runtime_Error_100162,
                    string.Format(
                        "Object associated with Observer ID {0} has been garbage collected. Deleting object reference and unregistering it. Message = {1}",
                        objectData.ObserverId,
                        message));
                
                // Try to remove. If it's not there, we don't care.
                this.TryDeregister(objectData.ObserverId);
                return;
            }

            bool start;
            lock (objectData.Messages)
            {
                objectData.Messages.Enqueue(message);
                start = !objectData.Running;
                objectData.Running = true;
            }

            if (this.logger.IsEnabled(LogLevel.Trace)) this.logger.Trace($"InvokeLocalObjectAsync {message} start {start}");

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
                        this.dispatchFunc,
                        objectData,
                        CancellationToken.None,
                        TaskCreationOptions.DenyChildAttach,
                        TaskScheduler.Default).Ignore();
            }
        }

        private async Task LocalObjectMessagePumpAsync(LocalObjectData objectData)
        {
            while (true)
            {
                try
                {
                    Message message;
                    lock (objectData.Messages)
                    {
                        if (objectData.Messages.Count == 0)
                        {
                            objectData.Running = false;
                            break;
                        }

                        message = objectData.Messages.Dequeue();
                    }

                    if (ExpireMessageIfExpired(message, MessagingStatisticsGroup.Phase.Invoke))
                        continue;

                    RequestContextExtensions.Import(message.RequestContextData);
                    InvokeMethodRequest request = null;
                    try
                    {
                        request = (InvokeMethodRequest) message.BodyObject;
                    }
                    catch (Exception deserializationException)
                    {
                        if (this.logger.IsEnabled(LogLevel.Warning))
                        {
                            this.logger.LogWarning(
                                "Exception during message body deserialization in " + nameof(LocalObjectMessagePumpAsync) + " for message: {Message}, Exception: {Exception}",
                                message,
                                deserializationException);
                        }

                        this.runtimeClient.SendResponse(message, Response.ExceptionResponse(deserializationException));
                        continue;
                    }

                    var targetOb = (IAddressable)objectData.LocalObject.Target;
                    object resultObject = null;
                    Exception caught = null;
                    try
                    {

                        // exceptions thrown within this scope are not considered to be thrown from user code
                        // and not from runtime code.
                        var resultPromise = objectData.Invoker.Invoke(targetOb, request);
                        if (resultPromise != null) // it will be null for one way messages
                        {
                            resultObject = await resultPromise;
                        }
                    }
                    catch (Exception exc)
                    {
                        // the exception needs to be reported in the log or propagated back to the caller.
                        caught = exc;
                    }

                    if (caught != null)
                        this.ReportException(message, caught);
                    else if (message.Direction != Message.Directions.OneWay)
                        this.SendResponseAsync(message, resultObject);
                }
                catch (Exception outerException)
                {
                    // ignore, keep looping.
                    this.logger.LogWarning("Exception in " + nameof(LocalObjectMessagePumpAsync) + ": {Exception}", outerException);
                }
                finally
                {
                    RequestContext.Clear();
                }
            }
        }

        private static bool ExpireMessageIfExpired(Message message, MessagingStatisticsGroup.Phase phase)
        {
            if (message.IsExpired)
            {
                message.DropExpiredMessage(phase);
                return true;
            }

            return false;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        private void SendResponseAsync(Message message, object resultObject)
        {
            if (ExpireMessageIfExpired(message, MessagingStatisticsGroup.Phase.Respond))
            {
                return;
            }

            object deepCopy;
            try
            {
                // we're expected to notify the caller if the deep copy failed.
                deepCopy = this.serializationManager.DeepCopy(resultObject);
            }
            catch (Exception exc2)
            {
                this.runtimeClient.SendResponse(message, Response.ExceptionResponse(exc2));
                this.logger.Warn(
                    ErrorCode.ProxyClient_OGC_SendResponseFailed,
                    "Exception trying to send a response.",
                    exc2);
                return;
            }

            // the deep-copy succeeded.
            this.runtimeClient.SendResponse(message, new Response(deepCopy));
            return;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        private void ReportException(Message message, Exception exception)
        {
            var request = (InvokeMethodRequest)message.BodyObject;
            switch (message.Direction)
            {
                case Message.Directions.OneWay:
                {
                    this.logger.Error(
                        ErrorCode.ProxyClient_OGC_UnhandledExceptionInOneWayInvoke,
                        String.Format(
                            "Exception during invocation of notification method {0}, interface {1}. Ignoring exception because this is a one way request.",
                            request.MethodId,
                            request.InterfaceId),
                        exception);
                    break;
                }

                case Message.Directions.Request:
                {
                    Exception deepCopy = null;
                    try
                    {
                        // we're expected to notify the caller if the deep copy failed.
                        deepCopy = (Exception)this.serializationManager.DeepCopy(exception);
                    }
                    catch (Exception ex2)
                    {
                        this.runtimeClient.SendResponse(message, Response.ExceptionResponse(ex2));
                        this.logger.Warn(
                            ErrorCode.ProxyClient_OGC_SendExceptionResponseFailed,
                            "Exception trying to send an exception response", ex2);
                        return;
                    }
                    // the deep-copy succeeded.
                    var response = Response.ExceptionResponse(deepCopy);
                    this.runtimeClient.SendResponse(message, response);
                    break;
                }

                default:
                    throw new InvalidOperationException($"Unrecognized direction for message {message}, request {request}, which resulted in exception: {exception}");
            }
        }

        public class LocalObjectData
        {
            internal WeakReference LocalObject { get; }
            internal IGrainMethodInvoker Invoker { get; }
            internal GuidId ObserverId { get; }
            internal Queue<Message> Messages { get; }
            internal bool Running { get; set; }

            internal LocalObjectData(IAddressable obj, GuidId observerId, IGrainMethodInvoker invoker)
            {
                this.LocalObject = new WeakReference(obj);
                this.ObserverId = observerId;
                this.Invoker = invoker;
                this.Messages = new Queue<Message>();
                this.Running = false;
            }
        }

        public void Dispose()
        {
            var tokenSource = this.disposed;
            Utils.SafeExecute(() => tokenSource?.Cancel(false));
            Utils.SafeExecute(() => tokenSource?.Dispose());
        }
    }
}