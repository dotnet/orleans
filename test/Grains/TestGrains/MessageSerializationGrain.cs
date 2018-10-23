using Orleans.CodeGeneration;

namespace UnitTests.Grains
{
    using System;
    using System.Threading.Tasks;

    using Orleans;
    using Orleans.Serialization;

    using UnitTests.GrainInterfaces;
    
    public class MessageSerializationGrain : Grain, IMessageSerializationGrain
    {
        public Task Send(UndeserializableType input) => Task.FromResult(input);
        public Task<UnserializableType> Get() => Task.FromResult(new UnserializableType());

        public async Task SendToOtherSilo()
        {
            var otherGrain = await GetGrainOnOtherSilo();

            // Message that grain in a way which should fail.
            await otherGrain.Send(new UndeserializableType(35));
        }

        public async Task GetFromOtheSilo()
        {
            var otherGrain = await GetGrainOnOtherSilo();

            // Message that grain in a way which should fail.
            await otherGrain.Get();
        }

        public Task SendToClient(IMessageSerializationClientObject obj) => obj.Send(new UndeserializableType(35));

        public Task GetFromClient(IMessageSerializationClientObject obj) => obj.Get();

        private async Task<IMessageSerializationGrain> GetGrainOnOtherSilo()
        {
            // Find a grain on another silo.
            IMessageSerializationGrain otherGrain;
            var id = this.GetPrimaryKeyLong();
            var currentSiloIdentity = await this.GetSiloIdentity();
            while (true)
            {
                otherGrain = this.GrainFactory.GetGrain<IMessageSerializationGrain>(++id);
                var otherIdentity = await otherGrain.GetSiloIdentity();
                if (!String.Equals(otherIdentity, currentSiloIdentity))
                {
                    break;
                }
            }

            return otherGrain;
        }

        public Task<string> GetSiloIdentity()
        {
            return Task.FromResult(this.RuntimeIdentity);
        }
    }
}