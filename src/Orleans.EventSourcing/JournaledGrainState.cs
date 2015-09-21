/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections;
using System.Collections.Generic;
using Orleans.CodeGeneration;


namespace Orleans.EventSourcing
{
    /// <summary>
    /// Base class for event-sourced grain state classes.
    /// </summary>
    public abstract class JournaledGrainState<TGrainState> : GrainState
        where TGrainState : JournaledGrainState<TGrainState>
    {
        private List<object> events = new List<object>();

        protected JournaledGrainState()
            : base(typeof(TGrainState).FullName)
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

        public override void SetAll(IDictionary<string, object> values)
        {
            base.SetAll(values);

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