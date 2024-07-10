using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Orleans.Serialization;
using Orleans.Serialization.Invocation;

namespace Orleans.Runtime
{
    /// <summary>
    /// Invokes a request on a grain.
    /// </summary>
    internal sealed class GrainMethodInvoker : IIncomingGrainCallContext
    {
        private readonly Message message;
        private readonly IInvokable request;
        private readonly List<IIncomingGrainCallFilter> filters;
        private readonly InterfaceToImplementationMappingCache interfaceToImplementationMapping;
        private readonly DeepCopier<Response> responseCopier;
        private readonly IGrainContext grainContext;
        private int stage;

        /// <summary>
        /// Initializes a new instance of the <see cref="GrainMethodInvoker"/> class.
        /// </summary>
        /// <param name="message">The message.</param>
        /// <param name="grainContext">The grain.</param>
        /// <param name="request">The request.</param>
        /// <param name="filters">The invocation interceptors.</param>
        /// <param name="interfaceToImplementationMapping">The implementation map.</param>
        /// <param name="responseCopier">The response copier.</param>
        public GrainMethodInvoker(
            Message message,
            IGrainContext grainContext,
            IInvokable request,
            List<IIncomingGrainCallFilter> filters,
            InterfaceToImplementationMappingCache interfaceToImplementationMapping,
            DeepCopier<Response> responseCopier)
        {
            this.message = message;
            this.request = request;
            this.grainContext = grainContext;
            this.filters = filters;
            this.interfaceToImplementationMapping = interfaceToImplementationMapping;
            this.responseCopier = responseCopier;
        }

        public IInvokable Request => request;

        public object Grain => grainContext.GrainInstance;

        public MethodInfo InterfaceMethod => request.GetMethod();

        public MethodInfo ImplementationMethod => GetMethodEntry().ImplementationMethod;

        public object Result
        {
            get => Response switch
            {
                { Exception: null } response => response.Result,
                _ => null
            };
            set => Response = Response.FromResult(value);
        }

        public Response Response { get; set; }

        public GrainId? SourceId => message.SendingGrain is { IsDefault: false } source ? source : null;

        public IGrainContext TargetContext => grainContext;

        public GrainId TargetId => grainContext.GrainId;

        public GrainInterfaceType InterfaceType => message.InterfaceType;

        public string InterfaceName => request.GetInterfaceName();

        public string MethodName => request.GetMethodName();

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

                    // If Response is null some filter did not continue the call chain
                    if (this.Response is null)
                    {
                        ThrowBrokenCallFilterChain(systemWideFilter.GetType().Name);
                    }

                    return;
                }

                if (stage == numFilters)
                {
                    stage++;

                    // Grain-level invoker, if present.
                    if (this.Grain is IIncomingGrainCallFilter grainClassLevelFilter)
                    {
                        await grainClassLevelFilter.Invoke(this);

                        // If Response is null some filter did not continue the call chain
                        if (this.Response is null)
                        {
                            ThrowBrokenCallFilterChain(this.Grain.GetType().Name);
                        }
                        return;
                    }
                }

                if (stage == numFilters + 1)
                {
                    // Finally call the root-level invoker.
                    stage++;
                    this.Response = await request.Invoke();

                    // Propagate exceptions to other filters.
                    if (this.Response.Exception is { } exception)
                    {
                        ExceptionDispatchInfo.Capture(exception).Throw();
                    }

                    this.Response = this.responseCopier.Copy(this.Response);

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
            throw new InvalidOperationException(
                $"{nameof(GrainMethodInvoker)}.{nameof(Invoke)}() received an invalid call.");
        }

        private static void ThrowBrokenCallFilterChain(string filterName)
        {
            throw new InvalidOperationException($"{nameof(GrainMethodInvoker)}.{nameof(Invoke)}() invoked a broken filter: {filterName}.");
        }


        private (MethodInfo ImplementationMethod, MethodInfo InterfaceMethod) GetMethodEntry()
        {
            var interfaceType = this.request.GetInterfaceType();
            var implementationType = this.request.GetTarget().GetType();

            // Get or create the implementation map for this object.
            var implementationMap = interfaceToImplementationMapping.GetOrCreate(
                implementationType,
                interfaceType);

            // Get the method info for the method being invoked.
            var method = request.GetMethod();
            if (method.IsConstructedGenericMethod)
            {
                if (implementationMap.TryGetValue(method.GetGenericMethodDefinition(), out var entry))
                {
                    return entry.GetConstructedGenericMethod(method);
                }
            }
            else if (implementationMap.TryGetValue(method, out var entry))
            {
                return (entry.ImplementationMethod, entry.InterfaceMethod);
            }

            Debug.Assert(false, "Method entry not found");
            return default;
        }
    }
}
