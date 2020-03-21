using System;
using System.Threading.Tasks;
using Orleans;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    class SimpleGenericGrain<T> :Grain, ISimpleGenericGrain<T>
    {
        protected T Value { get; set; }

        public virtual Task Set(T t)
        {
            Value = t;
            return Task.CompletedTask;
        }

        public virtual Task Transform()
        {
            return Task.CompletedTask;
        }

        public Task<T> Get()
        {
            return Task.FromResult(Value);
        }

        public Task CompareGrainReferences(ISimpleGenericGrain<T> clientReference)
        {
            // Compare reference to this grain created by the client 
            var thisReference = GrainFactory.GetGrain <ISimpleGenericGrain<T>>(this.GetPrimaryKeyLong());
            if(!thisReference.Equals(clientReference))
                throw new Exception(String.Format("Case_3: 2 grain references are different, while should have been the same: gr1={0}, gr2={1}", thisReference, clientReference));

            return Task.CompletedTask;
        }
    }
}
