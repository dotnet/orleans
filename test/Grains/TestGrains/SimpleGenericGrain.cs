using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class SimpleGenericGrain<TType> : Grain, ISimpleGenericGrain<TType>
    {
        protected TType Value { get; set; }

        public virtual Task Set(TType t)
        {
            Value = t;
            return Task.CompletedTask;
        }

        public virtual Task Transform()
        {
            return Task.CompletedTask;
        }

        public Task<TType> Get()
        {
            return Task.FromResult(Value);
        }

        public Task CompareGrainReferences(ISimpleGenericGrain<TType> clientReference)
        {
            // Compare reference to this grain created by the client 
            var thisReference = GrainFactory.GetGrain<ISimpleGenericGrain<TType>>(this.GetPrimaryKeyLong());
            if (!thisReference.Equals(clientReference))
                throw new Exception(string.Format("Case_3: 2 grain references are different, while should have been the same: gr1={0}, gr2={1}", thisReference, clientReference));

            return Task.CompletedTask;
        }
    }
}
