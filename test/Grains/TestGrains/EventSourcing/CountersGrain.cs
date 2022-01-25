using Orleans;
using Orleans.EventSourcing;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TestGrainInterfaces;

namespace TestGrains
{
    /// <summary>
    /// An example of a journaled grain that counts statistics.
    /// 
    /// For configuration options, see derived classes in CountersGrainVariations.cs
    /// </summary>
    [GrainType("simple-counters-grain")]
    public class CountersGrain : JournaledGrain<CountersGrain.GrainState>, ICountersGrain
    {
        /// <summary>
        /// The state of this grain is a dictionary that keeps a count for each key.
        /// We define this as a nested class, just for scoping convenience.
        /// </summary>
        [Serializable]
        [GenerateSerializer]
        public class GrainState
        {
            /// <summary>  the current count </summary>
            [Id(0)]
            public Dictionary<string, int> Counts { get; set; }

            public GrainState()
            {
                Counts = new Dictionary<string, int>();
            }

            public void Apply(UpdatedEvent e)
            {
                if (Counts.ContainsKey(e.Key))
                    Counts[e.Key] += e.Amount;
                else
                    Counts.Add(e.Key, e.Amount);
            }

            public void Apply(ResetAllEvent e)
            {
                Counts.Clear();
            }
        }

        /// <summary>
        /// An event representing a counter update
        /// </summary>
        [Serializable]
        [GenerateSerializer]
        public class UpdatedEvent
        {
            [Id(0)]
            public string Key { get; set; }
            [Id(1)]
            public int Amount { get; set; }
        }

        /// <summary>
        /// An event representing a reset of all counters
        /// </summary>
        [Serializable]
        [GenerateSerializer]
        public class ResetAllEvent
        {
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            // on activation, we load lazily (do not wait until the current state is loaded).
            return Task.CompletedTask;
        }

        public async Task Add(string key, int amount, bool wait_for_confirmation)
        {
            RaiseEvent(new UpdatedEvent() { Key = key, Amount = amount });

            // optionally, wait until the event has been persisted to storage
            if (wait_for_confirmation)
                await ConfirmEvents();
        }

        public async Task Reset(bool wait_for_confirmation)
        {
            RaiseEvent(new ResetAllEvent());

            // optionally, wait until the event has been persisted to storage
            if (wait_for_confirmation)
                await ConfirmEvents();
        }

        public Task ConfirmAllPreviouslyRaisedEvents()
        {
            return ConfirmEvents();
        }
            

        public Task<int> GetTentativeCount(string key)
        {
            return Task.FromResult(TentativeState.Counts[key]);
        }

        public Task<IReadOnlyDictionary<string, int>> GetTentativeState()
        {
            return Task.FromResult((IReadOnlyDictionary<string, int>)TentativeState.Counts);
        }

        public Task<IReadOnlyDictionary<string, int>> GetConfirmedState()
        {
            return Task.FromResult((IReadOnlyDictionary<string, int>)State.Counts);
        }


        // some providers allow you to look at the log of events
        public Task<IReadOnlyList<object>> GetAllEvents()
        {
            return RetrieveConfirmedEvents(0, Version);
        }
    }
}
