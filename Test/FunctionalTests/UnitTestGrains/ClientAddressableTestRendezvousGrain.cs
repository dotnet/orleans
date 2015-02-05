using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnitTestGrainInterfaces;
using Orleans;

namespace UnitTestGrains
{

    public class ClientAddressableTestRendezvousGrain : Grain, IClientAddressableTestRendezvousGrain
    {
        private IClientAddressableTestProducer producer = null;

        public Task<IClientAddressableTestProducer> GetProducer()
        {
            return Task.FromResult(this.producer);
        }

        public Task SetProducer(IClientAddressableTestProducer producer)
        {
            this.producer = producer;
            return TaskDone.Done;
        }
    }
}
