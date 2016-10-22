using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Core;
using Orleans.Storage;

namespace Orleans.Runtime
{
    /// <summary>
    /// Helper classe used to create local instances of grains.
    /// </summary>
    public class GrainCreator
    {
        private readonly Lazy<IGrainRuntime> grainRuntime;

        private readonly IServiceProvider services;

        private readonly Func<Type, ObjectFactory> createFactory;

        private readonly ConcurrentDictionary<Type, ObjectFactory> typeActivatorCache = new ConcurrentDictionary<Type, ObjectFactory>();

        /// <summary>
        /// Initializes a new instance of the <see cref="GrainCreator"/> class.
        /// </summary>
        /// <param name="services">Service provider used to create new grains</param>
        /// <param name="getGrainRuntime">
        /// The delegate used to get the grain runtime.
        /// </param>
        public GrainCreator(IServiceProvider services, Func<IGrainRuntime> getGrainRuntime)
        {
            this.services = services;
            this.grainRuntime = new Lazy<IGrainRuntime>(getGrainRuntime);
            if (services != null)
            {
                this.createFactory = type => ActivatorUtilities.CreateFactory(type, Type.EmptyTypes);
            }
            else
            {
                this.createFactory = type => (sp, args) => Activator.CreateInstance(type);
            }
        }

        /// <summary>
        /// Create a new instance of a grain
        /// </summary>
        /// <param name="grainType">The grain type.</param>
        /// <param name="identity">Identity for the new grain</param>
        /// <returns>The newly created grain.</returns>
        public Grain CreateGrainInstance(Type grainType, IGrainIdentity identity)
        {
            var activator = this.typeActivatorCache.GetOrAdd(grainType, this.createFactory);
            var grain = (Grain)activator(this.services, arguments: null);

            // Inject runtime hooks into grain instance
            grain.Runtime = this.grainRuntime.Value;
            grain.Identity = identity;

            return grain;
        }

        /// <summary>
        /// Create a new instance of a grain
        /// </summary>
        /// <param name="grainType">The grain type.</param>
        /// <param name="identity">Identity for the new grain</param>
        /// <param name="stateType">If the grain is a stateful grain, the type of the state it persists.</param>
        /// <param name="storageProvider">If the grain is a stateful grain, the storage provider used to persist the state.</param>
        /// <returns>The newly created grain.</returns>
        public Grain CreateGrainInstance(Type grainType, IGrainIdentity identity, Type stateType, IStorageProvider storageProvider)
        {
            // Create a new instance of the grain
            var grain = this.CreateGrainInstance(grainType, identity);

            var statefulGrain = grain as IStatefulGrain;

            if (statefulGrain == null)
            {
                return grain;
            }

            var storage = new GrainStateStorageBridge(grainType.FullName, statefulGrain, storageProvider);

            //Inject state and storage data into the grain
            statefulGrain.GrainState.State = Activator.CreateInstance(stateType);
            statefulGrain.SetStorage(storage);

            return grain;
        }
    }
}