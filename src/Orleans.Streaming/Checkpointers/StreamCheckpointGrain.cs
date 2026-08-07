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
    [GrainType("stream.checkpoint")]
    public class StreamCheckpointGrain : Grain, IStreamCheckpointerGrain
    {
        internal const string StateName = "chk";
        private readonly IPersistentState<StreamCheckpointerGrainState> _state;

        public StreamCheckpointGrain(
            [PersistentState(StateName, ProviderConstants.DEFAULT_PUBSUB_PROVIDER_NAME)]
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

        public async ValueTask<string> Update(
            string offset,
            string expectedCheckpoint,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(_state.State.Checkpoint, expectedCheckpoint, StringComparison.Ordinal))
            {
                return _state.State.Checkpoint;
            }

            var previousCheckpoint = _state.State.Checkpoint;
            _state.State.Checkpoint = offset;
            try
            {
                await _state.WriteStateAsync(cancellationToken);
            }
            catch
            {
                _state.State.Checkpoint = previousCheckpoint;
                throw;
            }

            return offset;
        }
    }

    [PreferLocalPlacement]
    [GrainType("stream.checkpoint.configured")]
    internal sealed class ConfiguredStreamCheckpointGrain
        : StreamCheckpointGrain, IConfiguredStreamCheckpointerGrain
    {
        public ConfiguredStreamCheckpointGrain(
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
            public string StateName => StreamCheckpointGrain.StateName;

            public string StorageName => storageProviderName;
        }
    }
}
