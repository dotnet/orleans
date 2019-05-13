using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Providers;
using VotingContract;

namespace VotingData
{
    [StorageProvider(ProviderName = "votes")]
    public class VoteGrain : Grain<Dictionary<string, int>>, IVoteGrain
    {
        private readonly ILogger logger;

        public VoteGrain(ILogger<VoteGrain> logger)
        {
            this.logger = logger;
        }

        public Task<Dictionary<string, int>> Get() => Task.FromResult(this.State);

        public async Task AddVote(string option)
        {
            var stopwatch = Stopwatch.StartNew();
            this.logger.LogInformation("Saving vote");

            var key = option.ToLower();
            if (!this.State.ContainsKey(key))
            {
                this.logger.LogInformation($"Created vote option {option} and voted...");
                this.State.Add(key, 1);
            }
            else
            {
                this.logger.LogInformation($"Voting for {option}...");
                this.State[key] += 1;
            }

            await this.WriteStateAsync();
            this.logger.LogInformation($"Saved vote in { stopwatch.ElapsedMilliseconds }ms");
        }

        public async Task RemoveVote(string option)
        {
            var stopwatch = Stopwatch.StartNew();
            this.logger.LogInformation("Deleting vote option");

            var key = option.ToLower();
            if (!this.State.ContainsKey(key))
            {
                var message = $"Didn't find vote option {key}";
                this.logger.LogWarning(message);
                throw new KeyNotFoundException(message);
            }
            else
            {
                this.logger.LogInformation($"Removed vote option {key}...");
                this.State.Remove(key.ToLower());
            }

            await this.WriteStateAsync();

            this.logger.LogInformation($"Deleted vote option { stopwatch }ms");
        }
    }
}