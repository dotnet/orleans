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
