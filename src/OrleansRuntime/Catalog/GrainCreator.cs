using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Core;
using Orleans.Storage;
using System.Collections.Concurrent;

namespace Orleans.Runtime
{
    /// <summary>
    /// Helper classe used to create local instances of grains.
    /// </summary>
    public class GrainCreator
    {
        private readonly IGrainRuntime _grainRuntime;
        private readonly IServiceProvider _services;
        private readonly Func<Type, ObjectFactory> _createFactory;
        private ConcurrentDictionary<Type, ObjectFactory> _typeActivatorCache = new ConcurrentDictionary<Type, ObjectFactory>();

        /// <summary>
        /// Instantiate a new instance of a <see cref="GrainCreator"/>
        /// </summary>
        /// <param name="grainRuntime">Runtime to use for all new grains</param>
        /// <param name="services">(Optional) Service provider used to create new grains</param>
        public GrainCreator(IGrainRuntime grainRuntime, IServiceProvider services)
        {
            _grainRuntime = grainRuntime;
            _services = services;
            if (_services != null)
            {
                _createFactory = (type) => ActivatorUtilities.CreateFactory(type, Type.EmptyTypes);
            }
            else
            {
                // TODO: we could optimize instance creation for the non-DI path also
                _createFactory = (type) => ((sp, args) => Activator.CreateInstance(type));
            }
    }

        /// <summary>
        /// Create a new instance of a grain
        /// </summary>
        /// <param name="grainType"></param>
        /// <param name="identity">Identity for the new grain</param>
        /// <returns></returns>
        public Grain CreateGrainInstance(Type grainType, IGrainIdentity identity)
        {
            var activator = _typeActivatorCache.GetOrAdd(grainType, _createFactory);
            var grain = (Grain)activator(_services, arguments: null);

            // Inject runtime hooks into grain instance
            grain.Runtime = _grainRuntime;
            grain.Identity = identity;

            return grain;
        }

        /// <summary>
        /// Create a new instance of a grain
        /// </summary>
        /// <param name="grainType"></param>
        /// <param name="identity">Identity for the new grain</param>
        /// <param name="stateType">If the grain is a stateful grain, the type of the state it persists.</param>
        /// <param name="storageProvider">If the grain is a stateful grain, the storage provider used to persist the state.</param>
        /// <returns></returns>
        public Grain CreateGrainInstance(Type grainType, IGrainIdentity identity, Type stateType,
            IStorageProvider storageProvider)
        {
            //Create a new instance of the grain
            var grain = CreateGrainInstance(grainType, identity);

            var statefulGrain = grain as IStatefulGrain;

            if (statefulGrain == null)
                return grain;

            var storage = new GrainStateStorageBridge(grainType.FullName, statefulGrain, storageProvider);

            //Inject state and storage data into the grain
            statefulGrain.GrainState.State = Activator.CreateInstance(stateType);
            statefulGrain.SetStorage(storage);

            return grain;
        }
    }
}
