using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.EventSourcing;
using TestGrainInterfaces;
using System.Collections.Generic;
using Orleans.Runtime;
using System.Linq;

namespace TestGrains
{
    public class PersonGrain : JournaledGrain<PersonState,IPersonEvent>, IPersonGrain
    {

        public Task RegisterBirth(PersonAttributes props)
        {
            if (this.State.FirstName == null)
            {
                RaiseEvent(new PersonRegistered(props.FirstName, props.LastName, props.Gender));

                return ConfirmEvents();
            }

            return Task.CompletedTask;
        }

        public async Task Marry(IPersonGrain spouse)
        {
            if (State.IsMarried)
                throw new NotSupportedException(string.Format("{0} is already married.", State.LastName));

            var spouseData = await spouse.GetTentativePersonalAttributes();

            var events = new List<IPersonEvent>();

            events.Add(new PersonMarried(spouse.GetPrimaryKey(), spouseData.FirstName, spouseData.LastName));

            if (State.LastName != spouseData.LastName)
            {
                events.Add(new PersonLastNameChanged(spouseData.LastName));
            }

            // issue all events atomically
            RaiseEvents(events);
            await ConfirmEvents();
        }

        public Task ChangeLastName(string lastName)
        {
            RaiseEvent(new PersonLastNameChanged(lastName));

            // we are not confirming this event here!
            // therefore, the tentative state and the confirmed state can differ for some time

            return Task.CompletedTask;
        }

        public Task ConfirmChanges()
        {
            return ConfirmEvents();
        }

        public Task<PersonAttributes> GetTentativePersonalAttributes()
        {
            return Task.FromResult(new PersonAttributes
            {
                FirstName = TentativeState.FirstName,
                LastName = TentativeState.LastName,
                Gender = TentativeState.Gender
            });
        }

        public Task<PersonAttributes> GetConfirmedPersonalAttributes()
        {
            return Task.FromResult(new PersonAttributes
            {
                FirstName = State.FirstName,
                LastName = State.LastName,
                Gender = State.Gender
            });
        }

        public Task<int> GetConfirmedVersion()
        {
            return Task.FromResult(Version);
        }

        public Task<int> GetTentativeVersion()
        {
            return Task.FromResult(TentativeVersion);
        }

        private int TentativeVersion
        {
            get
            {
                return Version + UnconfirmedEvents.Count();
            }
        }

        // below is a unit test; ideally this code would be in the Tester project,
        // but this test has to run on the grain, not the client, so we had to add it here

        private static void AssertEqual<T>(T a, T b)
        {
            if (!Object.Equals(a, b))
                throw new OrleansException($"Test failed. Expected = {a}. Actual = {b}.");
        }


        public async Task RunTentativeConfirmedStateTest()
        {
            // initially both the confirmed version and the tentative version are the same: version 0
            AssertEqual(0, Version);
            AssertEqual(0, TentativeVersion);
            AssertEqual(null, State.LastName);
            AssertEqual(null, TentativeState.LastName);

            // now we change the last name
            await ChangeLastName("Organa");

            // while the udpate is pending, the confirmed version and the tentative version are different
            AssertEqual(0, Version);
            AssertEqual(1, TentativeVersion);
            AssertEqual(null, State.LastName);
            AssertEqual("Organa", TentativeState.LastName);

            // let's wait until the update has been confirmed.
            await ConfirmChanges();

            // now the two versions are the same again
            AssertEqual(1, Version);
            AssertEqual(1, TentativeVersion);
            AssertEqual("Organa", State.LastName);
            AssertEqual("Organa", TentativeState.LastName);

            // issue another change
            await ChangeLastName("Solo");

            // again, the confirmed and the tentative versions are different
            AssertEqual(1, Version);
            AssertEqual(2, TentativeVersion);
            AssertEqual("Organa", State.LastName);
            AssertEqual("Solo", TentativeState.LastName);

            // this time, we wait for (what should be) enough time to commit to MemoryStorage.
            // we would never use such timing assumptions in real code. But this is a unit test.
            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(20);
                if (Version == 2) break;
            }

            // now the two versions should be the same again
            AssertEqual(2, Version);
            AssertEqual(2, TentativeVersion);
            AssertEqual("Solo", State.LastName);
            AssertEqual("Solo", TentativeState.LastName);
        }
    }
}
