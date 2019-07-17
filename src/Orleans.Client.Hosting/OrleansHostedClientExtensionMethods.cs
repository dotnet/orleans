using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System;

namespace Orleans.Client.Hosting
{
    public static class OrleansHostedClientExtensionMethods
    {

        public static IServiceCollection AddOrleansHostedClient(this IServiceCollection services
       , Action<IOrleansHostedClientBuilder> clientBuilder)
        {
            clientBuilder(new OrleansHostedClientBuilder(services));
            services.AddTransient<OrleansHostedConection>();
            services.AddSingleton<OrleansHostedClientStore>();
            services.AddSingleton<IOrleansHostedClientAccessor, OrleansHostedClientAccessor>();
            return services;
        }



        public static IOrleansHostedClientBuilder AddOrleansClient(this IOrleansHostedClientBuilder builder,
        string name,
        Action<IClientBuilder> clientBuilder)
        {

            IClientBuilder clientBuilderObj = new ClientBuilder();
            clientBuilder(clientBuilderObj);

            builder.Services.AddSingleton<IHostedService, OrleansHostedClientService>((sp) =>
            {
                var namedClientBuilder = new NamedOrleansHostedClientBuilder() {
                    Name = name,
                    ClientBuilder = clientBuilderObj };

                return ActivatorUtilities.CreateInstance<OrleansHostedClientService>(sp, namedClientBuilder);
            });
            return builder;
        }
    }
}
