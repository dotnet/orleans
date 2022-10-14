using System;
using Orleans.Providers;

namespace Orleans.Runtime.Providers
{
    internal class SiloProviderRuntime : IProviderRuntime
    {
        private readonly IGrainContextAccessor _grainContextAccessor;

        public SiloProviderRuntime(
            IGrainContextAccessor grainContextAccessor,
            IGrainFactory grainFactory,
            IServiceProvider serviceProvider)
        {
            _grainContextAccessor = grainContextAccessor;
            GrainFactory = grainFactory;
            ServiceProvider = serviceProvider;
        }

        public IGrainFactory GrainFactory { get; }

        public IServiceProvider ServiceProvider { get; }

        public (TExtension, TExtensionInterface) BindExtension<TExtension, TExtensionInterface>(Func<TExtension> newExtensionFunc)
            where TExtension : class, TExtensionInterface
            where TExtensionInterface : class, IGrainExtension
        {
            return _grainContextAccessor.GrainContext.GetComponent<IGrainExtensionBinder>().GetOrSetExtension<TExtension, TExtensionInterface>(newExtensionFunc);
        }
    }
}
