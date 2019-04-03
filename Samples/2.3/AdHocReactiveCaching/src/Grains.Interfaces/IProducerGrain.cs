using System;
using System.Threading.Tasks;
using Grains.Models;
using Orleans;

namespace Grains
{
    public interface IProducerGrain : IGrainWithStringKey
    {
        Task StartAsync(int increment, TimeSpan delay);
        Task<VersionedValue<int>> GetAsync();
        Task<VersionedValue<int>> LongPollAsync(VersionToken knownVersion);
    }
}
