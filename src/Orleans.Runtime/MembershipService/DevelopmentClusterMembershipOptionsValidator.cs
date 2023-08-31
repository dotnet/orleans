using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService;

namespace Orleans.Configuration
{
    internal class DevelopmentClusterMembershipOptionsValidator : IConfigurationValidator
    {
        private readonly DevelopmentClusterMembershipOptions options;
        private readonly IMembershipTable membershipTable;

        public DevelopmentClusterMembershipOptionsValidator(IOptions<DevelopmentClusterMembershipOptions> options, IServiceProvider serviceProvider)
        {
            this.options = options.Value;
            membershipTable = serviceProvider.GetService<IMembershipTable>();
        }

        public void ValidateConfiguration()
        {
            if (membershipTable is SystemTargetBasedMembershipTable && options.PrimarySiloEndpoint is null)
            {
                throw new OrleansConfigurationException("Development clustering is enabled but no value is specified ");
            }
        }
    }
}