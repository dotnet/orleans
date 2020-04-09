using System;

namespace Orleans.Runtime
{
    /// <summary>
    /// Helper class used to create local instances of grains. In the future this should be opened up for extension similar to ASP.NET's ControllerFactory.
    /// </summary>
    internal class GrainCreator
    {
        private readonly IGrainActivator grainActivator;

        private readonly Lazy<IGrainRuntime> grainRuntime;
 
        /// <summary>
        /// Initializes a new instance of the <see cref="GrainCreator"/> class.
        /// </summary>
        /// <param name="grainActivator">The activator used to used to create new grains</param>
        /// <param name="getGrainRuntime">The delegate used to get the grain runtime.</param>
        public GrainCreator(
            IGrainActivator grainActivator,
            Factory<IGrainRuntime> getGrainRuntime)
        {
            this.grainActivator = grainActivator;
            this.grainRuntime = new Lazy<IGrainRuntime>(() => getGrainRuntime());
        }

        /// <summary>
        /// Create a new instance of a grain
        /// </summary>
        /// <param name="context">The <see cref="IGrainActivationContext"/> for the executing action.</param>
        /// <returns>The newly created grain.</returns>
        public IGrain CreateGrainInstance(IGrainActivationContext context)
        {
            var grain = (IGrain)grainActivator.Create(context);

            if (grain is Grain grainBase)
            {
                // Inject runtime hooks into grain instance
                grainBase.Runtime = this.grainRuntime.Value;
                grainBase.Identity = context.GrainIdentity;
            }
            // TODO: consider making Identity mockable for POCOs. (Runtime will inherently be mockable via Manual DI)

            // wire up to lifecycle
            var participant = grain as ILifecycleParticipant<IGrainLifecycle>;
            participant?.Participate(context.ObservableLifecycle);

            return grain;
        }

        public void Release(IGrainActivationContext context, object grain)
        {
            this.grainActivator.Release(context, grain);
        }
    }
}