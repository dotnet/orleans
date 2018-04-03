using System;
using System.Collections.Generic;
using ReplicatedEventSample.Interfaces;

namespace ReplicatedEventSample.Grains
{
    /// <summary>
    ///  state of an event
    /// </summary>
    [Serializable]
    public class EventState
    {
        /// <summary>
        ///  list of all outcomes, sorted by timestamp
        /// </summary>
        public SortedDictionary<DateTime, Outcome> outcomes;

        public EventState()
        {
            outcomes = new SortedDictionary<DateTime, Outcome>();
        }

        public void Apply(Outcome outcome)
        {
            if (outcome == null)
                throw new ArgumentNullException("outcome");

            // idempotency check: ignore update if already there
            if (outcomes.ContainsKey(outcome.When))
                return;

            outcomes.Add(outcome.When, outcome);
        }
    }
}