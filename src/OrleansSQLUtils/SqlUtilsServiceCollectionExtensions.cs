using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.MembershipService;
using OrleansSQLUtils.Configuration;

namespace Microsoft.Orleans.Hosting
{
    public static class SqlUtilsServiceCollectionExtensions
    {
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
