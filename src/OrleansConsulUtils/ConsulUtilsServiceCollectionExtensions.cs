using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.ConsulUtils.Configuration;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Host;

namespace Microsoft.Orleans.Hosting
{
    public static class ConsulUtilsServiceCollectionExtensions
    {
        /// <summary>
        /// Configure DI container with ConsuleBasedMemebership
        /// </summary>
        /// <param name="services"></param>
        /// <param name="configureOptions"></param>
        /// <returns></returns>
        public static IServiceCollection UseConsulMembership(this IServiceCollection services,
            Action<ConsulMembershipOptions> configureOptions)
        {
            services.Configure<ConsulMembershipOptions>(configureOptions);
            services.AddSingleton<IMembershipTable, ConsulBasedMembershipTable>();
            return services;
        }

        /// <summary>
        /// Configure DI container with ConsuleBasedMemebership
        /// </summary>
        /// <param name="services"></param>
        /// <param name="configuration"></param>
        /// <returns></returns>
        public static IServiceCollection UseConsulMembership(this IServiceCollection services,
            IConfiguration configuration)
        {
            services.Configure<ConsulMembershipOptions>(configuration);
            services.AddSingleton<IMembershipTable, ConsulBasedMembershipTable>();
            return services;
        }
    }
}
