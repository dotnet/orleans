using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Streams;

namespace LoadTestGrainInterfaces
{
    public interface IExplicitConsumerGrain : IGrain
    {
        Task Subscribe(Guid streamId, string streamNamespace, StreamingLoadTestStartEvent item, StreamSequenceToken token);
    }
}