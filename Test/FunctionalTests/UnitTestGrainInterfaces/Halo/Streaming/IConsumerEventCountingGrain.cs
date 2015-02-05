using System;
using System.Threading.Tasks;

using Orleans;

namespace UnitTestGrainInterfaces.Halo.Streaming
{
    /// <summary>
    /// Stream consumer grain that just counts the events it consumes
    /// </summary>
    interface IConsumerEventCountingGrain : IGrain
    {
        Task BecomeConsumer(Guid streamId, string providerToUse);

        Task StopConsuming();

        Task<int> GetNumberConsumed();
    }
}