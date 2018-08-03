using Orleans.CodeGeneration;
using Orleans.Runtime;

namespace Orleans.Hosting
{
    public static class HostingGrainExtensions
    {
        public static ISiloHostBuilder AddGrainExtension<TExtensionInterface,TExtension>(this ISiloHostBuilder builder)
            where TExtensionInterface : class, IGrainExtension
            where TExtension : class, TExtensionInterface
        {
            int interfaceId = GrainInterfaceUtils.GetGrainInterfaceId(typeof(TExtensionInterface));
            return builder.ConfigureServices(services => services.AddTransientKeyedService<int, IGrainExtension, TExtension>(interfaceId));
        }
    }
}
