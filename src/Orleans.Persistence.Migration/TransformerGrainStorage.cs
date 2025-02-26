using Orleans.Runtime;
using Orleans.Storage;

namespace Orleans.Persistence.Migration
{
    /// <summary>
    /// A specific grain storage implementation,
    /// which can be used for migration purposes to modify the data
    /// before and after communication with the underlying storage
    /// </summary>
    internal class TransformerGrainStorage : IGrainStorage, ILifecycleParticipant<ISiloLifecycle>
    {
        private readonly IGrainTransformer _transformer;

        private readonly IGrainStorage _innerGrainStorage;
        private readonly ILifecycleParticipant<ISiloLifecycle> _innerGrainStorageParticipant;

        protected TransformerGrainStorage(IGrainTransformer grainTransformer, IGrainStorage grainStorage)
        {
            _transformer = grainTransformer;
            _innerGrainStorage = grainStorage;

            if (_innerGrainStorage is not ILifecycleParticipant<ISiloLifecycle> innerGrainStorageParticipant)
            {
                throw new ArgumentException("GrainStorage should implement ILifecycleParticipant<ISiloLifecycle>", nameof(grainStorage));
            }
            _innerGrainStorageParticipant = innerGrainStorageParticipant;
        }

        public async Task ClearStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            _transformer.BeforeClearState(ref grainType, grainReference, grainState);
            await this._innerGrainStorage.ClearStateAsync(grainType, grainReference, grainState);
        }

        public async Task ReadStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            _transformer.BeforeReadState(ref grainType, grainReference, grainState);
            await this._innerGrainStorage.ReadStateAsync(grainType, grainReference, grainState);
        }

        public async Task WriteStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            _transformer.BeforeWriteState(ref grainType, grainReference, grainState);
            await this._innerGrainStorage.WriteStateAsync(grainType, grainReference, grainState);
        }
        
        public static IGrainStorage Create(IServiceProvider serviceProvider, string innerGrainStorageName, string grainTransformerName)
        {
            var innerGrainStorage = serviceProvider.GetRequiredServiceByName<IGrainStorage>(innerGrainStorageName);
            var grainTransformer = serviceProvider.GetRequiredServiceByName<IGrainTransformer>(grainTransformerName);

            return new TransformerGrainStorage(grainTransformer, innerGrainStorage);
        }

        public void Participate(ISiloLifecycle lifecycle) => _innerGrainStorageParticipant.Participate(lifecycle);
    }

    /// <summary>
    /// Provides API to control the grain data before it is communicated to the underlying storage.
    /// </summary>
    public interface IGrainTransformer
    {
        void BeforeClearState(ref string grainType, GrainReference grainReference, IGrainState grainState);
        void BeforeReadState(ref string grainType, GrainReference grainReference, IGrainState grainState);
        void BeforeWriteState(ref string grainType, GrainReference grainReference, IGrainState grainState);
    }
}
