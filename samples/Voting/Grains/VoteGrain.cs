using System.Diagnostics;
using Orleans;
using Orleans.Providers;
using Orleans.Runtime;
using VotingContract;

namespace VotingData;

[StorageProvider(ProviderName = "votes")]
public class VoteGrain : Grain, IVoteGrain
{
    private readonly ILogger _logger;
    private readonly IPersistentState<Dictionary<string, int>> _state;

    public VoteGrain(
        [PersistentState("votes", storageName: "votes")] IPersistentState<Dictionary<string, int>> state,
        ILogger<VoteGrain> logger)
    {
        _logger = logger;
        _state = state;
    }

    public Task<Dictionary<string, int>> Get() => Task.FromResult(_state.State);

    public async Task AddVote(string option)
    {
        var stopwatch = Stopwatch.StartNew();
        _logger.LogInformation("Saving vote");

        var key = option.ToLowerInvariant();
        if (!_state.State.ContainsKey(key))
        {
            _logger.LogInformation("Created a vote option and recorded a vote");
            _state.State.Add(key, 1);
        }
        else
        {
            _logger.LogInformation("Recorded a vote for an existing option");
            _state.State[key] += 1;
        }

        await _state.WriteStateAsync();
        _logger.LogInformation("Saved vote in {ElapsedMilliseconds}ms", stopwatch.ElapsedMilliseconds);
    }

    public async Task RemoveVote(string option)
    {
        var stopwatch = Stopwatch.StartNew();
        _logger.LogInformation("Deleting vote option");

        var key = option.ToLowerInvariant();
        if (!_state.State.ContainsKey(key))
        {
            _logger.LogWarning("Didn't find the requested vote option");
            throw new KeyNotFoundException("The requested vote option was not found.");
        }
        else
        {
            _logger.LogInformation("Removed a vote option");
            _state.State.Remove(key);
        }

        await _state.WriteStateAsync();

        _logger.LogInformation("Deleted vote option {ElapsedMilliseconds}ms", stopwatch.ElapsedMilliseconds);
    }
}
