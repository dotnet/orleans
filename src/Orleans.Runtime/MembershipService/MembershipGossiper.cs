using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Runtime.MembershipService
{
    internal class MembershipGossiper : IMembershipGossiper
    {
        private readonly IServiceProvider serviceProvider;

        public MembershipGossiper(IServiceProvider serviceProvider)
        {
            this.serviceProvider = serviceProvider;
        }

        public Task GossipToRemoteSilos(
            SiloAddress updatedSilo,
            SiloStatus updatedStatus,
            List<SiloAddress> gossipPartners)
        {
            var systemTarget = this.serviceProvider.GetRequiredService<MembershipSystemTarget>();
            return systemTarget.GossipToRemoteSilos(updatedSilo, updatedStatus, gossipPartners);
        }
    }
}
