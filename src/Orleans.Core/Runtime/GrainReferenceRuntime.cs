using Microsoft.Extensions.Logging;
using Orleans.CodeGeneration;
using Orleans.Serialization;
using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace Orleans.Runtime
{
    internal class GrainReferenceRuntime : IGrainReferenceRuntime
    {
        private const bool USE_DEBUG_CONTEXT = false;
        private const bool USE_DEBUG_CONTEXT_PARAMS = false;
        private static ConcurrentDictionary<int, string> debugContexts = new ConcurrentDictionary<int, string>();

        private readonly Action<Message, TaskCompletionSource<object>> responseCallbackDelegate;
        private readonly ILogger logger;
        private readonly IInternalGrainFactory internalGrainFactory;
        private readonly SerializationManager serializationManager;
        private readonly IGrainCancellationTokenRuntime cancellationTokenRuntime;
        private readonly IGrainCallArgumentVisitor<CheckAndDeepCopyArgumentVisitor.Context> _checkAndDeepCopy;

        public GrainReferenceRuntime(
            ILogger<GrainReferenceRuntime> logger,
            IRuntimeClient runtimeClient,
            IGrainCancellationTokenRuntime cancellationTokenRuntime,
            IInternalGrainFactory internalGrainFactory,
            SerializationManager serializationManager)
        {
            this.responseCallbackDelegate = this.ResponseCallback;
            this.logger = logger;
            this.RuntimeClient = runtimeClient;
            this.cancellationTokenRuntime = cancellationTokenRuntime;
            this.internalGrainFactory = internalGrainFactory;
            this.serializationManager = serializationManager;
            this._checkAndDeepCopy = new CheckAndDeepCopyArgumentVisitor();
        }

        public IRuntimeClient RuntimeClient { get; private set; }

        /// <inheritdoc />
        public void InvokeOneWayMethod<TArgs>(GrainReference reference, int methodId, ref TArgs arguments, InvokeMethodOptions options, SiloAddress silo)
            where TArgs : struct, IGrainCallArguments
        {
            Task<object> resultTask = InvokeMethodAsync<TArgs, object>(reference, methodId, ref arguments, options | InvokeMethodOptions.OneWay, silo);
            if (!resultTask.IsCompleted && resultTask.Result != null)
            {
                throw new OrleansException("Unexpected return value: one way InvokeMethod is expected to return null.");
            }
        }

        /// <inheritdoc />
        public Task<TResult> InvokeMethodAsync<TArgs, TResult>(GrainReference reference, int methodId, ref TArgs arguments, InvokeMethodOptions options, SiloAddress silo)
            where TArgs : struct, IGrainCallArguments
        {
            if (arguments.Count != 0)
            {
                arguments.Visit(_checkAndDeepCopy, new CheckAndDeepCopyArgumentVisitor.Context { Runtime = this, Target = reference });
            }

            var request = new InvokeMethodRequest<TArgs>(reference.InterfaceId, reference.InterfaceVersion, methodId, ref arguments);

            if (IsUnordered(reference))
                options |= InvokeMethodOptions.Unordered;

            Task<object> resultTask = InvokeMethod_Impl(reference, request, null, options);

            if (resultTask == null)
            {
                if (typeof(TResult) == typeof(object))
                {
                    // optimize for most common case when using one way calls.
                    return OrleansTaskExtentions.CompletedTask as Task<TResult>;
                }

                return Task.FromResult(default(TResult));
            }

            resultTask = OrleansTaskExtentions.ConvertTaskViaTcs(resultTask);
            return resultTask.ToTypedTask<TResult>();
        }

        public TGrainInterface Convert<TGrainInterface>(IAddressable grain)
        {
            return this.internalGrainFactory.Cast<TGrainInterface>(grain);
        }

        private Task<object> InvokeMethod_Impl<TArgs>(GrainReference reference, InvokeMethodRequest<TArgs> request, string debugContext, InvokeMethodOptions options)
            where TArgs : struct, IGrainCallArguments
        {
            if (debugContext == null && USE_DEBUG_CONTEXT)
            {
                if (USE_DEBUG_CONTEXT_PARAMS)
                {
#pragma warning disable 162
                    // This is normally unreachable code, but kept for debugging purposes
                    debugContext = GetDebugContext(reference.InterfaceName, reference.GetMethodName(reference.InterfaceId, request.MethodId), ref request.Arguments);
#pragma warning restore 162
                }
                else
                {
                    var hash = reference.InterfaceId ^ request.MethodId;
                    if (!debugContexts.TryGetValue(hash, out debugContext))
                    {
                        debugContext = GetDebugContext(reference.InterfaceName, reference.GetMethodName(reference.InterfaceId, request.MethodId), ref request.Arguments);
                        debugContexts[hash] = debugContext;
                    }
                }
            }

            // Call any registered client pre-call interceptor function.
            CallClientInvokeCallback(reference, request);

            bool isOneWayCall = ((options & InvokeMethodOptions.OneWay) != 0);

            var resolver = isOneWayCall ? null : new TaskCompletionSource<object>();
            this.RuntimeClient.SendRequest(reference, request, resolver, this.responseCallbackDelegate, debugContext, options, reference.GenericArguments);
            return isOneWayCall ? null : resolver.Task;
        }

        private void CallClientInvokeCallback(GrainReference reference, InvokeMethodRequest request)
        {
            // Make callback to any registered client callback function, allowing opportunity for an application to set any additional RequestContext info, etc.
            // Should we set some kind of callback-in-progress flag to detect and prevent any inappropriate callback loops on this GrainReference?
            try
            {
                var callback = this.RuntimeClient.ClientInvokeCallback; // Take copy to avoid potential race conditions
                if (callback == null) return;

                // Call ClientInvokeCallback only for grain calls, not for system targets.
                var grain = reference as IGrain;
                if (grain != null)
                {
                    callback(request, grain);
                }
            }
            catch (Exception exc)
            {
                logger.Warn(ErrorCode.ProxyClient_ClientInvokeCallback_Error,
                    "Error while invoking ClientInvokeCallback function " + this.RuntimeClient?.ClientInvokeCallback,
                    exc);
                throw;
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        private void ResponseCallback(Message message, TaskCompletionSource<object> context)
        {
            Response response;
            if (message.Result != Message.ResponseTypes.Rejection)
            {
                try
                {
                    response = (Response)message.GetDeserializedBody(this.serializationManager);
                }
                catch (Exception exc)
                {
                    //  catch the Deserialize exception and break the promise with it.
                    response = Response.ExceptionResponse(exc);
                }
            }
            else
            {
                Exception rejection;
                switch (message.RejectionType)
                {
                    case Message.RejectionTypes.GatewayTooBusy:
                        rejection = new GatewayTooBusyException();
                        break;
                    case Message.RejectionTypes.DuplicateRequest:
                        return; // Ignore duplicates

                    default:
                        rejection = message.GetDeserializedBody(this.serializationManager) as Exception;
                        if (rejection == null)
                        {
                            if (string.IsNullOrEmpty(message.RejectionInfo))
                            {
                                message.RejectionInfo = "Unable to send request - no rejection info available";
                            }
                            rejection = new OrleansMessageRejectionException(message.RejectionInfo);
                        }
                        break;
                }
                response = Response.ExceptionResponse(rejection);
            }

            if (!response.ExceptionFlag)
            {
                context.TrySetResult(response.Data);
            }
            else
            {
                context.TrySetException(response.Exception);
            }
        }

        private static String GetDebugContext<TArgs>(string interfaceName, string methodName, ref TArgs arguments)
            where TArgs : IGrainCallArguments
        {
            // String concatenation is approx 35% faster than string.Format here
            //debugContext = String.Format("{0}:{1}()", interfaceName, methodName);
            var debugContext = new StringBuilder();
            debugContext.Append(interfaceName);
            debugContext.Append(":");
            debugContext.Append(methodName);
            if (USE_DEBUG_CONTEXT_PARAMS && arguments.Count > 0)
            {
                debugContext.Append("(");
                debugContext.Append(Utils.EnumerableToString(arguments));
                debugContext.Append(")");
            }
            else
            {
                debugContext.Append("()");
            }
            return debugContext.ToString();
        }

        private bool IsUnordered(GrainReference reference)
        {
            return this.RuntimeClient.GrainTypeResolver?.IsUnordered(reference.GrainId.TypeCode) == true;
        }

        private sealed class CheckAndDeepCopyArgumentVisitor : IGrainCallArgumentVisitor<CheckAndDeepCopyArgumentVisitor.Context>
        {
            public struct Context
            {
                public GrainReferenceRuntime Runtime;
                public GrainReference Target;
            }

            public void Visit<TArg>(ref TArg item, Context context)
            {
                if (item is Grain)
                    throw new ArgumentException(String.Format("Cannot pass a grain object {0} as an argument to a method. Pass this.AsReference<GrainInterface>() instead.", item.GetType().FullName));

                // Sets target grain to the found instances of type GrainCancellationToken
                (item as GrainCancellationToken)?.AddGrainReference(context.Runtime.cancellationTokenRuntime, context.Target);

                context.Runtime.serializationManager.DeepCopyInPlace(ref item);
            }
        }
    }
}