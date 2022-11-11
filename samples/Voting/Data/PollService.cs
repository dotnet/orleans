using VotingContract;

namespace Voting.Data;

public sealed partial class PollService
{
    private readonly IGrainFactory _grainFactory;
    private IUserAgentGrain _userAgentGrain;

    public PollService(IGrainFactory grainFactory) => _grainFactory = grainFactory;

    public void Initialize(string clientIp) =>
        _userAgentGrain = _grainFactory.GetGrain<IUserAgentGrain>(clientIp);

    public Task<string> CreatePollAsync(string question, List<string> options) =>
        _userAgentGrain.CreatePoll(new PollState
        {
            Question = question,
            Options = options.Select(o => (o, 0)).ToList()
        });
    
    public Task<(PollState Results, bool Voted)> GetPollResultsAsync(string pollId) =>
        _userAgentGrain.GetPollResults(pollId);

    public Task<PollState> AddVoteAsync(string pollId, int optionId) =>
        _userAgentGrain.AddVote(pollId, optionId);

    public async ValueTask<IAsyncDisposable> WatchPoll(string pollId, IPollWatcher watcherObject)
    {
        var pollGrain = _grainFactory.GetGrain<IPollGrain>(pollId);
        var watcherReference = _grainFactory.CreateObjectReference<IPollWatcher>(watcherObject);
        var result = new PollWatcherSubscription(watcherObject, pollGrain, watcherReference);

        await ValueTask.CompletedTask;
        
        return result;
    }
}