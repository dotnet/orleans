using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Core;
using Orleans.LogConsistency;
using Orleans.Storage;
using Orleans.Runtime.LogConsistency;
using Orleans.GrainDirectory;

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
            this.createFactory = type => ActivatorUtilities.CreateFactory(type, Type.EmptyTypes);
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
        /// Install the storage bridge into a stateful grain.
        /// </summary>
        /// <param name="grain">The grain.</param>
        /// <param name="grainType">The grain type.</param>
        /// <param name="stateType">The type of the state it persists.</param>
        /// <param name="storageProvider">The provider used to store the state.</param>
        public void InstallStorageBridge(Grain grain, Type grainType, Type stateType, IStorageProvider storageProvider)
        {
            var statefulgrain = (IStatefulGrain) grain;

            var storage = new GrainStateStorageBridge(grainType.FullName, statefulgrain, storageProvider);

            //Inject state and storage data into the grain
            statefulgrain.GrainState.State = Activator.CreateInstance(stateType);
            statefulgrain.SetStorage(storage);
        }


        /// <summary>
        /// Install the log-view adaptor into a log-consistent grain.
        /// </summary>
        /// <param name="grain">The grain.</param>
        /// <param name="grainType">The grain type.</param>
        /// <param name="stateType">The type of the grain state.</param>
        /// <param name="mcRegistrationStrategy">The multi-cluster registration strategy.</param>
        /// <param name="factory">The consistency adaptor factory</param>
        /// <param name="storageProvider">The storage provider, or null if none needed</param>
        /// <returns>The newly created grain.</returns>
        public void InstallLogViewAdaptor(Grain grain, Type grainType, 
            Type stateType, IMultiClusterRegistrationStrategy mcRegistrationStrategy,
            ILogViewAdaptorFactory factory, IStorageProvider storageProvider)
        {
            // try to find a suitable logger that we can use to trace consistency protocol information
            var logger = (factory as ILogConsistencyProvider)?.Log ?? storageProvider?.Log;
           
            // encapsulate runtime services used by consistency adaptors
            var svc = new ProtocolServices(grain, logger, mcRegistrationStrategy);

            var state = Activator.CreateInstance(stateType);

            ((ILogConsistentGrain)grain).InstallAdaptor(factory, state, grainType.FullName, storageProvider, svc);
        }
    }
}