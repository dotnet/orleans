using System.Threading.Tasks;
using Orleans;

namespace Grains
{
    public interface ISummaryGrain : IGrainWithGuidKey
    {
        Task SetAsync(string name, int value);

        Task<int?> TryGetAsync(string name);
    }
}