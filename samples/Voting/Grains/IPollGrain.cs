namespace VotingContract;

public interface IPollGrain : IGrainWithStringKey
{
    Task CreatePoll(PollState initialState);
    Task<PollState> GetCurrentResults();
    Task<PollState> AddVote(int optionId);
    Task StartWatching(IPollWatcher watcher);
    Task StopWatching(IPollWatcher watcher);
}

[GenerateSerializer]
public class PollState
{
    [Id(0)] public string Question { get; set; }
    [Id(1)] public List<(string Option, int Votes)> Options { get; set; }
}
