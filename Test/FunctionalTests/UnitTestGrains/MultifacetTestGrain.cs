using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;

namespace MultifacetGrain
{
    public interface IMultifacetTestGrainState : IGrainState
    {
        int Value { get; set; }
    }

    public class MultifacetTestGrain : Grain<IMultifacetTestGrainState>, IMultifacetTestGrain
    {
        
        public string GetRuntimeInstanceId()
        {
            return this.RuntimeIdentity;
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
