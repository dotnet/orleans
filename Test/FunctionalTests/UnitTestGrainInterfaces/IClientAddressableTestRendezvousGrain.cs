using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;

namespace UnitTestGrainInterfaces
{
    public interface IClientAddressableTestRendezvousGrain : IGrain
    {
        Task<IClientAddressableTestProducer> GetProducer();
        Task SetProducer(IClientAddressableTestProducer producer);
    }
}
