using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;

namespace VotingContract
{
    public interface IPollGrain : IGrainWithStringKey
    {
        Task CreatePoll(PollState initialState);
        Task<PollState> GetCurrentResults();
        Task<PollState> AddVote(int optionId);
        Task StartWatching(IPollWatcher watcher);
        Task StopWatching(IPollWatcher watcher);
    }

    [Serializable]
    public class PollState
    {
        public string Question { get; set; }
        public List<(string Option, int Votes)> Options { get; set; }
    }
}
