using Microsoft.Extensions.Options;
using Xunit;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using TestExtensions;
using Tester;

using Orleans.Configuration;

namespace Tests.GeoClusterTests
{
    [TestCategory("GeoCluster"), TestCategory("Functional")]
    public class BasicLogTestGrainTests : IClassFixture<BasicLogTestGrainTests.Fixture>
    {
        private readonly Fixture fixture;
        private readonly Random random;

        public class Fixture : BaseAzureTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.Options.ConnectionTransport = ConnectionTransportType.TcpSocket;
                builder.Options.InitialSilosCount = 1;
                builder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();
            }

            private class SiloBuilderConfigurator : ISiloConfigurator
            {
                public void Configure(ISiloBuilder hostBuilder)
                {
                    hostBuilder
                        .AddStateStorageBasedLogConsistencyProvider()
                        .AddLogStorageBasedLogConsistencyProvider()
                        .AddCustomStorageBasedLogConsistencyProvider("CustomStorage")
                        .AddCustomStorageBasedLogConsistencyProvider("CustomStoragePrimaryCluster", "A")
                        .AddAzureTableGrainStorageAsDefault(builder => builder.Configure<IOptions<ClusterOptions>>((options, silo) =>
                        {
                            options.ConfigureTableServiceClient(TestDefaultConfiguration.DataConnectionString);
                        }))
                        .AddAzureTableGrainStorage("AzureStore", builder => builder.Configure<IOptions<ClusterOptions>>((options, silo) =>
                        {
                            options.ConfigureTableServiceClient(TestDefaultConfiguration.DataConnectionString);
                        }))
                        .AddMemoryGrainStorage("MemoryStore"); 
                }
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
            async Task checker1()
            {
                int x = GetRandom();
                var grain = this.fixture.GrainFactory.GetGrain<ILogTestGrain>(x, grainClass);
                await grain.SetAGlobal(x);
                int a = await grain.GetAGlobal();
                Assert.Equal(x, a); // value of A survive grain call
                Assert.Equal(1, await grain.GetConfirmedVersion());
            }

            // Local
            async Task checker2()
            {
                int x = GetRandom();
                var grain = this.fixture.GrainFactory.GetGrain<ILogTestGrain>(x, grainClass);
                Assert.Equal(0, await grain.GetConfirmedVersion());
                await grain.SetALocal(x);
                int a = await grain.GetALocal();
                Assert.Equal(x, a); // value of A survive grain call
            }

            // Local then Global
            async Task checker3()
            {
                // Local then Global
                int x = GetRandom();
                var grain = this.fixture.GrainFactory.GetGrain<ILogTestGrain>(x, grainClass);
                await grain.SetALocal(x);
                int a = await grain.GetAGlobal();
                Assert.Equal(x, a);
                Assert.Equal(1, await grain.GetConfirmedVersion());
            }

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
