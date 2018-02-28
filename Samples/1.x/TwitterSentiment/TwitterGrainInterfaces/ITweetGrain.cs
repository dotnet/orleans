using Orleans;
using Orleans.Concurrency;
using System.Threading.Tasks;

namespace TwitterGrainInterfaces
{
    /// <summary>
    /// A grain to act as the API into Orleans, and fan out read/writes to multiple hashtag grains
    /// </summary>
    public interface ITweetDispatcherGrain : IGrainWithIntegerKey
    {
        Task AddScore(int score, string[] hashtags, string tweet);

        Task<Totals[]> GetTotals(string[] hashtags);
    }
}
