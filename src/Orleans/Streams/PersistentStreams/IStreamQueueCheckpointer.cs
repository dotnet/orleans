using System;
using System.Threading.Tasks;

namespace Orleans.Streams
{
    public interface IStreamQueueCheckpointer<TCheckpoint>
    {
        bool CheckpointExists { get; }
        Task<TCheckpoint> Load();
        void Update(TCheckpoint offset, DateTime utcNow);
    }
}
