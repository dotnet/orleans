using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Providers;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    [Serializable]
    public class MultifacetTestGrainState
    {
        public int Value { get; set; }
    }

    [StorageProvider(ProviderName = "MemoryStore")]
    public class MultifacetTestGrain : Grain<MultifacetTestGrainState>, IMultifacetTestGrain
    {
        
        public string GetRuntimeInstanceId()
        {
            return RuntimeIdentity;
        }

        #region IMultifacetWriter Members

        public Task SetValue(int x)
        {
            State.Value = x;
            return TaskDone.Done;
        }

        #endregion

        #region IMultifacetReader Members

        Task<int> IMultifacetReader.GetValue()
        {
            return Task.FromResult(State.Value);
        }

        #endregion
    }
}
