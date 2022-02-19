using System;
using Orleans.Runtime;

namespace Orleans.Hosting
{
    /// <summary>
    /// Methods for configuring <see cref="IGrainExtension"/>s on a silo.
    /// </summary>
    public static class HostingGrainExtensions
    {
        /// <summary>
        /// Registers a grain extension implementation for the specified interface.
        /// </summary>
        /// <typeparam name="TExtensionInterface">The <see cref="IGrainExtension"/> interface being registered.</typeparam>
        /// <typeparam name="TExtension">The implementation of <typeparamref name="TExtensionInterface"/>.</typeparam>
        public static ISiloBuilder AddGrainExtension<TExtensionInterface, TExtension>(this ISiloBuilder builder)
            where TExtensionInterface : class, IGrainExtension
            where TExtension : class, TExtensionInterface
        {
            return builder.ConfigureServices(services => services.AddTransientKeyedService<Type, IGrainExtension, TExtension>(typeof(TExtensionInterface)));
        }
    }
}
