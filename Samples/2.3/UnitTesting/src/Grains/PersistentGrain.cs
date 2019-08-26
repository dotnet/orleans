using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using ProtoBuf;

namespace Grains
{
    public class PersistentGrain : Grain, IPersistentGrain
    {
        private readonly IPersistentState<MyState> value;

        public PersistentGrain([PersistentState("State")] IPersistentState<MyState> value)
        {
            this.value = value;
        }

        public Task SaveAsync() => value.WriteStateAsync();

        public Task SetValueAsync(int value)
        {
            this.value.State.Value = value;
            return Task.CompletedTask;
        }

        [ProtoContract]
        public class MyState
        {
            [ProtoMember(1)]
            public int Value { get; set; }
        }
    }
}