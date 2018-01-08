using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Messaging;
using Orleans.Runtime.Configuration;
using LivenessProviderType = Orleans.Runtime.Configuration.GlobalConfiguration.LivenessProviderType;

namespace Orleans.Runtime.MembershipService
{
    /// <summary>
    /// LegacyMembershipConfigurator configure membership table in the legacy way, which is from global configuration
    /// </summary>
    public interface ILegacyMembershipConfigurator
    {
        /// <summary>
        /// Configure the membership table in the legacy way 
        /// </summary>
        void ConfigureServices(GlobalConfiguration configuration, IServiceCollection services);
    }
}
