using Orleans.Placement;
using Orleans.Providers;
using Orleans.Runtime;
using System.Threading.Tasks;

namespace Orleans.Streams
{
    [PreferLocalPlacement]
    public class StreamCheckpointerGrainGrain : Grain, IStreamCheckpointerGrain
    {
        private readonly IPersistentState<StreamCheckpointerGrainState> _state;

        // TODO Expose the provider name as an option for the GrainStreamQueueCheckpointer
        public StreamCheckpointerGrainGrain(
            [PersistentState("streamcheckpointer", ProviderConstants.DEFAULT_PUBSUB_PROVIDER_NAME)]
            IPersistentState<StreamCheckpointerGrainState> state)
        {
            _state = state;
        }

        public Task<string> Load() => Task.FromResult(_state.State.Checkpoint);

        public async Task Update(string offset)
        {
            _state.State.Checkpoint = offset;
            await _state.WriteStateAsync();
        }
    }
}
