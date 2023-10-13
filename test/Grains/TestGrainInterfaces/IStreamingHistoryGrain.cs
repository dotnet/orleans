using Orleans.Runtime;

namespace UnitTests.GrainInterfaces
{
    public interface IStreamingHistoryGrain : IGrainWithStringKey
    {
        Task BecomeConsumer(StreamId streamId, string provider, string filterData = null);

        Task StopBeingConsumer();

        Task<List<int>> GetReceivedItems();
    }
}