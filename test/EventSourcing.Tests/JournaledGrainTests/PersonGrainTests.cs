﻿using System;
using System.Threading.Tasks;
using System.Linq;
using Orleans;
using TestGrainInterfaces;
using Xunit;
using Assert = Xunit.Assert;
using TestExtensions;
using Xunit.Abstractions;
using Orleans.Runtime;

namespace EventSourcing.Tests
{
    [Collection("EventSourcingCluster"), TestCategory("EventSourcing"), TestCategory("Functional")]
    public class PersonGrainTests
    {
        private readonly EventSourcingClusterFixture fixture;

        public PersonGrainTests(EventSourcingClusterFixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact]
        public async Task JournaledGrainTests_Activate()
        {
            var grainWithState = this.fixture.GrainFactory.GetGrain<IPersonGrain>(Guid.Empty);

            Assert.NotNull(await grainWithState.GetTentativePersonalAttributes());
        }

        [Fact]
        public async Task JournaledGrainTests_Persist()
        {
            var grainWithState = this.fixture.GrainFactory.GetGrain<IPersonGrain>(Guid.Empty);

            await grainWithState.RegisterBirth(new PersonAttributes { FirstName = "Luke", LastName = "Skywalker", Gender = GenderType.Male });

            var attributes = await grainWithState.GetTentativePersonalAttributes();

            Assert.NotNull(attributes);
            Assert.Equal("Luke", attributes.FirstName);
        }

        [Fact]
        public async Task JournaledGrainTests_AppendMoreEvents()
        {
            var leia = this.fixture.GrainFactory.GetGrain<IPersonGrain>(Guid.NewGuid());
            await leia.RegisterBirth(new PersonAttributes { FirstName = "Leia", LastName = "Organa", Gender = GenderType.Female });

            var han = this.fixture.GrainFactory.GetGrain<IPersonGrain>(Guid.NewGuid());
            await han.RegisterBirth(new PersonAttributes { FirstName = "Han", LastName = "Solo", Gender = GenderType.Male });

            await leia.Marry(han);

            var attributes = await leia.GetTentativePersonalAttributes();
            Assert.NotNull(attributes);
            Assert.Equal("Leia", attributes.FirstName);
            Assert.Equal("Solo", attributes.LastName);
        }

        [Fact]
        public async Task JournaledGrainTests_TentativeConfirmedState()
        {
            var leia = this.fixture.GrainFactory.GetGrain<IPersonGrain>(Guid.NewGuid());

            // the whole test has to run inside the grain, otherwise the interleaving of 
            // the individual steps is nondeterministic
            await leia.RunTentativeConfirmedStateTest();
        }
    }
}