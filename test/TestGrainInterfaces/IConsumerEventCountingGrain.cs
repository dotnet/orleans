using System;
using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    /// <summary>
    /// Stream consumer grain that just counts the events it consumes
    /// </summary>
    public interface IConsumerEventCountingGrain : IGrainWithGuidKey
    {
        Task BecomeConsumer(Guid streamId, string providerToUse);

        Task StopConsuming();

        Task<int> GetNumberConsumed();
    }
}