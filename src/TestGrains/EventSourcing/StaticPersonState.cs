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
using Orleans.EventSourcing;
using TestGrainInterfaces;

namespace TestGrains
{
    public class StaticPersonState : JournaledGrainState<StaticPersonState>,
        IJournaledGrainStateTransition<StaticPersonState, PersonRegistered>,
        IJournaledGrainStateTransition<StaticPersonState, PersonMarried>,
        IJournaledGrainStateTransition<StaticPersonState, PersonLastNameChanged>
    {
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public GenderType Gender { get; set; }

        public void Apply(StaticPersonState state, PersonRegistered registered)
        {
            state.FirstName = registered.FirstName;
            state.LastName = registered.LastName;
            state.Gender = registered. Gender;
        }

        public void Apply(StaticPersonState state, PersonMarried married)
        {
            // TODO
        }

        public void Apply(StaticPersonState state, PersonLastNameChanged lnChanged)
        {
            state.LastName = lnChanged.LastName;
        }
    }
}