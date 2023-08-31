using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class ClientAddressableTestConsumerGrain : Grain, IClientAddressableTestConsumer
    {
        private IClientAddressableTestProducer producer;
        
        public async Task<int> PollProducer()
        {
            return await producer.Poll();
        }

        public async Task Setup()
        {
            var rendezvous = GrainFactory.GetGrain<IClientAddressableTestRendezvousGrain>(0);
            producer = await rendezvous.GetProducer();
        }
    }
}
