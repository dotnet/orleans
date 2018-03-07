using System;
using System.Threading.Tasks;

namespace Orleans.Streams
{
    public interface IStreamQueueCheckpointerFactory
    {
        Task<IStreamQueueCheckpointer<string>> Create(string partition);
    }

    //why is checkpointer has a type param while the checkpointer factory in EventHubAdapterFactory is hard coded to be string type?
    public interface IStreamQueueCheckpointer<TCheckpoint>
    {
        bool CheckpointExists { get; }
        Task<TCheckpoint> Load();
        void Update(TCheckpoint offset, DateTime utcNow);
    }
}
