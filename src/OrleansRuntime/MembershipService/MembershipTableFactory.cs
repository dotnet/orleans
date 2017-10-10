using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime.MembershipService
{
    internal class MembershipTableFactory
    {
        private readonly IServiceProvider serviceProvider;
        private readonly AsyncLock initializationLock = new AsyncLock();
        private readonly ILogger logger;
        private IMembershipTable membershipTable;

        public MembershipTableFactory(IServiceProvider serviceProvider, ILogger<MembershipTableFactory> logger)
        {
            this.serviceProvider = serviceProvider;
            this.logger = logger;
        }

        internal async Task<IMembershipTable> GetMembershipTable()
        {
            if (membershipTable != null) return membershipTable;
            using (await this.initializationLock.LockAsync())
            {
                if (membershipTable != null) return membershipTable;
                
                // get membership through DI
                var result = this.serviceProvider.GetService<IMembershipTable>();
                if (result == null)
                {
                    string errorMessage = "No membership table provider configured with Silo";
                    this.logger?.LogCritical(errorMessage);
                    throw new NotImplementedException(errorMessage);
                }

                await result.InitializeMembershipTable(true);
                membershipTable = result;
            }

            return membershipTable;
        }        
    }
}