using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Providers;
using Orleans.Runtime;
using VotingContract;

namespace VotingData
{
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

            var key = option.ToLower();
            if (!_state.State.ContainsKey(key))
            {
                _logger.LogInformation("Created vote option {Option} and voted...", option);
                _state.State.Add(key, 1);
            }
            else
            {
                _logger.LogInformation("Voting for {Option}...", option);
                _state.State[key] += 1;
            }

            await _state.WriteStateAsync();
            _logger.LogInformation("Saved vote in {ElapsedMilliseconds}ms", stopwatch.ElapsedMilliseconds);
        }

        public async Task RemoveVote(string option)
        {
            var stopwatch = Stopwatch.StartNew();
            _logger.LogInformation("Deleting vote option");

            var key = option.ToLower();
            if (!_state.State.ContainsKey(key))
            {
                _logger.LogWarning("Didn't find vote option {Option}", key);
                throw new KeyNotFoundException($"Didn't find vote option {key}");
            }
            else
            {
                _logger.LogInformation("Removed vote option {Option}...", key);
                _state.State.Remove(key.ToLower());
            }

            await _state.WriteStateAsync();

            _logger.LogInformation("Deleted vote option {ElapsedMilliseconds}ms", stopwatch.ElapsedMilliseconds);
        }
    }
}