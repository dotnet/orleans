using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class NullStateGrain : Grain<NullableState>, INullStateGrain
    {
        public async Task SetStateAndDeactivate(NullableState state)
        {
            this.State = state;
            await WriteStateAsync();
            DeactivateOnIdle();
        }

        public Task<NullableState> GetState()
        {
            return Task.FromResult(this.State);
        }
    }
}