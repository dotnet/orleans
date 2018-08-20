using Orleans.CodeGeneration;
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
        public static ISiloHostBuilder AddGrainExtension<TExtensionInterface, TExtension>(this ISiloHostBuilder builder)
            where TExtensionInterface : class, IGrainExtension
            where TExtension : class, TExtensionInterface
        {
            int interfaceId = GrainInterfaceUtils.GetGrainInterfaceId(typeof(TExtensionInterface));
            return builder.ConfigureServices(services => services.AddTransientKeyedService<int, IGrainExtension, TExtension>(interfaceId));
        }
    }
}
