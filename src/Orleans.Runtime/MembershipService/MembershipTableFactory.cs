using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime.MembershipService
{
    internal class MembershipTableFactory
    {
        private readonly ILogger logger;
        private IMembershipTable membershipTable;

        public MembershipTableFactory(IMembershipTable membershipTable, ILogger<MembershipTableFactory> logger)
        {
            this.membershipTable = membershipTable;
            this.logger = logger;
        }

        internal IMembershipTable GetMembershipTable()
        {
            return this.membershipTable;
        }        
    }
}