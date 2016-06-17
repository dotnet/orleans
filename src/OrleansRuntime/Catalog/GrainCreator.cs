﻿using System;
using Orleans.Core;
using Orleans.Storage;

namespace Orleans.Runtime
{
    /// <summary>
    /// Helper classe used to create local instances of grains.
    /// </summary>
    public class GrainCreator
    {
        private readonly IGrainRuntime _grainRuntime;
        private readonly IServiceProvider _services;

        /// <summary>
        /// Instantiate a new instance of a <see cref="GrainCreator"/>
        /// </summary>
        /// <param name="grainRuntime">Runtime to use for all new grains</param>
        /// <param name="services">(Optional) Service provider used to create new grains</param>
        public GrainCreator(IGrainRuntime grainRuntime, IServiceProvider services = null)
        {
            _grainRuntime = grainRuntime;
            _services = services;
        }

        /// <summary>
        /// Create a new instance of a grain
        /// </summary>
        /// <param name="grainType"></param>
        /// <param name="identity">Identity for the new grain</param>
        /// <returns></returns>
        public Grain CreateGrainInstance(Type grainType, IGrainIdentity identity)
        {
            var grain = _services != null
                ? (Grain) _services.GetService(grainType)
                : (Grain) Activator.CreateInstance(grainType);

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
