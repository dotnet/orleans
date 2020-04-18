using Orleans.Services;

namespace Orleans.Runtime
{
    public interface IGrainServiceFactory
    {
        /// <summary>
        /// Creates a grain reference for a grain service instance on a given silo.
        /// This is used by grain indexing.
        /// </summary>
        GrainReference MakeGrainServiceReference(int typeData, string systemGrainId, SiloAddress siloAddress);

        /// <summary>
        /// Casts a grain reference to a typed grain service reference.
        /// Used by grain indexing.
        /// </summary>
        T CastToGrainServiceReference<T>(GrainReference grainReference) where T : IGrainService;
    }

    internal class GrainServiceFactory : IGrainServiceFactory
    {
        private readonly IRuntimeClient runtimeClient;

        public GrainServiceFactory(IRuntimeClient runtimeClient)
        {
            this.runtimeClient = runtimeClient;
        }

        public GrainReference MakeGrainServiceReference(int typeData, string systemGrainId, SiloAddress siloAddress)
            => GrainReference.FromGrainId(SystemTargetGrainId.CreateGrainServiceGrainId(typeData, systemGrainId, siloAddress), this.runtimeClient.GrainReferenceRuntime);

        public T CastToGrainServiceReference<T>(GrainReference grainReference) where T : IGrainService
            => this.runtimeClient.InternalGrainFactory.GetSystemTarget<T>(grainReference.GrainId);
    }
}
