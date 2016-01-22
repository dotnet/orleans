using System;
using System.Collections;
using System.Collections.Generic;
using Orleans.CodeGeneration;


namespace Orleans.EventSourcing
{
    /// <summary>
    /// Base class for event-sourced grain state classes.
    /// </summary>
    public abstract class JournaledGrainState<TGrainState>
        where TGrainState : JournaledGrainState<TGrainState>
    {
        private List<object> events = new List<object>();
        protected TGrainState State;

        protected JournaledGrainState()
        {
        }

        public IEnumerable Events
        {
            get { return events.AsReadOnly(); }
        }

        public int Version { get; private set; }

        public void AddEvent<TEvent>(TEvent @event)
            where TEvent : class
        {
            events.Add(@event);

            StateTransition(@event);

            Version++;
        }

        public void SetAll(TGrainState value)
        {
            State = value;
            foreach (var @event in Events)
                StateTransition(@event);
        }

        private void StateTransition<TEvent>(TEvent @event)
            where TEvent : class
        {
            dynamic me = this;

            try
            {
                me.Apply(@event);
            }
            catch(MissingMethodException)
            {
                OnMissingStateTransition(@event);
            }
        }

        protected virtual void OnMissingStateTransition(object @event)
        {
            // Log
        }
    }
}
