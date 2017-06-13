using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using Orleans.Providers;

namespace Orleans.Runtime
{
    /// <summary>
    /// Invokes a request on a grain.
    /// </summary>
    internal class GrainMethodInvoker : IGrainCallContext, IGrainMethodInvoker
    {
        private readonly InvokeMethodRequest request;
        private readonly IGrainMethodInvoker rootInvoker;
        private readonly List<IGrainCallFilter> filters;
        private readonly InvokeInterceptor deprecatedInvokeInterceptor;
        private readonly InterfaceToImplementationMappingCache interfaceToImplementationMapping;
        private int stage;

        /// <summary>
        /// Initializes a new instance of the <see cref="GrainMethodInvoker"/> class.
        /// </summary>
        /// <param name="grain">The grain.</param>
        /// <param name="request">The request.</param>
        /// <param name="rootInvoker">The generated invoker.</param>
        /// <param name="filters">The invocation interceptors.</param>
        /// <param name="interfaceToImplementationMapping">The implementation map.</param>
        /// <param name="invokeInterceptor">The deprecated silo-wide interceptor.</param>
        public GrainMethodInvoker(
            IAddressable grain,
            InvokeMethodRequest request,
            IGrainMethodInvoker rootInvoker,
            List<IGrainCallFilter> filters,
            InterfaceToImplementationMappingCache interfaceToImplementationMapping,
            InvokeInterceptor invokeInterceptor)
        {
            this.request = request;
            this.rootInvoker = rootInvoker;
            this.Grain = grain;
            this.filters = filters;
            this.deprecatedInvokeInterceptor = invokeInterceptor;
            this.interfaceToImplementationMapping = interfaceToImplementationMapping;
        }

        /// <inheritdoc />
        public IAddressable Grain { get; }

        /// <inheritdoc />
        public MethodInfo Method
        {
            get
            {
                // Determine if the object being invoked is a grain or a grain extension.
                Type implementationType;
                var extensionMap = this.rootInvoker as IGrainExtensionMap;
                IGrainExtension extension;
                if (extensionMap != null && extensionMap.TryGetExtension(request.InterfaceId, out extension))
                {
                    implementationType = extension.GetType();
                }
                else
                {
                    implementationType = this.Grain.GetType();
                }

                // Get or create the implementation map for this object.
                var implementationMap = interfaceToImplementationMapping.GetOrCreate(
                    implementationType,
                    request.InterfaceId);

                // Get the method info for the method being invoked.
                MethodInfo methodInfo;
                implementationMap.TryGetValue(request.MethodId, out methodInfo);
                return methodInfo;
            }
        }

        /// <inheritdoc />
        public object[] Arguments => request.Arguments;
        
        /// <inheritdoc />
        public object Result { get; set; }

        /// <inheritdoc />
        public async Task Invoke()
        {
            // Execute each stage in the pipeline. Each successive call to this method will invoke the next stage.
            // Stages which are not implemented (eg, because the user has not specified an interceptor) are skipped.
            var numFilters = filters.Count;
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
                stage++;

                // Deprecated silo-level invoker, if present.
                var grain = this.Grain as IGrain;
                if (grain != null && deprecatedInvokeInterceptor != null)
                {
                    this.Result =
                        await deprecatedInvokeInterceptor.Invoke(this.Method, this.request, grain, this);
                    return;
                }
            }

            if (stage == numFilters + 1)
            {
                stage++;

                // Grain-level invoker, if present.
                var grainClassLevelFilter = this.Grain as IGrainCallFilter;
                if (grainClassLevelFilter != null)
                {
                    await grainClassLevelFilter.Invoke(this);
                    return;
                }
            }

#pragma warning disable 618
            if (stage == numFilters + 2)
            {
                stage++;

                // Deprecated grain-level invoker, if present.
                var intercepted = this.Grain as IGrainInvokeInterceptor;
                if (intercepted != null)
                {
                    this.Result = await intercepted.Invoke(this.Method, this.request, this);
                    return;
                }
            }
#pragma warning restore 618

            if (stage == numFilters + 3)
            {
                // Finally call the root-level invoker.
                stage++;
                this.Result = await rootInvoker.Invoke(this.Grain, this.request);
                return;
            }

            // If this method has been called more than the expected number of times, that is invalid.
            ThrowInvalidCall();
        }
        
        int IGrainMethodInvoker.InterfaceId => this.rootInvoker.InterfaceId;

        ushort IGrainMethodInvoker.InterfaceVersion => this.rootInvoker.InterfaceVersion;

        async Task<object> IGrainMethodInvoker.Invoke(IAddressable grain, InvokeMethodRequest invokeMethodRequest)
        {
            ValidateArguments(grain, invokeMethodRequest);
            await this.Invoke();
            return this.Result;
        }

        private void ValidateArguments(IAddressable grain, InvokeMethodRequest invokeMethodRequest)
        {
            if (!Equals(this.Grain, grain))
                throw new ArgumentException($"Provided {nameof(IAddressable)} differs from expected value",
                    nameof(grain));
            if (!Equals(this.request, invokeMethodRequest))
                throw new ArgumentException($"Provided {nameof(InvokeMethodRequest)} differs from expected value",
                    nameof(invokeMethodRequest));
        }

        private static void ThrowInvalidCall()
        {
            throw new InvalidOperationException(
                $"{nameof(GrainMethodInvoker)}.{nameof(Invoke)}() received an invalid call.");
        }
    }
}
