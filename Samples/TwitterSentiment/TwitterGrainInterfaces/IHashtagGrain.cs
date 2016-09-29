using Orleans;
using System.Threading.Tasks;

namespace TwitterGrainInterfaces
{
    /// <summary>
    /// A grain to maintain a sentiment score against a hashtag
    /// </summary>
    public interface IHashtagGrain : IGrainWithIntegerCompoundKey
    {
        Task AddScore(int score, string lastTweet);

        Task<Totals> GetTotals();
    }
}
