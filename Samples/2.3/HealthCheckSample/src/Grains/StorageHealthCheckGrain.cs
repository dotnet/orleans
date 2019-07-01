using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Placement;
using Orleans.Runtime;

namespace Grains
{
    [PreferLocalPlacement]
    public class StorageHealthCheckGrain : Grain, IStorageHealthCheckGrain
    {
        private readonly IPersistentState<Guid> state;

        public StorageHealthCheckGrain([PersistentState("State")] IPersistentState<Guid> state)
        {
            this.state = state;
        }

        public async Task CheckAsync()
        {
            try
            {
                state.State = Guid.NewGuid();
                await state.WriteStateAsync();
                await state.ReadStateAsync();
                await state.ClearStateAsync();
            }
            finally
            {
                DeactivateOnIdle();
            }
        }
    }
}
