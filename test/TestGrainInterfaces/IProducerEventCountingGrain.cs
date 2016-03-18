using System;
using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    /// <summary>
    /// Stream producer grain that sends a single event at a time (when told, see SendEvent) and tracks the number of events sent
    /// </summary>
    public interface IProducerEventCountingGrain : IGrainWithGuidKey
    {
        Task BecomeProducer(Guid streamId, string providerToUse);

        /// <summary>
        /// Sends a single event and, upon successful completion, updates the number of events produced.
        /// </summary>
        /// <returns></returns>
        Task SendEvent();

        Task<int> GetNumberProduced();
    }
}