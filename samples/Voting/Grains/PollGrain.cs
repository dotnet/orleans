using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Grains;
using Orleans;
using Orleans.Runtime;
using VotingContract;

namespace VotingData
{
    public class PollGrain : Grain, IPollGrain
    {
        private readonly IPersistentState<PollState> _votes;

        public PollGrain(
            [PersistentState(stateName: "pollState", storageName: "votes")]
            IPersistentState<PollState> state)
        {
            _votes = state;
        }

        public Task<PollState> GetCurrentResults() => Task.FromResult(_votes.State);

        public async Task CreatePoll(PollState initialState)
        {
            // Set the state and persist it
            _votes.State = initialState;
            await _votes.WriteStateAsync();
        }

        public async Task<PollState> AddVote(int optionId)
        {
            // Perform input validation
            var options = _votes.State.Options;
            if (optionId < 0 || optionId >= options.Count)
            {
                throw new KeyNotFoundException($"Invalid option {optionId}");
            }

            // Add the vote & persist the updated state.
            var (Option, Votes) = options[optionId];
            options[optionId] = (Option, Votes + 1);
            await _votes.WriteStateAsync();

            // Notify the watchers.
            _pollWatchers.Notify(watcher => watcher.OnPollUpdated(_votes.State));
            return _votes.State;
        }

        private readonly ObserverManager<IPollWatcher> _pollWatchers = new(TimeSpan.FromMinutes(1));

        public Task StartWatching(IPollWatcher watcher)
        {
            _pollWatchers.Subscribe(watcher);
            return Task.CompletedTask;
        }

        public Task StopWatching(IPollWatcher watcher)
        {
            _pollWatchers.Unsubscribe(watcher);
            return Task.CompletedTask;
        }
    }
}