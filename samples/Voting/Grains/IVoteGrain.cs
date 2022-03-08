using Orleans;

namespace VotingContract;

public interface IVoteGrain : IGrainWithIntegerKey
{
    Task<Dictionary<string, int>> Get();
    Task AddVote(string option);
    Task RemoveVote(string option);
}
