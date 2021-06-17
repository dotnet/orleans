using System.Threading.Tasks;

using Orleans;

using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class MessageSerializationGrain : Grain, IMessageSerializationGrain
    {
        private IMessageSerializationGrain grainOnOtherSilo;

        public Task SendUnserializable(UnserializableType input) => Task.CompletedTask;
        public Task SendUndeserializable(UndeserializableType input) => Task.CompletedTask;
        public Task<UnserializableType> GetUnserializable() => Task.FromResult(new UnserializableType());
        public Task<UndeserializableType> GetUndeserializable() => Task.FromResult(new UndeserializableType());

        public async Task SendUndeserializableToOtherSilo()
        {
            var otherGrain = await GetGrainOnOtherSilo();

            // Message that grain in a way which should fail.
            await otherGrain.SendUndeserializable(new UndeserializableType(35));
        }

        public async Task GetUnserializableFromOtherSilo()
        {
            var otherGrain = await GetGrainOnOtherSilo();

            // Message that grain in a way which should fail.
            await otherGrain.GetUnserializable();
        }

        public async Task SendUnserializableToOtherSilo()
        {
            var otherGrain = await GetGrainOnOtherSilo();

            // Message that grain in a way which should fail.
            await otherGrain.SendUnserializable(new UnserializableType());
        }

        public async Task GetUndeserializableFromOtherSilo()
        {
            var otherGrain = await GetGrainOnOtherSilo();

            // Message that grain in a way which should fail.
            await otherGrain.GetUndeserializable();
        }

        public Task SendUndeserializableToClient(IMessageSerializationClientObject obj) => obj.SendUndeserializable(new UndeserializableType(35));
        public Task SendUnserializableToClient(IMessageSerializationClientObject obj) => obj.SendUnserializable(new UnserializableType());

        public Task GetUnserializableFromClient(IMessageSerializationClientObject obj) => obj.GetUnserializable();
        public Task GetUndeserializableFromClient(IMessageSerializationClientObject obj) => obj.GetUndeserializable();

        private async Task<IMessageSerializationGrain> GetGrainOnOtherSilo()
        {
            if (this.grainOnOtherSilo != null) return this.grainOnOtherSilo;

            // Find a grain on another silo.
            IMessageSerializationGrain otherGrain;
            var id = this.GetPrimaryKeyLong();
            var currentSiloIdentity = await this.GetSiloIdentity();
            while (true)
            {
                otherGrain = this.GrainFactory.GetGrain<IMessageSerializationGrain>(++id);
                var otherIdentity = await otherGrain.GetSiloIdentity();
                if (!string.Equals(otherIdentity, currentSiloIdentity))
                {
                    break;
                }
            }

            return this.grainOnOtherSilo = otherGrain;
        }

        public Task<string> GetSiloIdentity()
        {
            return Task.FromResult(this.RuntimeIdentity);
        }
    }
}