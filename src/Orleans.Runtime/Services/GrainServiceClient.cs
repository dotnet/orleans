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
        private readonly IInternalGrainFactory grainFactory;
        private readonly IConsistentRingProvider ringProvider;
        private readonly GrainType grainType;

        /// <summary>
        /// Currently we only support a single GrainService per Silo, when multiple are supported we will request the number of GrainServices to partition per silo here.
        /// </summary>
        /// <param name="serviceProvider">The service provider.</param>
        protected GrainServiceClient(IServiceProvider serviceProvider)
        {
            grainFactory = serviceProvider.GetRequiredService<IInternalGrainFactory>();
            ringProvider = serviceProvider.GetRequiredService<IConsistentRingProvider>();

            // GrainInterfaceMap only holds IGrain types, not ISystemTarget types, so resolved via Orleans.CodeGeneration.
            // Resolve this before merge.
            var grainTypeCode = GrainInterfaceUtils.GetGrainClassTypeCode(typeof(TGrainService));
            grainType = SystemTargetGrainId.CreateGrainServiceGrainType(grainTypeCode, null);
        }

        /// <summary>
        /// Get a reference to the <see cref="GrainService"/> responsible for actioning the request based on the <see cref="CallingGrainReference"/>
        /// </summary>
        protected TGrainService GrainService
        {
            get
            {
                var destination = MapGrainReferenceToSiloRing(CallingGrainReference);
                var grainId = SystemTargetGrainId.CreateGrainServiceGrainId(grainType, destination);
                var grainService = grainFactory.GetSystemTarget<TGrainService>(grainId);

                return grainService;
            }
        }

        /// <summary>
        /// Gets a reference to the the currently executing grain.
        /// </summary>
        protected GrainReference CallingGrainReference => RuntimeContext.Current?.GrainReference;

        private SiloAddress MapGrainReferenceToSiloRing(GrainReference grainRef)
        {
            var hashCode = grainRef.GetUniformHashCode();
            return ringProvider.GetPrimaryTargetSilo(hashCode);
        }
    }
}
