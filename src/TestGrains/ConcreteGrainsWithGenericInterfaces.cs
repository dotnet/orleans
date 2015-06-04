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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    class ConcreteGrainWithGenericInterfaceOfIntFloat : Grain, IGenericGrain<int, float>
    {
        protected int T { get; set; }

        public Task SetT(int t)
        {
            T = t;
            return TaskDone.Done;
        }

        public Task<float> MapT2U()
        {
            return Task.FromResult((float)T);
        }
    }

    class ConcreteGrainWithGenericInterfaceOfFloatString : Grain, IGenericGrain<float, string>
    {
        protected float T { get; set; }

        public Task SetT(float t)
        {
            T = t;
            return TaskDone.Done;
        }

        public Task<string> MapT2U()
        {
            return Task.FromResult(Convert.ToString(T));
        }
    }

    class ConcreteGrainWith2GenericInterfaces: Grain, IGenericGrain<int, string>, ISimpleGenericGrain<int>
    {
        // IGenericGrain<int, string> methods:

        protected int T { get; set; }

        public Task SetT(int t)
        {
            T = t;
            return TaskDone.Done;
        }

        public Task<string> MapT2U()
        {
            return Task.FromResult(Convert.ToString(T * 10, 10));
        }

        //ISimpleGenericGrain<int> methods:

        public Task Set(int t)
        {
            return SetT(t);
        }

        public Task Transform()
        {
            T = T * 10;
            return TaskDone.Done;
        }

        public Task<int> Get()
        {
            return Task.FromResult(T);
        }

        public Task CompareGrainReferences(ISimpleGenericGrain<int> clientReference) 
        {
            throw new NotImplementedException();
        }
    }
}
