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
    internal class GrainMethodInvoker : IIncomingGrainCallContext
    {
        private readonly InvokeMethodRequest request;
        private readonly IGrainMethodInvoker rootInvoker;
        private readonly List<IIncomingGrainCallFilter> filters;
        private readonly InterfaceToImplementationMappingCache interfaceToImplementationMapping;
        private readonly IGrainContext grainContext;
        private int stage;

        /// <summary>
        /// Initializes a new instance of the <see cref="GrainMethodInvoker"/> class.
        /// </summary>
        /// <param name="grainContext">The grain.</param>
        /// <param name="request">The request.</param>
        /// <param name="rootInvoker">The generated invoker.</param>
        /// <param name="filters">The invocation interceptors.</param>
        /// <param name="interfaceToImplementationMapping">The implementation map.</param>
        public GrainMethodInvoker(
            IGrainContext grainContext,
            InvokeMethodRequest request,
            IGrainMethodInvoker rootInvoker,
            List<IIncomingGrainCallFilter> filters,
            InterfaceToImplementationMappingCache interfaceToImplementationMapping)
        {
            this.request = request;
            this.rootInvoker = rootInvoker;
            this.grainContext = grainContext;
            this.filters = filters;
            this.interfaceToImplementationMapping = interfaceToImplementationMapping;
        }

        /// <inheritdoc />
        public IAddressable Grain => this.grainContext.GrainInstance;

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
                    this.Result = await rootInvoker.Invoke(this.grainContext, this.request);
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
        
        private InterfaceToImplementationMappingCache.Entry GetMethodEntry()
        {
            // Determine if the object being invoked is a grain or a grain extension.
            var interfaceType = this.rootInvoker?.InterfaceType;
            var implementationType = this.rootInvoker?.GetTarget(this.grainContext)?.GetType();

            // Get or create the implementation map for this object.
            var implementationMap = interfaceToImplementationMapping.GetOrCreate(
                implementationType,
                interfaceType);

            // Get the method info for the method being invoked.
            if (!implementationMap.TryGetValue(request.MethodId, out var method))
            {
                false.ToString();
            }

            if (method.InterfaceMethod is null)
            {
                false.ToString();
            }

            return method;
        }

        private static void ThrowInvalidCall()
        {
            throw new InvalidOperationException(
                $"{nameof(GrainMethodInvoker)}.{nameof(Invoke)}() received an invalid call.");
        }
    }
}
