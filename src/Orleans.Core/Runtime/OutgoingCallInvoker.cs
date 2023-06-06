using System;
using System.Reflection;
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using Orleans.Serialization.Invocation;

namespace Orleans.Runtime
{
    /// <summary>
    /// Invokes a request on a grain reference.
    /// </summary>
    internal sealed class OutgoingCallInvoker<TResult> : IOutgoingGrainCallContext
    {
        private readonly InvokeMethodOptions options;
        private readonly Action<GrainReference, IResponseCompletionSource, IInvokable, InvokeMethodOptions> sendRequest;
        private readonly IOutgoingGrainCallFilter[] filters;
        private readonly int stages;
        private readonly GrainReference grainReference;
        private readonly IOutgoingGrainCallFilter requestFilter;
        private int stage;

        /// <summary>
        /// Initializes a new instance of the <see cref="OutgoingCallInvoker{TResult}"/> class.
        /// </summary>
        /// <param name="grain">The grain reference.</param>
        /// <param name="request">The request.</param>
        /// <param name="options"></param>
        /// <param name="sendRequest"></param>
        /// <param name="filters">The invocation interceptors.</param>
        public OutgoingCallInvoker(
            GrainReference grain,
            IInvokable request,
            InvokeMethodOptions options,
            Action<GrainReference, IResponseCompletionSource, IInvokable, InvokeMethodOptions> sendRequest,
            IOutgoingGrainCallFilter[] filters)
        {
            this.Request = request;
            this.options = options;
            this.sendRequest = sendRequest;
            grainReference = grain;
            this.filters = filters;
            stages = filters.Length;
            SourceContext = RuntimeContext.Current;

            if (request is IOutgoingGrainCallFilter requestFilter)
            {
                this.requestFilter = requestFilter;
                ++stages;
            }
        }

        public IInvokable Request { get; }

        public object Grain => grainReference;

        public MethodInfo InterfaceMethod => Request.GetMethod();

        public object Result { get => TypedResult; set => TypedResult = (TResult)value; }

        public Response Response { get; set; }

        public TResult TypedResult { get => Response.GetResult<TResult>(); set => Response = Response.FromResult(value); }

        public IGrainContext SourceContext { get; }

        public GrainId? SourceId => SourceContext?.GrainId;

        public GrainId TargetId => grainReference.GrainId;

        public GrainInterfaceType InterfaceType => grainReference.InterfaceType;

        public string InterfaceName => Request.GetInterfaceName();

        public string MethodName => Request.GetMethodName();

        public async Task Invoke()
        {
            try
            {
                // Execute each stage in the pipeline. Each successive call to this method will invoke the next stage.
                // Stages which are not implemented (eg, because the user has not specified an interceptor) are skipped.
                if (stage < filters.Length)
                {
                    // Call each of the specified interceptors.
                    var systemWideFilter = filters[stage];
                    stage++;
                    await systemWideFilter.Invoke(this);

                    // If Response is null some filter did not continue the call chain
                    if (Response is null)
                    {
                        ThrowBrokenCallFilterChain(systemWideFilter.GetType().Name);
                    }

                    return;
                }
                else if (stage < stages)
                {
                    stage++;
                    await requestFilter.Invoke(this);

                    // If Response is null some filter did not continue the call chain
                    if (Response is null)
                    {
                        ThrowBrokenCallFilterChain(requestFilter.GetType().Name);
                    }

                    return;
                }
                else if (stage == stages)
                {
                    // Finally call the root-level invoker.
                    stage++;
                    var responseCompletionSource = ResponseCompletionSourcePool.Get();
                    sendRequest(grainReference, responseCompletionSource, Request, options);
                    Response = await responseCompletionSource.AsValueTask();

                    return;
                }
            }
            finally
            {
                stage--;
            }

            // If this method has been called more than the expected number of times, that is invalid.
            ThrowInvalidCall();
        }

        private static void ThrowInvalidCall() => throw new InvalidOperationException($"{typeof(OutgoingCallInvoker<TResult>)}.{nameof(Invoke)}() received an invalid call.");

        private static void ThrowBrokenCallFilterChain(string filterName) => throw new InvalidOperationException($"{typeof(OutgoingCallInvoker<TResult>)}.{nameof(Invoke)}() invoked a broken filter: {filterName}.");
    }
}