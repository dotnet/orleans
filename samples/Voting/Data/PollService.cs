using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans;
using VotingContract;

namespace Voting.Data
{
    public partial class PollService
    {
        private readonly IGrainFactory _grainFactory;
        private IUserAgentGrain _userAgentGrain;

        public PollService(IGrainFactory grainFactory)
        {
            _grainFactory = grainFactory;
        }

        public void Initialize(string clientIp)
        {
            _userAgentGrain = _grainFactory.GetGrain<IUserAgentGrain>(clientIp);
        }

        public async Task<string> CreatePollAsync(string question, List<string> options)
        {
            return await _userAgentGrain.CreatePoll(new PollState
            {
                Question = question,
                Options = options.Select(o => (o, 0)).ToList()
            });
        }

        public async Task<(PollState Results, bool Voted)> GetPollResultsAsync(string pollId)
        {
            return await _userAgentGrain.GetPollResults(pollId);
        }

        public async Task<PollState> AddVoteAsync(string pollId, int optionId)
        {
            return await _userAgentGrain.AddVote(pollId, optionId);
        }

        public async Task<IAsyncDisposable> WatchPoll(string pollId, IPollWatcher watcherObject)
        {
            var pollGrain = _grainFactory.GetGrain<IPollGrain>(pollId);
            var watcherReference = await _grainFactory.CreateObjectReference<IPollWatcher>(watcherObject);
            var result = new PollWatcherSubscription(watcherObject, pollGrain, watcherReference);
            return result;
        }
    }
}