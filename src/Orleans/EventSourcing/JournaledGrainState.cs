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
using System.Collections.Generic;
using Orleans.CodeGeneration;


namespace Orleans.EventSourcing
{
    public abstract class JournaledGrainState<TGrainState> : GrainState
        where TGrainState : JournaledGrainState<TGrainState>
    {
        private List<StateEvent> events = new List<StateEvent>();

        protected JournaledGrainState(Type type)
            : base(type.FullName)
        {
        }

        public IEnumerable<StateEvent> Events
        {
            get { return events.AsReadOnly(); }
        }

        public int Version { get; private set; }

        public void AddEvent<TEvent>(TEvent @event)
            where TEvent: StateEvent
        {
            events.Add(@event);

            DynamicStateTransition(@event);
            //StaticStateTransition(@event);

            Version++;
        }

        public override void SetAll(IDictionary<string, object> values)
        {
            base.SetAll(values);

            dynamic me = this;

            foreach (StateEvent @event in Events)
                me.Apply(@event);
        }

        private void DynamicStateTransition<TEvent>(TEvent @event)
            where TEvent : StateEvent
        {
            dynamic me = this;
            me.Apply(this, @event);
        }

        private void StaticStateTransition<TEvent>(TEvent @event)
            where TEvent : StateEvent
        {
            var transition = this as IJournaledGrainStateTransition<TGrainState, TEvent>;
            
            if(transition != null)
                transition.Apply(this as TGrainState, @event);
        }
    }
}