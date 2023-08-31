using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    [Serializable]
    [GenerateSerializer]
    public class SimpleGrainState
    {
        [Id(0)]
        public int A { get; set; }
        [Id(1)]
        public int EventDelay { get; set; }
    }

    /// <summary>
    /// A simple grain that allows to set two arguments and then multiply them.
    /// </summary>
    [Orleans.Providers.StorageProvider(ProviderName = "MemoryStore")]
    public class PromiseForwardGrain : Grain<SimpleGrainState>, IPromiseForwardGrain
    {
        protected  ISimpleGrain MySimpleGrain { get; set; }
        protected int b = 0;
        public Task<int> GetAxB_Async()
        {
            return GetSimpleGrain().GetAxB();
        }
        public Task<int> GetAxB_Async(int a, int b)
        {
            return GetSimpleGrain().GetAxB(a, b);
        }
        public Task SetA_Async(int a)
        {
            return GetSimpleGrain().SetA(a);
        }
        public Task SetB_Async(int b)
        {
            return GetSimpleGrain().SetB(b);
        }
        public Task IncrementA_Async()
        {
            return GetSimpleGrain().IncrementA();
        }
        public Task<int> GetA_Async()
        {
            return GetSimpleGrain().GetA();
        }

        public async Task SetA(int a)
        {
            await GetSimpleGrain().SetA(a);
        }
        public async Task SetB(int a)
        {
            await GetSimpleGrain().SetB(a);
        }
        public Task<int> GetAxB()
        {
            return GetSimpleGrain().GetAxB();
        }
        public Task<int> GetAxB(int a, int b)
        {
            return GetSimpleGrain().GetAxB(a, b);
        }
        public async Task IncrementA()
        {
            await GetSimpleGrain().IncrementA();
        }
        public Task<int> GetA()
        {
            return GetSimpleGrain().GetA();
        }
        
        private ISimpleGrain GetSimpleGrain()
        {
            if( MySimpleGrain == null )
                MySimpleGrain = GrainFactory.GetGrain<ISimpleGrain>((new Random()).Next(), SimpleGrain.SimpleGrainNamePrefix);

            return MySimpleGrain;
        }

        public Task<int> A
        {
            get { return Task.FromResult(State.A); }
        }
    }
}
