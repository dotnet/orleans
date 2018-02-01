using System;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using UnitTests.GrainInterfaces;
using Orleans.TestingHost;
using Xunit;
using TestExtensions;
using Tester;
using Microsoft.Extensions.Logging;
using Orleans.EventSourcing.CustomStorage;
using Orleans.TestingHost.Utils;

namespace Tests.GeoClusterTests
{
    [TestCategory("GeoCluster")]
    public class BasicLogTestGrainTests : IClassFixture<BasicLogTestGrainTests.Fixture>
    {
        private readonly Fixture fixture;
        private Random random;

        public class Fixture : BaseAzureTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.Options.InitialSilosCount = 1;

                builder.ConfigureLegacyConfiguration(legacy =>
                {
                    legacy.ClusterConfiguration.AddMemoryStorageProvider("Default");
                    legacy.ClusterConfiguration.AddMemoryStorageProvider("MemoryStore");
                    legacy.ClusterConfiguration.AddAzureTableStorageProvider("AzureStore");

                    legacy.ClusterConfiguration.AddAzureTableStorageProvider();
                    legacy.ClusterConfiguration.AddStateStorageBasedLogConsistencyProvider();
                    legacy.ClusterConfiguration.AddLogStorageBasedLogConsistencyProvider();
                    legacy.ClusterConfiguration.AddCustomStorageInterfaceBasedLogConsistencyProvider("CustomStorage");

                    legacy.ClusterConfiguration.AddCustomStorageInterfaceBasedLogConsistencyProvider("CustomStoragePrimaryCluster", "A");
                });
            }
        }

        public BasicLogTestGrainTests(Fixture fixture)
        {
            this.fixture = fixture;
            fixture.EnsurePreconditionsMet();
            this.random = new Random();
        }

        [SkippableFact]
        public async Task DefaultStorage()
        {
            await DoBasicLogTestGrainTest("TestGrains.LogTestGrainDefaultStorage");
        }
        [SkippableFact]
        public async Task MemoryStorage()
        {
            await DoBasicLogTestGrainTest("TestGrains.LogTestGrainMemoryStorage");
        }
        [SkippableFact]
        public async Task SharedStorage()
        {
            await DoBasicLogTestGrainTest("TestGrains.LogTestGrainSharedStateStorage");
        }
        [SkippableFact]
        public async Task SharedLogStorage()
        {
            await DoBasicLogTestGrainTest("TestGrains.LogTestGrainSharedLogStorage");
        }
        [SkippableFact]
        public async Task CustomStorage()
        {
            await DoBasicLogTestGrainTest("TestGrains.LogTestGrainCustomStorage");
        }
        [SkippableFact]
        public async Task GsiStorage()
        {
            await DoBasicLogTestGrainTest("TestGrains.GsiLogTestGrain");
        }

        private int GetRandom()
        {
            lock (random)
                return random.Next();
        }


        private async Task DoBasicLogTestGrainTest(string grainClass, int phases = 100)
        {
            await ThreeCheckers(grainClass, phases);
        }

        private async Task ThreeCheckers(string grainClass, int phases)
        {
            // Global 
            Func<Task> checker1 = async () =>
            {
                int x = GetRandom();
                var grain = this.fixture.GrainFactory.GetGrain<ILogTestGrain>(x, grainClass);
                await grain.SetAGlobal(x);
                int a = await grain.GetAGlobal();
                Assert.Equal(x, a); // value of A survive grain call
                Assert.Equal(1, await grain.GetConfirmedVersion());
            };

            // Local
            Func<Task> checker2 = async () =>
            {
                int x = GetRandom();
                var grain = this.fixture.GrainFactory.GetGrain<ILogTestGrain>(x, grainClass);
                Assert.Equal(0, await grain.GetConfirmedVersion());
                await grain.SetALocal(x);
                int a = await grain.GetALocal();
                Assert.Equal(x, a); // value of A survive grain call
            };

            // Local then Global
            Func<Task> checker3 = async () =>
            {
                // Local then Global
                int x = GetRandom();
                var grain = this.fixture.GrainFactory.GetGrain<ILogTestGrain>(x, grainClass);
                await grain.SetALocal(x);
                int a = await grain.GetAGlobal();
                Assert.Equal(x, a);
                Assert.Equal(1, await grain.GetConfirmedVersion());
            };

            // test them in sequence
            await checker1();
            await checker2();
            await checker3();

            // test (phases) instances of each checker, all in parallel
            var tasks = new List<Task>();
            for (int i = 0; i < phases; i++)
            {
                tasks.Add(checker1());
                tasks.Add(checker2());
                tasks.Add(checker3());
            }
            await Task.WhenAll(tasks);
        }
    }
}
