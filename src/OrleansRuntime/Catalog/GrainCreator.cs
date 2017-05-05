using System;
using Orleans.Core;
using Orleans.LogConsistency;
using Orleans.Storage;
using Orleans.Runtime.LogConsistency;
using Orleans.GrainDirectory;

namespace Orleans.Runtime
{
    /// <summary>
    /// Helper class used to create local instances of grains. In the future this should be opened up for extension similar to ASP.NET's ControllerFactory.
    /// </summary>
    public class GrainCreator
    {
        private readonly IGrainActivator grainActivator;

        private readonly Lazy<IGrainRuntime> grainRuntime;

        /// <summary>
        /// Initializes a new instance of the <see cref="GrainCreator"/> class.
        /// </summary>
        /// <param name="grainActivator">The activator used to used to create new grains</param>
        /// <param name="getGrainRuntime">
        /// The delegate used to get the grain runtime.
        /// </param>
        public GrainCreator(IGrainActivator grainActivator, Func<IGrainRuntime> getGrainRuntime)
        {
            this.grainActivator = grainActivator;
            this.grainRuntime = new Lazy<IGrainRuntime>(getGrainRuntime);
        }

        /// <summary>
        /// Create a new instance of a grain
        /// </summary>
        /// <param name="context">The <see cref="IGrainActivationContext"/> for the executing action.</param>
        /// <returns>The newly created grain.</returns>
        public Grain CreateGrainInstance(IGrainActivationContext context)
        {
            var grain = (Grain)grainActivator.Create(context);

            // Inject runtime hooks into grain instance
            grain.Runtime = this.grainRuntime.Value;
            grain.Identity = context.GrainIdentity;

            return grain;
        }

        /// <summary>
        /// Create a new instance of a grain
        /// </summary>
        /// <param name="context">The <see cref="IGrainActivationContext"/> for the executing action.</param>
        /// <param name="stateType">If the grain is a stateful grain, the type of the state it persists.</param>
        /// <param name="storage">If the grain is a stateful grain, the storage used to persist the state.</param>
        /// <returns></returns>
        public Grain CreateGrainInstance(IGrainActivationContext context, Type stateType, IStorage storage)
		{
            //Create a new instance of the grain
            var grain = CreateGrainInstance(context);

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
            // try to find a suitable logger that we can use to trace consistency protocol information
            var logger = (factory as ILogConsistencyProvider)?.Log ?? storageProvider?.Log;
           
            // encapsulate runtime services used by consistency adaptors
            var svc = new ProtocolServices(grain, logger, mcRegistrationStrategy);

            var state = Activator.CreateInstance(stateType);

            ((ILogConsistentGrain)grain).InstallAdaptor(factory, state, grainType.FullName, storageProvider, svc);
        }
    }
}