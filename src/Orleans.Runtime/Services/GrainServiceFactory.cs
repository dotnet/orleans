using Orleans.Services;

namespace Orleans.Runtime
{
    /// <summary>
    /// Functionality for interacting with grain services.
    /// </summary>
    public interface IGrainServiceFactory
    {
        /// <summary>
        /// Casts a grain reference to a typed grain service reference.
        /// Used by grain indexing.
        /// </summary>
        /// <typeparam name="T">The grain service interface.</typeparam>
        /// <param name="grainReference">The grain reference.</param>
        /// <returns>A reference to the specified grain service.</returns>
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
