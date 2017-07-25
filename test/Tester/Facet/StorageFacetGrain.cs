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
        [StorageFacet("Blob", stateName: "FirstState")]
        public IStorageFacet<string> First { get; set; }
        [StorageFacet("Table")]
        public IStorageFacet<string> Second { get; set; }

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
