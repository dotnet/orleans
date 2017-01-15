using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.EventSourcing;
using Orleans.Providers;
using TestGrainInterfaces;
using System.Collections.Generic;
using Orleans.Runtime;

namespace TestGrains
{
    public class JournaledPersonGrain : JournaledGrain<PersonState,IPersonEvent>, IJournaledPersonGrain
    {

        public Task RegisterBirth(PersonAttributes props)
        {
            if (this.State.FirstName == null)
            {
                RaiseEvent(new PersonRegistered(props.FirstName, props.LastName, props.Gender));

                return ConfirmEvents();
            }

            return TaskDone.Done;
        }

        public async Task Marry(IJournaledPersonGrain spouse)
        {
            if (State.IsMarried)
                throw new NotSupportedException(string.Format("{0} is already married.", State.LastName));

            var spouseData = await spouse.GetPersonalAttributes();

            var events = new List<IPersonEvent>();

            events.Add(new PersonMarried(spouse.GetPrimaryKey(), spouseData.FirstName, spouseData.LastName));

            if (State.LastName != spouseData.LastName)
            {
                events.Add(new PersonLastNameChanged(spouseData.LastName));
            }

            RaiseEvents(events);

            await ConfirmEvents();
        }

        public Task ChangeLastName(string lastName)
        {
            RaiseEvent(new PersonLastNameChanged(lastName));

            return TaskDone.Done;
        }

        public Task ConfirmChanges()
        {
            return ConfirmEvents();
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

        public Task<PersonAttributes> GetConfirmedPersonalAttributes()
        {
            return Task.FromResult(new PersonAttributes
            {
                FirstName = ConfirmedState.FirstName,
                LastName = ConfirmedState.LastName,
                Gender = ConfirmedState.Gender
            });
        }

        public Task<int> GetConfirmedVersion()
        {
            return Task.FromResult(ConfirmedVersion);
        }

        public Task<int> GetVersion()
        {
            return Task.FromResult(Version);
        }

        private static void AssertEqual<T>(T a, T b)
        {
            if (!Object.Equals(a, b))
                throw new OrleansException($"Test failed. Expected = {a}. Actual = {b}.");
        }

        public async Task RunTentativeConfirmedStateTest()
        {
            // initially both the confirmed version and the tentative version are the same: version 0
            AssertEqual(0, ConfirmedVersion);
            AssertEqual(0, Version);
            AssertEqual(null, ConfirmedState.LastName);
            AssertEqual(null, State.LastName);

            // now we change the last name
            await ChangeLastName("Organa");

            // while the udpate is pending, the confirmed version and the tentative version are different
            AssertEqual(0, ConfirmedVersion);
            AssertEqual(1, Version);
            AssertEqual(null, ConfirmedState.LastName);
            AssertEqual("Organa", State.LastName);

            // let's wait until the update has been confirmed.
            await ConfirmChanges();

            // now the two versions are the same again
            AssertEqual(1, ConfirmedVersion);
            AssertEqual(1, Version);
            AssertEqual("Organa", ConfirmedState.LastName);
            AssertEqual("Organa", State.LastName);

            // issue another change
            await ChangeLastName("Solo");

            // again, the confirmed and the tentative versions are different
            AssertEqual(1, ConfirmedVersion);
            AssertEqual(2, Version);
            AssertEqual("Organa", ConfirmedState.LastName);
            AssertEqual("Solo", State.LastName);

            // this time, we wait for (what should be) enough time to commit to MemoryStorage.
            await Task.Delay(20);

            // now the two versions should be the same again
            AssertEqual(2, ConfirmedVersion);
            AssertEqual(2, Version);
            AssertEqual("Solo", ConfirmedState.LastName);
            AssertEqual("Solo", State.LastName);
        }
    }
}
