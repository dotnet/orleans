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
using System.Threading.Tasks;


namespace Orleans.EventSourcing
{
    public class JournaledGrain<TGrainState> : Grain<TGrainState>
        where TGrainState : JournaledGrainState
    {
        protected delegate void ApplyAction(TGrainState state, StateEvent @event);

        /// <summary>
        /// This methiod is for events that know how to apply themselves to TGrainState, subclasses of StateEvent&lt;T&gt;.
        /// </summary>
        /// <param name="event">Event to raise</param>
        /// <param name="commit">Whether or not the event needs to be immediately committed to storage</param>
        /// <returns></returns>
        protected Task RaiseStateEvent(StateEvent @event, bool commit = true)
        {
            if (@event == null) throw new ArgumentNullException("event");

            State.AddEvent(@event);
            return commit ? WriteStateAsync() : TaskDone.Done;
        }
    }
}