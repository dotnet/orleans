using Microsoft.Extensions.Logging;
using Orleans.CodeGeneration;
using Orleans.Internal;
using Orleans.Serialization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.Runtime
{
    internal class GrainReferenceRuntime : IGrainReferenceRuntime
    {
        private readonly Func<GrainReference, InvokeMethodRequest, InvokeMethodOptions, Task<object>> sendRequestDelegate;
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
            if (arguments != null)
            {
                CheckForGrainArguments(arguments);
                SetGrainCancellationTokensTarget(arguments, reference);
                this.serializationManager.DeepCopyElementsInPlace(arguments);
            }

            var request = new InvokeMethodRequest(reference.InterfaceId, reference.InterfaceVersion, methodId, arguments);

            if (IsUnordered(reference))
                options |= InvokeMethodOptions.Unordered;

            Task<object> resultTask = InvokeMethod_Impl(reference, request, options);

            if (resultTask == null)
            {
                if (typeof(T) == typeof(object))
                {
                    // optimize for most common case when using one way calls.
                    return OrleansTaskExtentions.CompletedTask as Task<T>;
                }

                return Task.FromResult(default(T));
            }
#if !NETCOREAPP
            resultTask = OrleansTaskExtentions.ConvertTaskViaTcs(resultTask);
#endif
            return resultTask.ToTypedTask<T>();
        }

        public TGrainInterface Convert<TGrainInterface>(IAddressable grain)
            => this.internalGrainFactory.Cast<TGrainInterface>(grain);

        public object Convert(IAddressable grain, Type interfaceType)
            => this.internalGrainFactory.Cast(grain, interfaceType);

        private Task<object> InvokeMethod_Impl(GrainReference reference, InvokeMethodRequest request, InvokeMethodOptions options)
        {
            // Call any registered client pre-call interceptor function.
            CallClientInvokeCallback(reference, request);

            if (this.filters?.Length > 0)
            {
                return InvokeWithFilters(reference, request, options);
            }
            
            return SendRequest(reference, request, options);
        }

        private Task<object> SendRequest(GrainReference reference, InvokeMethodRequest request, InvokeMethodOptions options)
        {
            bool isOneWayCall = (options & InvokeMethodOptions.OneWay) != 0;

            var resolver = isOneWayCall ? null : new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            this.RuntimeClient.SendRequest(reference, request, resolver, options, reference.GenericArguments);
            return isOneWayCall ? null : resolver.Task;
        }

        private async Task<object> InvokeWithFilters(GrainReference reference, InvokeMethodRequest request, InvokeMethodOptions options)
        {
            var invoker = new OutgoingCallInvoker(reference, request, options, this.sendRequestDelegate, this.grainReferenceMethodCache, this.filters);
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