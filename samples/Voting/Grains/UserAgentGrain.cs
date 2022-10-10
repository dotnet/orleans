using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Orleans;
using System.Diagnostics;
using VotingContract;

namespace Grains
{
    /// <summary>
    /// This grain demonstrates a simple way to throttle a given client
    /// (identified by their IP address, which is used as the primary key of the grain)
    /// It maintains a count of recent calls and throttles if they exceed a defined threshold.
    /// The score decays over time until, allowing the client to resume making calls.
    /// </summary>
    internal class UserAgentGrain : Grain, IUserAgentGrain
    {
        private const double DecayRate = ThrottleThreshold / (double)DecayPeriod;
        private const int ThrottleThreshold = 10;
        private const int DecayPeriod = 5;

        private readonly IGrainFactory _grainFactory;
        private readonly HashSet<string> _votedPolls = new();
        private readonly HashSet<string> _myPolls = new();

        private double _throttleScore;
        private Stopwatch _stopwatch = new Stopwatch();

        public UserAgentGrain(IGrainFactory grainFactory)
        {
            _grainFactory = grainFactory;
        }

        public async Task<(PollState Results, bool Voted)> GetPollResults(string pollId)
        {
            Throttle();

            // Get a reference to the poll grain
            var pollGrain = _grainFactory.GetGrain<IPollGrain>(pollId);

            // Get the current poll results
            var results = await pollGrain.GetCurrentResults();

            // Return the results as well as whether we've voted in the poll or not
            return (Results: results, Voted: _votedPolls.Contains(pollId));
        }

        public async Task<string> CreatePoll(PollState initialState)
        {
            Throttle();

            // Limit the number of polls any one user can make
            if (_myPolls.Count >= 5)
            {
                throw new InvalidOperationException("You have already created 5 polls, which is enough for anybody.");
            }

            // Generate a new id and get a reference to the PollGrain with that id
            var pollId = Guid.NewGuid().ToString("N").Substring(0, 6);
            var pollGrain = _grainFactory.GetGrain<IPollGrain>(pollId);

            // Create the poll. We could avoid colitions here by making this return an error if a poll
            // with that id already exists.
            await pollGrain.CreatePoll(initialState);

            _myPolls.Add(pollId);
            return pollId;
        }

        public async Task<PollState> AddVote(string pollId, int optionId)
        {
            Throttle();

            // First, check to see whether we've voted already.
            if (_votedPolls.Contains(pollId))
            {
                throw new InvalidOperationException("You have already voted in that poll!");
            }

            // Vote
            var pollGrain = _grainFactory.GetGrain<IPollGrain>(pollId);
            var result = await pollGrain.AddVote(optionId);

            // Record our vote to prevent double-voting.
            _votedPolls.Add(pollId);
            return result;
        }

        private void Throttle()
        {
            // Work out how long it's been since the last call.
            var elapsedSeconds = _stopwatch.Elapsed.TotalSeconds;
            _stopwatch.Restart();

            // Calculate a new score based on a constant rate of score decay and the
            // time which elapsed since the last call.
            _throttleScore = Math.Max(0, _throttleScore - elapsedSeconds * DecayRate) + 1;

            // If the user has exceeded the threshold, deny their request and give them a
            // helpful warning.
            if (_throttleScore > ThrottleThreshold)
            {
                var remainingSeconds = Math.Max(0, (int)Math.Ceiling((_throttleScore - (ThrottleThreshold - 1)) / DecayRate));
                throw new ThrottlingException($"Request rate exceeded, wait {remainingSeconds}s before retrying"); 
            }
        }
    }
}
