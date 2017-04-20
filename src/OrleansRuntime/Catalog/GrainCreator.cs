using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Core;
using Orleans.LogConsistency;
using Orleans.Storage;
using Orleans.Runtime.LogConsistency;
using Orleans.GrainDirectory;
using Orleans.Serialization;
using Orleans.Runtime.MultiClusterNetwork;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime
{
    /// <summary>
    /// Helper class used to create local instances of grains.
    /// </summary>
    internal class GrainCreator
    {
        private readonly Lazy<IGrainRuntime> grainRuntime;

        private readonly IServiceProvider services;

        private readonly Func<Type, ObjectFactory> createFactory;

        private readonly ConcurrentDictionary<Type, ObjectFactory> typeActivatorCache = new ConcurrentDictionary<Type, ObjectFactory>();
        
        private readonly Factory<Grain, IMultiClusterRegistrationStrategy, ProtocolServices> protocolServicesFactory;

        /// <summary>
        /// Initializes a new instance of the <see cref="GrainCreator"/> class.
        /// </summary>
        /// <param name="services">Service provider used to create new grains</param>
        /// <param name="getGrainRuntime">The delegate used to get the grain runtime.</param>
        /// <param name="protocolServicesFactory"></param>
        public GrainCreator(
            IServiceProvider services,
            Func<IGrainRuntime> getGrainRuntime,
            Factory<Grain, IMultiClusterRegistrationStrategy, ProtocolServices> protocolServicesFactory)
        {
            this.services = services;
            this.protocolServicesFactory = protocolServicesFactory;
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
        /// Create a new instance of a grain
        /// </summary>
        /// <param name="grainType"></param>
        /// <param name="identity">Identity for the new grain</param>
        /// <param name="stateType">If the grain is a stateful grain, the type of the state it persists.</param>
        /// <param name="storage">If the grain is a stateful grain, the storage used to persist the state.</param>
        /// <returns></returns>
        public Grain CreateGrainInstance(Type grainType, IGrainIdentity identity, Type stateType, IStorage storage)
		{
            //Create a new instance of the grain
            var grain = CreateGrainInstance(grainType, identity);

            var statefulGrain = grain as IStatefulGrain;

            if (statefulGrain == null)
                return grain;

            //Inject state and storage data into the grain
            statefulGrain.GrainState.State = Activator.CreateInstance(stateType);
            statefulGrain.SetStorage(storage);

            return grain;
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
            // encapsulate runtime services used by consistency adaptors
            var svc = this.protocolServicesFactory(grain, mcRegistrationStrategy);

            var state = Activator.CreateInstance(stateType);

            ((ILogConsistentGrain)grain).InstallAdaptor(factory, state, grainType.FullName, storageProvider, svc);
        }
    }
}