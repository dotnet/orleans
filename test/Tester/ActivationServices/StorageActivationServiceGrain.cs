using Orleans;
using System.Threading.Tasks;

namespace Tester
{
    public interface IStorageActivationServiceGrain : IGrainWithIntegerKey
    {
        Task<string[]> GetNames();
        Task<string[]> GetExtendedInfo();
    }

    public class StorageActivationServiceGrain : Grain, IStorageActivationServiceGrain
    {
        private readonly IStorageActivationService<string> first;
        private readonly IStorageActivationService<string> second;

        public StorageActivationServiceGrain(
            [StorageActivationService("Blob", stateName: "FirstState")]
            IStorageActivationService<string> first,
            [StorageActivationService("Table")]
            IStorageActivationService<string> second)
        {
            this.first = first;
            this.second = second;
        }

        public Task<string[]> GetNames()
        {
            return Task.FromResult(new[] { this.first.Name, this.second.Name });
        }

        public Task<string[]> GetExtendedInfo()
        {
            return Task.FromResult(new[] { this.first.GetExtendedInfo(), this.second.GetExtendedInfo() });
        }
    }
}
