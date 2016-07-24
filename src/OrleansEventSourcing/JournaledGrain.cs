using System;
using System.Collections.Generic;
using System.Threading.Tasks;


namespace Orleans.EventSourcing
{
    /// <summary>
    /// The base class for all grain classes that have event-sourced state.
    /// </summary>
    public class JournaledGrain<TGrainState> : Grain<TGrainState>
        where TGrainState : JournaledGrainState<TGrainState>
    {
        /// <summary>
        /// Helper method for raising events, applying them to TGrainState and optionally committing to storage
        /// </summary>
        /// <param name="event">Event to raise</param>
        /// <param name="commit">Whether or not the event needs to be immediately committed to storage</param>
        /// <returns></returns>
        protected Task RaiseStateEvent<TEvent>(TEvent @event, bool commit = true)
            where TEvent : class
        {
            if (@event == null) throw new ArgumentNullException("event");

            State.AddEvent(@event);
            return commit ? WriteStateAsync() : TaskDone.Done;
        }
    }
}
