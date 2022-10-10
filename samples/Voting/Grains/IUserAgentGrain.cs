using System.Threading.Tasks;
using Orleans;

namespace VotingContract
{
    public interface IUserAgentGrain : IGrainWithStringKey
    {
        Task<string> CreatePoll(PollState initialState);
        Task<(PollState Results, bool Voted)> GetPollResults(string pollId);
        Task<PollState> AddVote(string pollId, int optionId);
    }
}
