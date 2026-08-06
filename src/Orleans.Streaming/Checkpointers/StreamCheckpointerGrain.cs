using System.Threading;
using System.Threading.Tasks;
using Orleans.Placement;
using Orleans.Providers;
using Orleans.Runtime;

namespace Orleans.Streams
{
    /// <summary>
    /// Persists a stream queue checkpoint using Orleans grain storage.
    /// </summary>
    [PreferLocalPlacement]
    [GrainType("streamcheckpointergrain")]
    public class StreamCheckpointerGrainGrain : Grain, IStreamCheckpointerGrain
    {
        private readonly IPersistentState<StreamCheckpointerGrainState> _state;

        public StreamCheckpointerGrainGrain(
            [PersistentState("streamcheckpointer", ProviderConstants.DEFAULT_PUBSUB_PROVIDER_NAME)]
            IPersistentState<StreamCheckpointerGrainState> state)
        {
            _state = state;
        }

        public ValueTask<string> Load(CancellationToken cancellationToken)
        {
            return cancellationToken.IsCancellationRequested
                ? ValueTask.FromCanceled<string>(cancellationToken)
                : ValueTask.FromResult(_state.State.Checkpoint);
        }

        public async ValueTask Update(string offset, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _state.State.Checkpoint = offset;
            await _state.WriteStateAsync(cancellationToken);
        }
    }

    [PreferLocalPlacement]
    [GrainType("configuredstreamcheckpointer")]
    internal sealed class ConfiguredStreamCheckpointerGrain
        : StreamCheckpointerGrainGrain, IConfiguredStreamCheckpointerGrain
    {
        public ConfiguredStreamCheckpointerGrain(
            IGrainContext grainContext,
            IPersistentStateFactory persistentStateFactory)
            : base(persistentStateFactory.Create<StreamCheckpointerGrainState>(
                grainContext,
                new CheckpointStateConfiguration(
                    GrainStreamQueueCheckpointer.GetConfiguredStorageProviderName(
                        grainContext.GrainId.Key.AsSpan()))))
        {
        }

        private sealed class CheckpointStateConfiguration(string storageProviderName) : IPersistentStateConfiguration
        {
            public string StateName => "streamcheckpointer";

            public string StorageName => storageProviderName;
        }
    }
}
