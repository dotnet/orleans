using System;
using System.Reflection;
using System.Threading.Tasks;
using Orleans.CodeGeneration;

namespace Orleans.Runtime
{
    /// <summary>
    /// Invokes a request on a grain reference.
    /// </summary>
    internal class OutgoingCallInvoker : IOutgoingGrainCallContext
    {
        private readonly InvokeMethodRequest request;
        private readonly InvokeMethodOptions options;
        private readonly string debugContext;
        private readonly Func<GrainReference, InvokeMethodRequest, string, InvokeMethodOptions, Task<object>> sendRequest;
        private readonly InterfaceToImplementationMappingCache mapping;
        private readonly IOutgoingGrainCallFilter[] filters;
        private readonly GrainReference grainReference;
        private int stage;

        /// <summary>
        /// Initializes a new instance of the <see cref="OutgoingCallInvoker"/> class.
        /// </summary>
        /// <param name="grain">The grain reference.</param>
        /// <param name="request">The request.</param>
        /// <param name="options"></param>
        /// <param name="debugContext"></param>
        /// <param name="sendRequest"></param>
        /// <param name="filters">The invocation interceptors.</param>
        /// <param name="mapping">The implementation map.</param>
        public OutgoingCallInvoker(
            GrainReference grain,
            InvokeMethodRequest request,
            InvokeMethodOptions options,
            string debugContext,
            Func<GrainReference, InvokeMethodRequest, string, InvokeMethodOptions, Task<object>> sendRequest,
            InterfaceToImplementationMappingCache mapping,
            IOutgoingGrainCallFilter[] filters)
        {
            this.request = request;
            this.options = options;
            this.debugContext = debugContext;
            this.sendRequest = sendRequest;
            this.mapping = mapping;
            this.grainReference = grain;
            this.filters = filters;
        }

        /// <inheritdoc />
        public IAddressable Grain => this.grainReference;

        /// <inheritdoc />
        public MethodInfo Method
        {
            get
            {
                var implementationType = this.grainReference.GetType();

                // Get or create the implementation map for this object.
                var implementationMap = mapping.GetOrCreate(
                    implementationType,
                    request.InterfaceId);

                // Get the method info for the method being invoked.
                implementationMap.TryGetValue(request.MethodId, out var method);
                return method.InterfaceMethod;
            }
        }

        /// <inheritdoc />
        public MethodInfo InterfaceMethod => this.Method;

        /// <inheritdoc />
        public object[] Arguments => request.Arguments;

        /// <inheritdoc />
        public object Result { get; set; }

        /// <inheritdoc />
        public async Task Invoke()
        {
            // Execute each stage in the pipeline. Each successive call to this method will invoke the next stage.
            // Stages which are not implemented (eg, because the user has not specified an interceptor) are skipped.
            var numFilters = filters.Length;
            if (stage < numFilters)
            {
                // Call each of the specified interceptors.
                var systemWideFilter = this.filters[stage];
                stage++;
                await systemWideFilter.Invoke(this);
                return;
            }

            if (stage == numFilters)
            {
                // Finally call the root-level invoker.
                stage++;
                var resultTask = this.sendRequest(this.grainReference, this.request, this.debugContext, this.options);
                if (resultTask != null)
                {
                    this.Result = await resultTask;
                }

                return;
            }

            // If this method has been called more than the expected number of times, that is invalid.
            ThrowInvalidCall();
        }

        private static void ThrowInvalidCall()
        {
            throw new InvalidOperationException(
                $"{nameof(OutgoingCallInvoker)}.{nameof(Invoke)}() received an invalid call.");
        }
    }
}