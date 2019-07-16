using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System;

namespace Orleans.Client.Hosting
{

    public static class OrleansClientExtensionMethods
    {
        public static IServiceCollection AddOrleansClient(this IServiceCollection services)
        {
            return AddOrleansClient(services, null);
        }
            public static IServiceCollection AddOrleansClient(this IServiceCollection services
            ,Action<OrleansClientHostedOptions> clientOptions)
        {
            var builder = services.AddOptions<OrleansClientHostedOptions>();
            builder.Configure(clientOptions);

            services.AddHostedService<OrleansClientHostedService>();
            services.AddTransient<IOrleansClientAccessor, OrleansClientAccessor>();
            return services;
        }
    }
}
