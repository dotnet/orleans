using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LoadTestGrainInterfaces;
using Orleans;
using Orleans.Providers;

namespace LoadTestGrains
{
    public interface IPersistenceLoadTestState : IGrainState
    {
        long Id { get; set; }
        int Val { get; set; }
    }

    [StorageProvider(ProviderName = "AzureStore")]
    public class PersistenceLoadTestGrain : Grain<IPersistenceLoadTestState>, IPersistenceLoadTestGrain
    {
        public override Task OnActivateAsync()
        {
            State.Id = this.GetPrimaryKeyLong();
            return TaskDone.Done;
        }

        public Task<int> GetStateValue()
        {
            //Debug.Assert(State.Id == this.GetPrimaryKeyLong());
            return Task.FromResult(State.Val);
        }

        public Task DoStateWrite(int val)
        {
            //Debug.Assert(State.Id == this.GetPrimaryKeyLong());
            State.Val = val;
            return State.WriteStateAsync();
        }

        public async Task<int> DoStateRead()
        {
            await State.ReadStateAsync();
            //Debug.Assert(State.Id == this.GetPrimaryKeyLong());
            return State.Val;
        }

        public Task Clear()
        {
            return TaskDone.Done;
        }
    }
}
