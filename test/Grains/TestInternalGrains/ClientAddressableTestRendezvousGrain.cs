using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{

    public class ClientAddressableTestRendezvousGrain : Grain, IClientAddressableTestRendezvousGrain
    {
        private IClientAddressableTestProducer producer;

        public Task<IClientAddressableTestProducer> GetProducer()
        {
            return Task.FromResult(producer);
        }

        public Task SetProducer(IClientAddressableTestProducer producer)
        {
            this.producer = producer;
            return Task.CompletedTask;
        }
    }
}
