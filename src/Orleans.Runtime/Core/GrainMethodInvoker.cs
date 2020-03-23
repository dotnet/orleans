using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Orleans.CodeGeneration;

namespace Orleans.Runtime
{
    /// <summary>
    /// Invokes a request on a grain.
    /// </summary>
    internal class GrainMethodInvoker : IIncomingGrainCallContext, IGrainMethodInvoker
    {
        private readonly InvokeMethodRequest request;
        private readonly IGrainMethodInvoker rootInvoker;
        private readonly List<IIncomingGrainCallFilter> filters;
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
        public GrainMethodInvoker(
            IAddressable grain,
            InvokeMethodRequest request,
            IGrainMethodInvoker rootInvoker,
            List<IIncomingGrainCallFilter> filters,
            InterfaceToImplementationMappingCache interfaceToImplementationMapping)
        {
            this.request = request;
            this.rootInvoker = rootInvoker;
            this.Grain = grain;
            this.filters = filters;
            this.interfaceToImplementationMapping = interfaceToImplementationMapping;
        }

        /// <inheritdoc />
        public IAddressable Grain { get; }

        /// <inheritdoc />
        public MethodInfo Method => GetMethodEntry().ImplementationMethod;

        /// <inheritdoc />
        public MethodInfo InterfaceMethod => GetMethodEntry().InterfaceMethod;

        /// <inheritdoc />
        public MethodInfo ImplementationMethod => GetMethodEntry().ImplementationMethod;

        /// <inheritdoc />
        public object[] Arguments => request.Arguments;
        
        /// <inheritdoc />
        public object Result { get; set; }

        /// <inheritdoc />
        public async Task Invoke()
        {
            try
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

                    // Grain-level invoker, if present.
                    if (this.Grain is IIncomingGrainCallFilter grainClassLevelFilter)
                    {
                        await grainClassLevelFilter.Invoke(this);
                        return;
                    }
                }

                if (stage == numFilters + 1)
                {
                    // Finally call the root-level invoker.
                    stage++;
                    this.Result = await rootInvoker.Invoke(this.Grain, this.request);
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
        
        int IGrainMethodInvoker.InterfaceId => this.rootInvoker.InterfaceId;

        ushort IGrainMethodInvoker.InterfaceVersion => this.rootInvoker.InterfaceVersion;

        async Task<object> IGrainMethodInvoker.Invoke(IAddressable grain, InvokeMethodRequest invokeMethodRequest)
        {
            ValidateArguments(grain, invokeMethodRequest);
            await this.Invoke();
            return this.Result;
        }

        private InterfaceToImplementationMappingCache.Entry GetMethodEntry()
        {
            // Determine if the object being invoked is a grain or a grain extension.
            Type implementationType;
            if (this.rootInvoker is IGrainExtensionMap extensionMap && extensionMap.TryGetExtension(request.InterfaceId, out var extension))
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
            implementationMap.TryGetValue(request.MethodId, out var method);
            return method;
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
