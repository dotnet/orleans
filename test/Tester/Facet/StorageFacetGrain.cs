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
        private readonly IStorageFacet<string> first;
        private readonly IStorageFacet<string> second;

        public StorageFacetGrain(
            [StorageFacet("Blob", stateName: "FirstState")]
            IStorageFacet<string> first,
            [StorageFacet("Table")]
            IStorageFacet<string> second)
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
