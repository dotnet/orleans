using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;
using UnitTestGrainInterfaces;

namespace UnitTestGrains
{
    public class ClientAddressableTestConsumerGrain : Grain, IClientAddressableTestConsumer
    {
        private IClientAddressableTestProducer producer;
        
        public async Task<int> PollProducer()
        {
            return await this.producer.Poll();
        }

        public async Task Setup()
        {
            var rendezvous = ClientAddressableTestRendezvousGrainFactory.GetGrain(0);
            this.producer = await rendezvous.GetProducer();
        }
    }
}
