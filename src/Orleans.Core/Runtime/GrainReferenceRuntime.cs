using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using Orleans.GrainReferences;
using Orleans.Metadata;
using Orleans.Serialization;
using Orleans.Serialization.Invocation;

namespace Orleans.Runtime
{
    internal class GrainReferenceRuntime : IGrainReferenceRuntime
    {
        private readonly GrainReferenceActivator referenceActivator;
        private readonly GrainInterfaceTypeResolver interfaceTypeResolver;
        private readonly IGrainCancellationTokenRuntime cancellationTokenRuntime;
        private readonly IOutgoingGrainCallFilter[] filters;
        private readonly Action<GrainReference, IResponseCompletionSource, IInvokable, InvokeMethodOptions> sendRequest;
        private readonly DeepCopier requestCopier;

        public GrainReferenceRuntime(
            IRuntimeClient runtimeClient,
            IGrainCancellationTokenRuntime cancellationTokenRuntime,
            IEnumerable<IOutgoingGrainCallFilter> outgoingCallFilters,
            GrainReferenceActivator referenceActivator,
            GrainInterfaceTypeResolver interfaceTypeResolver,
            DeepCopier requestCopier)
        {
            this.RuntimeClient = runtimeClient;
            this.cancellationTokenRuntime = cancellationTokenRuntime;
            this.referenceActivator = referenceActivator;
            this.interfaceTypeResolver = interfaceTypeResolver;
            this.filters = outgoingCallFilters.ToArray();
            this.requestCopier = requestCopier;
            this.sendRequest = SendFilteredRequest;
        }

        public IRuntimeClient RuntimeClient { get; private set; }

        public ValueTask<TResult?> InvokeMethodAsync<TResult>(GrainReference reference, IInvokable request, InvokeMethodOptions options)
        {
            // TODO: Remove expensive interface type check
            if (this.filters.Length == 0 && request is not IOutgoingGrainCallFilter)
            {
                return InvokeMethodAsyncCore<TResult>(reference, request, options);
            }
            else
            {
                return InvokeMethodWithFiltersAsync<TResult>(reference, request, options);
            }
        }

        public ValueTask InvokeMethodAsync(GrainReference reference, IInvokable request, InvokeMethodOptions options)
        {
            // TODO: Remove expensive interface type check
            if (filters.Length == 0 && request is not IOutgoingGrainCallFilter)
            {
                return InvokeMethodAsyncCore(reference, request, options);
            }
            else
            {
                return InvokeMethodWithFiltersAsync(reference, request, options);
            }
        }

        public void InvokeMethod(GrainReference reference, IInvokable request, InvokeMethodOptions options)
        {
            Debug.Assert((options & InvokeMethodOptions.OneWay) != 0);
            var requestTransferred = false;

            try
            {
                // TODO: Remove expensive interface type check
                if (filters.Length == 0 && request is not IOutgoingGrainCallFilter)
                {
                    SetGrainCancellationTokensTarget(reference, request);
                    requestTransferred = true;
                    this.RuntimeClient.SendRequest(reference, request, context: null, options);
                }
                else
                {
                    InvokeMethodWithFiltersAsync(reference, request, options).AsTask().Ignore();
                    requestTransferred = true;
                }
            }
            finally
            {
                if (!requestTransferred)
                {
                    DisposeRequest(request);
                }
            }
        }

        private async ValueTask<TResult?> InvokeMethodWithFiltersAsync<TResult>(GrainReference reference, IInvokable request, InvokeMethodOptions options)
        {
            try
            {
                SetGrainCancellationTokensTarget(reference, request);
                var invoker = new OutgoingCallInvoker<TResult>(reference, request, options, this.sendRequest, this.filters);
                await invoker.Invoke();
                return invoker.TypedResult;
            }
            finally
            {
                DisposeRequest(request);
            }
        }

        private async ValueTask InvokeMethodWithFiltersAsync(GrainReference reference, IInvokable request, InvokeMethodOptions options)
        {
            try
            {
                SetGrainCancellationTokensTarget(reference, request);
                var invoker = new OutgoingCallInvoker<object>(reference, request, options, this.sendRequest, this.filters);
                await invoker.Invoke();
            }
            finally
            {
                DisposeRequest(request);
            }
        }

        private ValueTask<TResult?> InvokeMethodAsyncCore<TResult>(GrainReference reference, IInvokable request, InvokeMethodOptions options)
        {
            ResponseCompletionSource<TResult>? responseCompletionSource = null;
            var requestTransferred = false;
            try
            {
                SetGrainCancellationTokensTarget(reference, request);
                responseCompletionSource = ResponseCompletionSourcePool.Get<TResult>();
                requestTransferred = true;
                this.RuntimeClient.SendRequest(reference, request, responseCompletionSource, options);
                return responseCompletionSource.AsValueTask();
            }
            catch
            {
                responseCompletionSource?.Reset();
                if (!requestTransferred)
                {
                    DisposeRequest(request);
                }

                throw;
            }
        }

        private ValueTask InvokeMethodAsyncCore(GrainReference reference, IInvokable request, InvokeMethodOptions options)
        {
            ResponseCompletionSource? responseCompletionSource = null;
            var requestTransferred = false;
            try
            {
                SetGrainCancellationTokensTarget(reference, request);
                responseCompletionSource = ResponseCompletionSourcePool.Get();
                requestTransferred = true;
                this.RuntimeClient.SendRequest(reference, request, responseCompletionSource, options);
                return responseCompletionSource.AsVoidValueTask();
            }
            catch
            {
                responseCompletionSource?.Reset();
                if (!requestTransferred)
                {
                    DisposeRequest(request);
                }

                throw;
            }
        }

        private static void DisposeRequest(IInvokable request)
        {
            if (request is RequestBase)
            {
                request.Dispose();
            }
        }

        private void SendFilteredRequest(
            GrainReference reference,
            IResponseCompletionSource callback,
            IInvokable request,
            InvokeMethodOptions options)
        {
            var messageRequest = request is RequestBase
                ? (IInvokable)this.requestCopier.Copy(request)!
                : request;
            RuntimeClient.SendRequest(reference, messageRequest, callback, options);
        }

        public object Cast(IAddressable grain, Type grainInterface)
        {
            var grainId = grain.GetGrainId();
            if (grain is GrainReference && grainInterface.IsAssignableFrom(grain.GetType()))
            {
                return grain;
            }

            var interfaceType = this.interfaceTypeResolver.GetGrainInterfaceType(grainInterface);
            return this.referenceActivator.CreateReference(grainId, interfaceType);
        }

        /// <summary>
        /// Sets target grain to the found instances of type GrainCancellationToken
        /// </summary>
        private void SetGrainCancellationTokensTarget(GrainReference target, IInvokable request)
        {
            var argumentCount = request.GetArgumentCount();
            for (var i = 0; i < argumentCount; i++)
            {
                var arg = request.GetArgument(i);
                if (arg is not GrainCancellationToken grainToken)
                {
                    continue;
                }

                grainToken.AddGrainReference(this.cancellationTokenRuntime, target);
            }
        }
    }
}
