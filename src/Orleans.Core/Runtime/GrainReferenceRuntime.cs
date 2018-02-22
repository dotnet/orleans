using Microsoft.Extensions.Logging;
using Orleans.CodeGeneration;
using Orleans.Serialization;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.Runtime
{
    internal class GrainReferenceRuntime : IGrainReferenceRuntime
    {
        private const bool USE_DEBUG_CONTEXT = false;
        private const bool USE_DEBUG_CONTEXT_PARAMS = false;
        private static readonly ConcurrentDictionary<int, string> debugContexts = new ConcurrentDictionary<int, string>();

        private readonly Action<Message, TaskCompletionSource<object>> responseCallbackDelegate;
        private readonly Func<GrainReference, InvokeMethodRequest, string, InvokeMethodOptions, Task<object>> sendRequestDelegate;
        private readonly ILogger logger;
        private readonly IInternalGrainFactory internalGrainFactory;
        private readonly SerializationManager serializationManager;
        private readonly IGrainCancellationTokenRuntime cancellationTokenRuntime;
        private readonly IOutgoingGrainCallFilter[] filters;
        private readonly InterfaceToImplementationMappingCache grainReferenceMethodCache;

        public GrainReferenceRuntime(
            ILogger<GrainReferenceRuntime> logger,
            IRuntimeClient runtimeClient,
            IGrainCancellationTokenRuntime cancellationTokenRuntime,
            IInternalGrainFactory internalGrainFactory,
            SerializationManager serializationManager,
            IEnumerable<IOutgoingGrainCallFilter> outgoingCallFilters)
        {
            this.grainReferenceMethodCache = new InterfaceToImplementationMappingCache();
            this.responseCallbackDelegate = this.ResponseCallback;
            this.sendRequestDelegate = SendRequest;
            this.logger = logger;
            this.RuntimeClient = runtimeClient;
            this.cancellationTokenRuntime = cancellationTokenRuntime;
            this.internalGrainFactory = internalGrainFactory;
            this.serializationManager = serializationManager;
            this.filters = outgoingCallFilters.ToArray();
        }

        public IRuntimeClient RuntimeClient { get; private set; }

        /// <inheritdoc />
        public void InvokeOneWayMethod(GrainReference reference, int methodId, object[] arguments, InvokeMethodOptions options, SiloAddress silo)
        {
            Task<object> resultTask = InvokeMethodAsync<object>(reference, methodId, arguments, options | InvokeMethodOptions.OneWay, silo);
            if (!resultTask.IsCompleted && resultTask.Result != null)
            {
                throw new OrleansException("Unexpected return value: one way InvokeMethod is expected to return null.");
            }
        }

        /// <inheritdoc />
        public Task<T> InvokeMethodAsync<T>(GrainReference reference, int methodId, object[] arguments, InvokeMethodOptions options, SiloAddress silo)
        {
            object[] argsDeepCopy = null;
            if (arguments != null)
            {
                CheckForGrainArguments(arguments);
                SetGrainCancellationTokensTarget(arguments, reference);
                argsDeepCopy = (object[])this.serializationManager.DeepCopy(arguments);
            }

            var request = new InvokeMethodRequest(reference.InterfaceId, reference.InterfaceVersion, methodId, argsDeepCopy);

            if (IsUnordered(reference))
                options |= InvokeMethodOptions.Unordered;

            Task<object> resultTask = InvokeMethod_Impl(reference, request, null, options);

            if (resultTask == null)
            {
                if (typeof(T) == typeof(object))
                {
                    // optimize for most common case when using one way calls.
                    return OrleansTaskExtentions.CompletedTask as Task<T>;
                }

                return Task.FromResult(default(T));
            }

            resultTask = OrleansTaskExtentions.ConvertTaskViaTcs(resultTask);
            return resultTask.ToTypedTask<T>();
        }

        public TGrainInterface Convert<TGrainInterface>(IAddressable grain)
        {
            return this.internalGrainFactory.Cast<TGrainInterface>(grain);
        }

        private Task<object> InvokeMethod_Impl(GrainReference reference, InvokeMethodRequest request, string debugContext, InvokeMethodOptions options)
        {
            if (debugContext == null && USE_DEBUG_CONTEXT)
            {
                if (USE_DEBUG_CONTEXT_PARAMS)
                {
#pragma warning disable 162
                    // This is normally unreachable code, but kept for debugging purposes
                    debugContext = GetDebugContext(reference.InterfaceName, reference.GetMethodName(reference.InterfaceId, request.MethodId), request.Arguments);
#pragma warning restore 162
                }
                else
                {
                    var hash = reference.InterfaceId ^ request.MethodId;
                    if (!debugContexts.TryGetValue(hash, out debugContext))
                    {
                        debugContext = GetDebugContext(reference.InterfaceName, reference.GetMethodName(reference.InterfaceId, request.MethodId), request.Arguments);
                        debugContexts[hash] = debugContext;
                    }
                }
            }

            // Call any registered client pre-call interceptor function.
            CallClientInvokeCallback(reference, request);

            if (this.filters?.Length > 0)
            {
                return InvokeWithFilters(reference, request, debugContext, options);
            }
            
            return SendRequest(reference, request, debugContext, options);
        }

        private Task<object> SendRequest(GrainReference reference, InvokeMethodRequest request, string debugContext, InvokeMethodOptions options)
        {
            bool isOneWayCall = (options & InvokeMethodOptions.OneWay) != 0;

            var resolver = isOneWayCall ? null : new TaskCompletionSource<object>();
            this.RuntimeClient.SendRequest(reference, request, resolver, this.responseCallbackDelegate, debugContext, options, reference.GenericArguments);
            return isOneWayCall ? null : resolver.Task;
        }

        private async Task<object> InvokeWithFilters(GrainReference reference, InvokeMethodRequest request, string debugContext, InvokeMethodOptions options)
        {
            var invoker = new OutgoingCallInvoker(reference, request, options, debugContext, this.sendRequestDelegate, this.grainReferenceMethodCache, this.filters);
            await invoker.Invoke();
            return invoker.Result;
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

        private static String GetDebugContext(string interfaceName, string methodName, object[] arguments)
        {
            // String concatenation is approx 35% faster than string.Format here
            //debugContext = String.Format("{0}:{1}()", interfaceName, methodName);
            var debugContext = new StringBuilder();
            debugContext.Append(interfaceName);
            debugContext.Append(":");
            debugContext.Append(methodName);
            if (USE_DEBUG_CONTEXT_PARAMS && arguments != null && arguments.Length > 0)
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

        private static void CheckForGrainArguments(object[] arguments)
        {
            foreach (var argument in arguments)
                if (argument is Grain)
                    throw new ArgumentException(String.Format("Cannot pass a grain object {0} as an argument to a method. Pass this.AsReference<GrainInterface>() instead.", argument.GetType().FullName));
        }

        /// <summary>
        /// Sets target grain to the found instances of type GrainCancellationToken
        /// </summary>
        /// <param name="arguments"> Grain method arguments list</param>
        /// <param name="target"> Target grain reference</param>
        private void SetGrainCancellationTokensTarget(object[] arguments, GrainReference target)
        {
            if (arguments == null) return;
            foreach (var argument in arguments)
            {
                (argument as GrainCancellationToken)?.AddGrainReference(this.cancellationTokenRuntime, target);
            }
        }

        private bool IsUnordered(GrainReference reference)
        {
            return this.RuntimeClient.GrainTypeResolver?.IsUnordered(reference.GrainId.TypeCode) == true;
        }
    }
}