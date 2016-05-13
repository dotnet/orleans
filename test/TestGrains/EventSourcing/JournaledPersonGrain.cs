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
