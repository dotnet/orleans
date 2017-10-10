using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime.MembershipService;
using OrleansSQLUtils.Configuration;

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
    }
}
