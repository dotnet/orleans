using System;
using System.Threading.Tasks;
using System.Linq;
using Orleans;
using TestGrainInterfaces;
using Xunit;
using Assert = Xunit.Assert;
using TestExtensions;
using Xunit.Abstractions;
using Orleans.Runtime;

namespace UnitTests.EventSourcingTests
{
    public class JournaledGrainTests : HostedTestClusterEnsureDefaultStarted, IDisposable
    {
        private const string LoggerPrefix = "Storage.MemoryStorage.1";

        public JournaledGrainTests(DefaultClusterFixture fixture, ITestOutputHelper output) : base(fixture)
        {
            var mgmt = GrainClient.GrainFactory.GetGrain<IManagementGrain>(0);
            var hosts = mgmt.GetHosts().Result.Select(kvp => kvp.Key).ToArray();
            mgmt.SetLogLevel(hosts, LoggerPrefix, (int)Severity.Verbose2);
        }

        public void Dispose()
        {
            var mgmt = GrainClient.GrainFactory.GetGrain<IManagementGrain>(0);
            var hosts = mgmt.GetHosts().Result.Select(kvp => kvp.Key).ToArray();
            mgmt.SetLogLevel(hosts, LoggerPrefix, (int)Severity.Info);
        }


        [Fact, TestCategory("EventSourcing"), TestCategory("Functional")]
        public async Task JournaledGrainTests_Activate()
        {
            var grainWithState = GrainClient.GrainFactory.GetGrain<IJournaledPersonGrain>(Guid.Empty);

            Assert.NotNull(await grainWithState.GetPersonalAttributes());
        }

        [Fact, TestCategory("EventSourcing"), TestCategory("Functional")]
        public async Task JournaledGrainTests_Persist()
        {
            var grainWithState = GrainClient.GrainFactory.GetGrain<IJournaledPersonGrain>(Guid.Empty);

            await grainWithState.RegisterBirth(new PersonAttributes { FirstName = "Luke", LastName = "Skywalker", Gender = GenderType.Male });

            var attributes = await grainWithState.GetPersonalAttributes();

            Assert.NotNull(attributes);
            Assert.Equal("Luke", attributes.FirstName);
        }

        [Fact, TestCategory("EventSourcing"), TestCategory("Functional")]
        public async Task JournaledGrainTests_AppendMoreEvents()
        {
            var leia = GrainClient.GrainFactory.GetGrain<IJournaledPersonGrain>(Guid.NewGuid());
            await leia.RegisterBirth(new PersonAttributes { FirstName = "Leia", LastName = "Organa", Gender = GenderType.Female });

            var han = GrainClient.GrainFactory.GetGrain<IJournaledPersonGrain>(Guid.NewGuid());
            await han.RegisterBirth(new PersonAttributes { FirstName = "Han", LastName = "Solo", Gender = GenderType.Male });

            await leia.Marry(han);

            var attributes = await leia.GetPersonalAttributes();
            Assert.NotNull(attributes);
            Assert.Equal("Leia", attributes.FirstName);
            Assert.Equal("Solo", attributes.LastName);
        }

        [Fact, TestCategory("EventSourcing"), TestCategory("Functional")]
        public async Task JournaledGrainTests_TentativeConfirmedState()
        {
            var leia = GrainClient.GrainFactory.GetGrain<IJournaledPersonGrain>(Guid.NewGuid());

            // the whole test has to run inside the grain, otherwise the interleaving of 
            // the individual steps is nondeterministic
            await leia.RunTentativeConfirmedStateTest();
        }
    }
}