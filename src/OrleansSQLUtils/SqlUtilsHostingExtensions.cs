using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Messaging;
using Orleans.Runtime.Membership;
using Orleans.Runtime.MembershipService;
using OrleansSQLUtils.Configuration;
using OrleansSQLUtils.Options;

namespace Orleans.Hosting
{
    public static class SqlUtilsHostingExtensions
    {
        /// <summary>
        /// Configure SiloHostBuilder with SqlMembership
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="configureOptions"></param>
        /// <returns></returns>
        public static ISiloHostBuilder UseSqlMembership(this ISiloHostBuilder builder,
            Action<SqlMembershipOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.UseSqlMembership(configureOptions));
        }

        /// <summary>
        /// Configure SiloHostBuilder with SqlMembership
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="configuration"></param>
        /// <returns></returns>
        public static ISiloHostBuilder UseSqlMembership(this ISiloHostBuilder builder,
            IConfiguration configuration)
        {
            return builder.ConfigureServices(services => services.UseSqlMembership(configuration));
        }

        /// <summary>
        /// Configure client to use SqlGatewayListProvider
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="configureOptions"></param>
        /// <returns></returns>
        public static IClientBuilder UseSqlGatewayListProvider(this IClientBuilder builder, Action<SqlGatewayListProviderOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.UseSqlGatewayListProvider(configureOptions));
        }

        /// <summary>
        /// Configure client to use SqlGatewayListProvider
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="configuration"></param>
        /// <returns></returns>
        public static IClientBuilder UseSqlGatewayListProvider(this IClientBuilder builder, IConfiguration configuration)
        {
            return builder.ConfigureServices(services => services.UseSqlGatewayListProvider(configuration));
        }

        /// <summary>
        /// Configure DI container with SqlMemebership
        /// </summary>
        /// <param name="services"></param>
        /// <param name="configureOptions"></param>
        /// <returns></returns>
        public static IServiceCollection UseSqlMembership(this IServiceCollection services,
            Action<SqlMembershipOptions> configureOptions)
        {
            return services.Configure<SqlMembershipOptions>(configureOptions)
                .AddSingleton<IMembershipTable, SqlMembershipTable>();
        }

        /// <summary>
        /// Configure DI container with SqlMemebership
        /// </summary>
        /// <param name="services"></param>
        /// <param name="configuration"></param>
        /// <returns></returns>
        public static IServiceCollection UseSqlMembership(this IServiceCollection services,
            IConfiguration configuration)
        {
            return services.Configure<SqlMembershipOptions>(configuration)
           .AddSingleton<IMembershipTable, SqlMembershipTable>();
        }

        /// <summary>
        /// Configure DI container with SqlGatewayListProvider
        /// </summary>
        /// <param name="services"></param>
        /// <param name="configureOptions"></param>
        /// <returns></returns>
        public static IServiceCollection UseSqlGatewayListProvider(this IServiceCollection services,
            Action<SqlGatewayListProviderOptions> configureOptions)
        {
            return services.Configure(configureOptions)
                .AddSingleton<IGatewayListProvider, SqlGatewayListProvider>();
        }

        /// <summary>
        /// Configure DI container with SqlGatewayProvider
        /// </summary>
        /// <param name="services"></param>
        /// <param name="configuration"></param>
        /// <returns></returns>
        public static IServiceCollection UseSqlGatewayListProvider(this IServiceCollection services,
            IConfiguration configuration)
        {
            return services.Configure<SqlGatewayListProviderOptions>(configuration)
                .AddSingleton<IGatewayListProvider, SqlGatewayListProvider>();
        }
    }
}
