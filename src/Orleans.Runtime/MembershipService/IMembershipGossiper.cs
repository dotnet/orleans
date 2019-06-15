using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Runtime.MembershipService
{
    internal interface IMembershipGossiper
    {
        Task GossipToRemoteSilos(SiloAddress updatedSilo, SiloStatus updatedStatus, List<SiloAddress> gossipPartners);
    }
}
