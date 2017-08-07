using Orleans;
using System.Threading.Tasks;

namespace Tester
{
    public interface IStorageFacetGrain : IGrainWithIntegerKey
    {
        Task<string[]> GetNames();
        Task<string[]> GetExtendedInfo();
    }

    public class StorageFacetGrain : Grain, IStorageFacetGrain
    {
        [StorageFeature("Blob", stateName: "FirstState")]
        public IStorageFeature<string> First { get; set; }
        [StorageFeature("Table")]
        public IStorageFeature<string> Second { get; set; }

        public Task<string[]> GetNames()
        {
            return Task.FromResult(new[] { this.First.Name, this.Second.Name });
        }

        public Task<string[]> GetExtendedInfo()
        {
            return Task.FromResult(new[] { this.First.GetExtendedInfo(), this.Second.GetExtendedInfo() });
        }
    }
}
