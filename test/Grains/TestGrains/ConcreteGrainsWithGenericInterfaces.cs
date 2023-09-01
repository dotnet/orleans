using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    internal class ConcreteGrainWithGenericInterfaceOfIntFloat : Grain, IGenericGrain<int, float>
    {
        protected int T { get; set; }

        public Task SetT(int t)
        {
            T = t;
            return Task.CompletedTask;
        }

        public Task<float> MapT2U()
        {
            return Task.FromResult((float)T);
        }
    }

    internal class ConcreteGrainWithGenericInterfaceOfFloatString : Grain, IGenericGrain<float, string>
    {
        protected float T { get; set; }

        public Task SetT(float t)
        {
            T = t;
            return Task.CompletedTask;
        }

        public Task<string> MapT2U()
        {
            return Task.FromResult(Convert.ToString(T));
        }
    }

    internal class ConcreteGrainWith2GenericInterfaces : Grain, IGenericGrain<int, string>, ISimpleGenericGrain<int>
    {
        // IGenericGrain<int, string> methods:

        protected int T { get; set; }

        public Task SetT(int t)
        {
            T = t;
            return Task.CompletedTask;
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
            return Task.CompletedTask;
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
