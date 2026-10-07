using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Orleans.Serialization;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Serializers;

namespace Orleans.Runtime
{
    /// <summary>
    /// Invokes a request on a grain.
    /// </summary>
    internal sealed class GrainMethodInvoker : GrainCallInvoker, IIncomingGrainCallContext
    {
        private readonly Message message;
        private readonly List<IIncomingGrainCallFilter> filters;
        private readonly InterfaceToImplementationMappingCache interfaceToImplementationMapping;
        private readonly DeepCopier<Response> responseCopier;
        private readonly IGrainContext grainContext;
        private readonly ICodecProvider codecProvider;
        private readonly CopyContextPool copyContexts;

        /// <summary>
        /// Initializes a new instance of the <see cref="GrainMethodInvoker"/> class.
        /// </summary>
        /// <param name="message">The message.</param>
        /// <param name="grainContext">The grain.</param>
        /// <param name="request">The request.</param>
        /// <param name="filters">The invocation interceptors.</param>
        /// <param name="interfaceToImplementationMapping">The implementation map.</param>
        /// <param name="responseCopier">The response copier.</param>
        /// <param name="codecProvider">The provider for generated response dependencies.</param>
        /// <param name="copyContexts">The pool for isolating source-known invocation results.</param>
        public GrainMethodInvoker(
            Message message,
            IGrainContext grainContext,
            IInvokable request,
            List<IIncomingGrainCallFilter> filters,
            InterfaceToImplementationMappingCache interfaceToImplementationMapping,
            DeepCopier<Response> responseCopier,
            ICodecProvider codecProvider,
            CopyContextPool copyContexts) : base(request)
        {
            this.message = message;
            this.grainContext = grainContext;
            this.filters = filters;
            this.interfaceToImplementationMapping = interfaceToImplementationMapping;
            this.responseCopier = responseCopier;
            this.codecProvider = codecProvider;
            this.copyContexts = copyContexts;
        }

        public override object Grain => grainContext.GrainInstance!;

        public MethodInfo ImplementationMethod => GetMethodEntry().ImplementationMethod;

        public override object? Result
        {
            get => Response switch
            {
                { Exception: null } response => response.Result,
                _ => null
            };
            set => Response = Response.FromResult(value);
        }

        public override GrainId? SourceId => message.SendingGrain is { IsDefault: false } source ? source : null;

        public IGrainContext TargetContext => grainContext;

        public override GrainId TargetId => grainContext.GrainId;

        public override GrainInterfaceType InterfaceType => message.InterfaceType;

        protected override int FilterCount => filters.Count + (Grain is IIncomingGrainCallFilter ? 1 : 0);

        protected override Task InvokeFilter(int index) => index < filters.Count
            ? filters[index].Invoke(this)
            : ((IIncomingGrainCallFilter)Grain).Invoke(this);

        protected override string GetFilterName(int index) => index < filters.Count
            ? filters[index].GetType().Name : Grain.GetType().Name;

        protected override async Task InvokeInner()
        {
            Response = await Request.InvokeAndCopy(codecProvider, copyContexts, responseCopier);
            if (Response.Exception is { } exception)
                ExceptionDispatchInfo.Capture(exception).Throw();
        }


        private (MethodInfo ImplementationMethod, MethodInfo InterfaceMethod) GetMethodEntry()
        {
            var interfaceType = Request.GetInterfaceType();
            var implementationType = Request.GetTarget()!.GetType();

            // Get or create the implementation map for this object.
            var implementationMap = interfaceToImplementationMapping.GetOrCreate(
                implementationType,
                interfaceType);

            // Get the method info for the method being invoked.
            var method = Request.GetMethod();
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
