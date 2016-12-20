using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.CodeGeneration;
using Orleans.Runtime.ConsistentRing;
using Orleans.Services;

namespace Orleans.Runtime.Services
{
    /// <summary>
    /// Proxies requests to the appropriate GrainService based on the appropriate Ring partitioning strategy.
    /// </summary>
    /// <typeparam name="TGrainService"></typeparam>
    public abstract class GrainServiceClient<TGrainService> : IGrainServiceClient<TGrainService> where TGrainService : IGrainService
    {
        private readonly ISiloRuntimeClient runtimeClient;
        private readonly IInternalGrainFactory grainFactory;
        private readonly IConsistentRingProvider ringProvider;
        private readonly int grainTypeCode;

        /// <summary>
        /// Currently we only support a single GrainService per Silo, when multiple are supported we will request the number of GrainServices to partition per silo here.
        /// </summary>
        protected GrainServiceClient(IServiceProvider serviceProvider)
        {
            runtimeClient =  serviceProvider.GetRequiredService<ISiloRuntimeClient>();
            grainFactory = serviceProvider.GetRequiredService<IInternalGrainFactory>();
            ringProvider = serviceProvider.GetRequiredService<IConsistentRingProvider>();

            // GrainInterfaceMap only holds IGrain types, not ISystemTarget types, so resolved via Orleans.CodeGeneration.
            // Resolve this before merge.
            grainTypeCode = GrainInterfaceUtils.GetGrainClassTypeCode(typeof(TGrainService));
        }

        /// <summary>
        /// Resolves the correct GrainService responsible for actioning the request based on the CallingGrainReference
        /// </summary>
        protected TGrainService GrainService
        {
            get
            {
                // Currently we only support a single GrainService per Silo, hence we pass Zero here instead of partitioning.
                var grainId = GrainId.GetGrainServiceGrainId(0, grainTypeCode);

                var destination = MapGrainReferenceToSiloRing(CallingGrainReference);

                var grainService = grainFactory.GetSystemTarget<TGrainService>(grainId, destination);

                return grainService;
            }
        }
        
        /// <summary>
        /// Resolves the Grain Reference invoking this request.
        /// </summary>
        protected GrainReference CallingGrainReference
        {
            get
            {
                return runtimeClient.CurrentActivationData.GrainReference;
            }
        }

        /// <summary>
        /// Moved from InsideRuntimeClient.cs
        /// </summary>
        /// <param name="grainRef"></param>
        /// <returns></returns>
        private SiloAddress MapGrainReferenceToSiloRing(GrainReference grainRef)
        {
            var hashCode = grainRef.GetUniformHashCode();
            return ringProvider.GetPrimaryTargetSilo(hashCode);
        }
    }
}
