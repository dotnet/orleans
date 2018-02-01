using System;
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
        public static ISiloHostBuilder UseSqlMembership(this ISiloHostBuilder builder,
            Action<SqlMembershipOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.UseSqlMembership(configureOptions));
        }

        /// <summary>
        /// Configure SiloHostBuilder with SqlMembership
        /// </summary>
        public static ISiloHostBuilder UseSqlMembership(this ISiloHostBuilder builder,
            Action<OptionsBuilder<SqlMembershipOptions>> configureOptions)
        {
            return builder.ConfigureServices(services => services.UseSqlMembership(configureOptions));
        }

        /// <summary>
        /// Configure client to use SqlGatewayListProvider
        /// </summary>
        public static IClientBuilder UseSqlGatewayListProvider(this IClientBuilder builder, Action<SqlGatewayListProviderOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.UseSqlGatewayListProvider(configureOptions));
        }

        /// <summary>
        /// Configure client to use SqlGatewayListProvider
        /// </summary>
        public static IClientBuilder UseSqlGatewayListProvider(this IClientBuilder builder, Action<OptionsBuilder<SqlGatewayListProviderOptions>> configureOptions)
        {
            return builder.ConfigureServices(services => services.UseSqlGatewayListProvider(configureOptions));
        }

        /// <summary>
        /// Configure DI container with SqlMembership
        /// </summary>
        public static IServiceCollection UseSqlMembership(this IServiceCollection services,
            Action<SqlMembershipOptions> configureOptions)
        {
            return services.UseSqlMembership(ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure DI container with SqlMembership
        /// </summary>
        public static IServiceCollection UseSqlMembership(this IServiceCollection services,
            Action<OptionsBuilder<SqlMembershipOptions>> configureOptions)
        {
            configureOptions?.Invoke(services.AddOptions<SqlMembershipOptions>());
            return services.AddSingleton<IMembershipTable, SqlMembershipTable>();
        }

        /// <summary>
        /// Configure DI container with SqlGatewayListProvider
        /// </summary>
        public static IServiceCollection UseSqlGatewayListProvider(this IServiceCollection services,
            Action<SqlGatewayListProviderOptions> configureOptions)
        {
            return services.UseSqlGatewayListProvider(ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure DI container with SqlGatewayProvider
        /// </summary>
        public static IServiceCollection UseSqlGatewayListProvider(this IServiceCollection services,
            Action<OptionsBuilder<SqlGatewayListProviderOptions>> configureOptions)
        {
            configureOptions?.Invoke(services.AddOptions<SqlGatewayListProviderOptions>());
            return services.AddSingleton<IGatewayListProvider, SqlGatewayListProvider>();
        }
    }
}
