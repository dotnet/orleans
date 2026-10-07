using System;
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using Orleans.Serialization.Invocation;

namespace Orleans.Runtime
{
    /// <summary>
    /// Invokes a request on a grain reference.
    /// </summary>
    internal sealed class OutgoingCallInvoker<TResult> : GrainCallInvoker, IOutgoingGrainCallContext
    {
        private readonly InvokeMethodOptions options;
        private readonly Action<GrainReference, IResponseCompletionSource, IInvokable, InvokeMethodOptions> sendRequest;
        private readonly IOutgoingGrainCallFilter[] filters;
        private readonly int stages;
        private readonly GrainReference grainReference;
        private readonly IOutgoingGrainCallFilter? requestFilter;

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
            IOutgoingGrainCallFilter[] filters) : base(request)
        {
            this.options = options;
            this.sendRequest = sendRequest;
            this.grainReference = grain;
            this.filters = filters;
            this.stages = filters.Length;
            SourceContext = RuntimeContext.Current;

            if (request is IOutgoingGrainCallFilter requestFilter)
            {
                this.requestFilter = requestFilter;
                ++this.stages;
            }
        }

        public override object Grain => this.grainReference;

        public override object? Result { get => TypedResult; set => TypedResult = (TResult?)value; }

        public TResult? TypedResult { get => Response!.GetResult<TResult>(); set => Response = Response.FromResult(value); }

        public IGrainContext? SourceContext { get; }

        public override GrainId? SourceId => SourceContext?.GrainId;

        public override GrainId TargetId => grainReference.GrainId;

        public override GrainInterfaceType InterfaceType => grainReference.InterfaceType;

        protected override int FilterCount => stages;

        protected override Task InvokeFilter(int index) => index < filters.Length
            ? filters[index].Invoke(this) : requestFilter!.Invoke(this);

        protected override string GetFilterName(int index) => index < filters.Length
            ? filters[index].GetType().Name : requestFilter!.GetType().Name;

        protected override async Task InvokeInner()
        {
            var completion = ResponseCompletionSourcePool.Get();
            sendRequest(grainReference, completion, Request, options);
            Response = await completion.AsValueTask().ConfigureAwait(false);
        }
    }
}
