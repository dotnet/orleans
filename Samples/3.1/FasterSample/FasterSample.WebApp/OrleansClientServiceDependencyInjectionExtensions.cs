using FasterSample.WebApp;
using Microsoft.Extensions.Hosting;

namespace Microsoft.Extensions.DependencyInjection
{
    public static class OrleansClientServiceDependencyInjectionExtensions
    {
        public static IServiceCollection AddOrleansClientService(this IServiceCollection services)
        {
            return services
                .AddSingleton<OrleansClientService>()
                .AddSingleton<IHostedService, OrleansClientService>(sp => sp.GetRequiredService<OrleansClientService>())
                .AddSingleton(sp => sp.GetRequiredService<OrleansClientService>().GrainFactory);
        }
    }
}