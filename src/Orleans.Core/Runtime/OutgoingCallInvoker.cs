using System;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using Orleans.Serialization.Invocation;

namespace Orleans.Runtime
{
    /// <summary>
    /// Invokes a request on a grain reference.
    /// </summary>
    internal class OutgoingCallInvoker<TResult> : IOutgoingGrainCallContext, IMethodArguments
    {
        private readonly IInvokable request;
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
            this.request = request;
            this.options = options;
            this.sendRequest = sendRequest;
            this.grainReference = grain;
            this.filters = filters;
            this.stages = filters.Length;
            if (request is IOutgoingGrainCallFilter requestFilter)
            {
                this.requestFilter = requestFilter;
                ++this.stages;
            }
        }

        /// <inheritdoc />
        public IAddressable Grain => this.grainReference;

        /// <inheritdoc />
        public MethodInfo Method => request.Method;

        /// <inheritdoc />
        public MethodInfo InterfaceMethod => this.Method;

        /// <inheritdoc />
        public IMethodArguments Arguments => this;

        /// <inheritdoc />
        public object Result { get => TypedResult; set => TypedResult = (TResult)value; }

        /// <inheritdoc />
        public Response Response { get; set; }

        /// <inheritdoc />
        public TResult TypedResult { get => Response.GetResult<TResult>(); set => Response = Response.FromResult(value); }

        object IMethodArguments.this[int index]
        {
            get => request.GetArgument<object>(index);
            set => request.SetArgument(index, value);
        }

        T IMethodArguments.GetArgument<T>(int index) => request.GetArgument<T>(index);

        void IMethodArguments.SetArgument<T>(int index, T value) => request.SetArgument(index, value);

        int IMethodArguments.Length => request.ArgumentCount;

        /// <inheritdoc />
        public async Task Invoke()
        {
            try
            {
                // Execute each stage in the pipeline. Each successive call to this method will invoke the next stage.
                // Stages which are not implemented (eg, because the user has not specified an interceptor) are skipped.
                if (stage < this.filters.Length)
                {
                    // Call each of the specified interceptors.
                    var systemWideFilter = this.filters[stage];
                    stage++;
                    await systemWideFilter.Invoke(this);
                    return;
                }
                else if (stage < this.stages)
                {
                    stage++;
                    await this.requestFilter.Invoke(this);
                    return;
                }
                else if (stage == this.stages)
                {
                    // Finally call the root-level invoker.
                    stage++;
                    var responseCompletionSource = ResponseCompletionSourcePool.Get();
                    try
                    {
                        this.sendRequest(this.grainReference, responseCompletionSource, this.request, this.options);
                        this.Response = await responseCompletionSource.AsValueTask();

                        // Rethrow exceptions for filters to optionally handle.
                        if (this.Response.Exception is { } exception)
                        {
                            ExceptionDispatchInfo.Capture(exception).Throw();
                        }
                    }
                    finally
                    {
                        ResponseCompletionSourcePool.Return(responseCompletionSource);
                    }

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

        private static void ThrowInvalidCall()
        {
            throw new InvalidOperationException($"{typeof(OutgoingCallInvoker<TResult>)}.{nameof(Invoke)}() received an invalid call.");
        }
    }
}