using System;
using System.Threading.Tasks;

namespace TestGrains
{
    using System.Collections.Generic;
    using System.Linq;

    using Orleans;

    using UnitTests.GrainInterfaces;
    public class SerializationGenerationGrain : Grain<SerializationGenerationGrain.MyState>, ISerializationGenerationGrain
    {
        public Task<object> RoundTripObject(object input)
        {
            return Task.FromResult(input);
        }

        public Task<SomeStruct> RoundTripStruct(SomeStruct input)
        {
            return Task.FromResult(input);
        }

        public Task<SomeAbstractClass> RoundTripClass(SomeAbstractClass input)
        {
            return Task.FromResult(input);
        }

        public Task<ISomeInterface> RoundTripInterface(ISomeInterface input)
        {
            return Task.FromResult(input);
        }

        public Task<SomeAbstractClass.SomeEnum> RoundTripEnum(SomeAbstractClass.SomeEnum input)
        {
            return Task.FromResult(input);
        }

        public async Task SetState(SomeAbstractClass input)
        {
            this.State.Classes = new List<SomeAbstractClass> { input };
            this.DeactivateOnIdle();
            await this.WriteStateAsync();
        }

        public Task<SomeAbstractClass> GetState()
        {
            return Task.FromResult(this.State.Classes.FirstOrDefault());
        }

        [Serializable]
        public class MyState
        {
            public IList<SomeAbstractClass> Classes { get; set; }
        }
    }
}
