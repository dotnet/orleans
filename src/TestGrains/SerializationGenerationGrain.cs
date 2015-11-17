/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System.Threading.Tasks;

namespace TestGrains
{
    using System.Collections.Generic;
    using System.Linq;

    using Orleans;

    using UnitTests.GrainInterfaces;
    public class SerializationGenerationGrain : Grain<SerializationGenerationGrain.MyState>, ISerializationGenerationGrain
    {
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

        public class MyState : GrainState
        {
            public IList<SomeAbstractClass> Classes { get; set; }
        }
    }
}
