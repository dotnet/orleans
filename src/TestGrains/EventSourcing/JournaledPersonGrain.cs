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
using System.Threading.Tasks;
using Orleans;
using Orleans.EventSourcing;
using Orleans.Providers;
using TestGrainInterfaces;

namespace TestGrains
{
    [StorageProvider(ProviderName = "MemoryStore")]
    public class JournaledPersonGrain : JournaledGrain<PersonState>, IJournaledPersonGrain
    {
        public Task RegisterBirth(PersonAttributes props)
        {
            return RaiseStateEvent(new PersonRegistered(props.FirstName, props.LastName, props.Gender));
        }

        public async Task Marry(IJournaledPersonGrain spouse)
        {
            if (State.IsMarried)
                throw new NotSupportedException(string.Format("{0} is already married.", State.LastName));

            var spouseData = await spouse.GetPersonalAttributes();

            await RaiseStateEvent(
                new PersonMarried(spouse.GetPrimaryKey(), spouseData.FirstName, spouseData.LastName),
                commit: false); // We are not storing the first event here

            if (State.LastName != spouseData.LastName)
            {
                await RaiseStateEvent(
                    new PersonLastNameChanged(spouseData.LastName),
                    commit: false);
            }

            // We might need a different, more explicit, persstence API for ES. 
            // Reusing the current API for now.
            await this.WriteStateAsync();
        }
        
        public Task<PersonAttributes> GetPersonalAttributes()
        {
            return Task.FromResult(new PersonAttributes
                {
                    FirstName = State.FirstName,
                    LastName = State.LastName,
                    Gender = State.Gender
                });
        }
    }
}