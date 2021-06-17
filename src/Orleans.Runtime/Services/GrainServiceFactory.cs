using Orleans.Services;

namespace Orleans.Runtime
{
    public interface IGrainServiceFactory
    {
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

        public T CastToGrainServiceReference<T>(GrainReference grainReference) where T : IGrainService
            => this.runtimeClient.InternalGrainFactory.GetSystemTarget<T>(grainReference.GrainId);
    }
}
