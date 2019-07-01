using Microsoft.Extensions.Options;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService;

namespace Orleans.Configuration
{
    internal class DevelopmentClusterMembershipOptionsValidator : IConfigurationValidator
    {
        private readonly DevelopmentClusterMembershipOptions options;
        private readonly IMembershipTable membershipTable;

        public DevelopmentClusterMembershipOptionsValidator(IOptions<DevelopmentClusterMembershipOptions> options, IMembershipTable membershipTable)
        {
            this.options = options.Value;
            this.membershipTable = membershipTable;
        }

        public void ValidateConfiguration()
        {
            if (this.membershipTable is SystemTargetBasedMembershipTable && this.options.PrimarySiloEndpoint is null)
            {
                throw new OrleansConfigurationException("Development clustering is enabled but no value is specified ");
            }
        }
    }
}