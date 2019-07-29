using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime.MembershipService
{
    internal class MembershipGossiper : IMembershipGossiper
    {
        private readonly IServiceProvider serviceProvider;
        private readonly ILogger<MembershipGossiper> log;

        public MembershipGossiper(IServiceProvider serviceProvider, ILogger<MembershipGossiper> log)
        {
            this.serviceProvider = serviceProvider;
            this.log = log;
        }

        public Task GossipToRemoteSilos(List<SiloAddress> gossipPartners, MembershipTableSnapshot snapshot)
        {
            if (gossipPartners.Count == 0) return Task.CompletedTask;

            this.log.LogInformation(
                "Gossiping status change to {NumPartners} partners",
                gossipPartners.Count);
            var systemTarget = this.serviceProvider.GetRequiredService<MembershipSystemTarget>();
            return systemTarget.GossipToRemoteSilos(gossipPartners, snapshot);
        }
    }
}
