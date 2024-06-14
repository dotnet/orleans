using System;
using System.Threading.Tasks;

namespace Orleans.Streams
{
    public class GrainStreamQueueCheckpointer : IStreamQueueCheckpointer<string>
    {
        // TODO Make this configurable
        private static readonly TimeSpan DEFAULT_CHECKPOINT_PERSIST_INTERVAL = TimeSpan.FromMinutes(1);

        private readonly IStreamCheckpointerGrain _grain;

        private string _checkpoint;
        private Task _inProgressSave;
        private DateTime? _throttleSavesUntilUtc;

        public GrainStreamQueueCheckpointer(IStreamCheckpointerGrain grain)
        {
            _grain = grain;
        }

        public bool CheckpointExists => _checkpoint is not null;

        public static async Task<IStreamQueueCheckpointer<string>> Create(string providerName, string partition, string serviceId, IClusterClient clusterClient)
        {
            var grain = clusterClient.GetGrain<IStreamCheckpointerGrain>($"{providerName}_{serviceId}_{partition}");

            var checkpoint = new GrainStreamQueueCheckpointer(grain);
            _ = await checkpoint.Load();

            return checkpoint;
        }

        public async Task<string> Load()
        {
            _checkpoint = await _grain.Load();
            return _checkpoint;
        }

        public void Update(string offset, DateTime utcNow)
        {
            // if offset has not changed, do nothing
            if (string.Compare(_checkpoint, offset, StringComparison.Ordinal) == 0)
            {
                return;
            }

            // if we've saved before but it's not time for another save or the last save operation has not completed, do nothing
            if (_throttleSavesUntilUtc.HasValue && (_throttleSavesUntilUtc.Value > utcNow || !_inProgressSave.IsCompleted))
            {
                return;
            }

            _checkpoint = offset;
            _throttleSavesUntilUtc = utcNow + DEFAULT_CHECKPOINT_PERSIST_INTERVAL;
            _inProgressSave = _grain.Update(_checkpoint);
            _inProgressSave.Ignore();
        }
    }
}
