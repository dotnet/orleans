using Microsoft.Extensions.Logging;
using Orleans.CodeGeneration;
using Orleans.GrainReferences;
using Orleans.Metadata;
using Orleans.Serialization.Invocation;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace Orleans.Runtime
{
    internal class GrainReferenceRuntime : IGrainReferenceRuntime
    {
        private readonly ILogger<GrainReferenceRuntime> _logger;
        private readonly GrainReferenceActivator referenceActivator;
        private readonly GrainInterfaceTypeResolver interfaceTypeResolver;
        private readonly IGrainCancellationTokenRuntime cancellationTokenRuntime;
        private readonly IOutgoingGrainCallFilter[] filters;
        private readonly Action<GrainReference, IResponseCompletionSource, IInvokable, InvokeMethodOptions> sendRequest;

        public GrainReferenceRuntime(
            IRuntimeClient runtimeClient,
            IGrainCancellationTokenRuntime cancellationTokenRuntime,
            IEnumerable<IOutgoingGrainCallFilter> outgoingCallFilters,
            GrainReferenceActivator referenceActivator,
            GrainInterfaceTypeResolver interfaceTypeResolver,
            ILogger<GrainReferenceRuntime> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.RuntimeClient = runtimeClient;
            this.cancellationTokenRuntime = cancellationTokenRuntime;
            this.referenceActivator = referenceActivator;
            this.interfaceTypeResolver = interfaceTypeResolver;
            this.filters = outgoingCallFilters.ToArray();
            this.sendRequest = (GrainReference reference, IResponseCompletionSource callback, IInvokable body, InvokeMethodOptions options) => RuntimeClient.SendRequest(reference, body, callback, options);
        }

        public IRuntimeClient RuntimeClient { get; private set; }

        public ValueTask<TResult?> InvokeMethodAsync<TResult>(GrainReference reference, IInvokable request, InvokeMethodOptions options)
        {
            // TODO: Remove expensive interface type check
            if (this.filters.Length == 0 && request is not IOutgoingGrainCallFilter)
            {
                var owner = RetainArgumentResources(request);
                try
                {
                    SetGrainCancellationTokensTarget(reference, request);
                    var responseCompletionSource = ResponseCompletionSourcePool.Get<TResult>();
                    this.RuntimeClient.SendRequest(reference, request, responseCompletionSource, options);
                    return responseCompletionSource.AsValueTask();
                }
                catch
                {
                    InvokableArgumentResources.Complete(owner, _logger);
                    throw;
                }
                finally
                {
                    InvokableArgumentResources.Release(owner, _logger);
                }
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
                var owner = RetainArgumentResources(request);
                try
                {
                    SetGrainCancellationTokensTarget(reference, request);
                    var responseCompletionSource = ResponseCompletionSourcePool.Get();
                    this.RuntimeClient.SendRequest(reference, request, responseCompletionSource, options);
                    return responseCompletionSource.AsVoidValueTask();
                }
                catch
                {
                    InvokableArgumentResources.Complete(owner, _logger);
                    throw;
                }
                finally
                {
                    InvokableArgumentResources.Release(owner, _logger);
                }
            }
            else
            {
                return InvokeMethodWithFiltersAsync(reference, request, options);
            }
        }

        public void InvokeMethod(GrainReference reference, IInvokable request, InvokeMethodOptions options)
        {
            Debug.Assert((options & InvokeMethodOptions.OneWay) != 0);

            // TODO: Remove expensive interface type check
            if (filters.Length == 0 && request is not IOutgoingGrainCallFilter)
            {
                var owner = RetainArgumentResources(request);
                try
                {
                    SetGrainCancellationTokensTarget(reference, request);
                    this.RuntimeClient.SendRequest(reference, request, context: null, options);
                }
                catch
                {
                    InvokableArgumentResources.Complete(owner, _logger);
                    throw;
                }
                finally
                {
                    InvokableArgumentResources.Release(owner, _logger);
                }
            }
            else
            {
                InvokeMethodWithFiltersAsync(reference, request, options).AsTask().Ignore();
            }
        }

        private ValueTask<TResult?> InvokeMethodWithFiltersAsync<TResult>(GrainReference reference, IInvokable request, InvokeMethodOptions options) =>
            request is IInvokableArgumentOwner
                ? InvokeOwnedMethodWithFiltersAsync<TResult>(reference, request, options)
                : InvokeBorrowedMethodWithFiltersAsync<TResult>(reference, request, options);

        private ValueTask InvokeMethodWithFiltersAsync(GrainReference reference, IInvokable request, InvokeMethodOptions options) =>
            request is IInvokableArgumentOwner
                ? InvokeOwnedMethodWithFiltersAsync(reference, request, options)
                : InvokeBorrowedMethodWithFiltersAsync(reference, request, options);

        private async ValueTask<TResult?> InvokeBorrowedMethodWithFiltersAsync<TResult>(GrainReference reference, IInvokable request, InvokeMethodOptions options)
        {
            SetGrainCancellationTokensTarget(reference, request);
            var invoker = new OutgoingCallInvoker<TResult>(reference, request, options, this.sendRequest, this.filters);
            await invoker.Invoke();
            return invoker.TypedResult;
        }

        private async ValueTask InvokeBorrowedMethodWithFiltersAsync(GrainReference reference, IInvokable request, InvokeMethodOptions options)
        {
            SetGrainCancellationTokensTarget(reference, request);
            var invoker = new OutgoingCallInvoker<object>(reference, request, options, this.sendRequest, this.filters);
            await invoker.Invoke();
        }

        private async ValueTask<TResult?> InvokeOwnedMethodWithFiltersAsync<TResult>(GrainReference reference, IInvokable request, InvokeMethodOptions options)
        {
            var owner = RetainArgumentResources(request);
            var sent = false;
            try
            {
                SetGrainCancellationTokensTarget(reference, request);
                var invoker = new OutgoingCallInvoker<TResult>(reference, request, options, SendOwnedRequest, this.filters);
                await invoker.Invoke();
                return invoker.TypedResult;
            }
            catch
            {
                InvokableArgumentResources.Complete(owner, _logger);
                throw;
            }
            finally
            {
                // A filter can short-circuit without handing the request to the transport.
                if (!sent)
                {
                    InvokableArgumentResources.Complete(owner, _logger);
                }

                InvokableArgumentResources.Release(owner, _logger);
            }

            void SendOwnedRequest(GrainReference target, IResponseCompletionSource callback, IInvokable body, InvokeMethodOptions invocationOptions)
            {
                this.sendRequest(target, callback, body, invocationOptions);
                sent = true;
            }
        }

        private async ValueTask InvokeOwnedMethodWithFiltersAsync(GrainReference reference, IInvokable request, InvokeMethodOptions options)
        {
            var owner = RetainArgumentResources(request);
            var sent = false;
            try
            {
                SetGrainCancellationTokensTarget(reference, request);
                var invoker = new OutgoingCallInvoker<object>(reference, request, options, SendOwnedRequest, this.filters);
                await invoker.Invoke();
            }
            catch
            {
                InvokableArgumentResources.Complete(owner, _logger);
                throw;
            }
            finally
            {
                // A filter can short-circuit without handing the request to the transport.
                if (!sent)
                {
                    InvokableArgumentResources.Complete(owner, _logger);
                }

                InvokableArgumentResources.Release(owner, _logger);
            }

            void SendOwnedRequest(GrainReference target, IResponseCompletionSource callback, IInvokable body, InvokeMethodOptions invocationOptions)
            {
                this.sendRequest(target, callback, body, invocationOptions);
                sent = true;
            }
        }

        private static IInvokableArgumentOwner? RetainArgumentResources(IInvokable request)
        {
            var owner = request as IInvokableArgumentOwner;
            if (owner is not null && !owner.TryRetainArgumentResources())
            {
                throw new OperationCanceledException("The request's owned arguments have already completed.");
            }

            return owner;
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
