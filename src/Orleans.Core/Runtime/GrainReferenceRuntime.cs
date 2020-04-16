using Orleans.CodeGeneration;
using Orleans.Internal;
using Orleans.Serialization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Orleans.Runtime
{
    internal class GrainReferenceRuntime : IGrainReferenceRuntime
    {
        private readonly Func<GrainReference, InvokeMethodRequest, InvokeMethodOptions, Task<object>> sendRequestDelegate;
        private readonly SerializationManager serializationManager;
        private readonly IGrainCancellationTokenRuntime cancellationTokenRuntime;
        private readonly IOutgoingGrainCallFilter[] filters;
        private readonly InterfaceToImplementationMappingCache grainReferenceMethodCache;

        public GrainReferenceRuntime(
            IRuntimeClient runtimeClient,
            IGrainCancellationTokenRuntime cancellationTokenRuntime,
            SerializationManager serializationManager,
            IEnumerable<IOutgoingGrainCallFilter> outgoingCallFilters,
            TypeMetadataCache typeMetadataCache)
        {
            this.grainReferenceMethodCache = new InterfaceToImplementationMappingCache();
            this.sendRequestDelegate = SendRequest;
            this.RuntimeClient = runtimeClient;
            this.cancellationTokenRuntime = cancellationTokenRuntime;
            this.GrainReferenceFactory = new GrainReferenceFactory(typeMetadataCache, this);
            this.serializationManager = serializationManager;
            this.filters = outgoingCallFilters.ToArray();
        }

        public IRuntimeClient RuntimeClient { get; private set; }

        public GrainReferenceFactory GrainReferenceFactory { get; }

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
            => (TGrainInterface)this.GrainReferenceFactory.Cast(grain, typeof(TGrainInterface));

        public object Convert(IAddressable grain, Type interfaceType)
            => this.GrainReferenceFactory.Cast(grain, interfaceType);

        private Task<object> InvokeMethod_Impl(GrainReference reference, InvokeMethodRequest request, InvokeMethodOptions options)
        {
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
            return LegacyGrainId.TryConvertFromGrainId(reference.GrainId, out var legacyId)
                && this.RuntimeClient.GrainTypeResolver is IGrainTypeResolver resolver
                && resolver.IsUnordered(legacyId.TypeCode);
        }
    }
}