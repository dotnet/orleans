using System;
using System.Linq;
using System.Threading.Tasks;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.TestingHost;
using Tester.TestStreamProviders.Controllable;
using TestExtensions;
using Xunit;

namespace UnitTests.StreamingTests
{
    public class ControllableStreamProviderTests : OrleansTestingBase, IClassFixture<ControllableStreamProviderTests.Fixture>
    {
        public class Fixture : BaseTestClusterFixture
        {
            public const string StreamProviderName = "ControllableTestStreamProvider";
            public readonly string StreamProviderTypeName = typeof(PersistentStreamProvider).FullName;

            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
            }

            private class MySiloBuilderConfigurator : ISiloBuilderConfigurator
            {
                public void Configure(ISiloHostBuilder hostBuilder)
                {
                    hostBuilder
                        .AddPersistentStreams(StreamProviderName,
                            ControllableTestAdapterFactory.Create)
                         .ConfigureStreamPubSub(StreamPubSubType.ImplicitOnly)
                         .UseDynamicClusterConfigDeploymentBalancer();
                }
            }
        }

        private readonly Fixture fixture;

        public ControllableStreamProviderTests(Fixture fixture)
        {
            this.fixture = fixture;
        }        

        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task ControllableAdapterEchoTest()
        {
            this.fixture.Logger.Info("************************ ControllableAdapterEchoTest *********************************");
            const string echoArg = "blarg";
            await this.ControllableAdapterEchoTestRunner(ControllableTestStreamProviderCommands.AdapterEcho, echoArg);
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task ControllableAdapterFactoryEchoTest()
        {
            this.fixture.Logger.Info("************************ ControllableAdapterFactoryEchoTest *********************************");
            const string echoArg = "blarg";
            await this.ControllableAdapterEchoTestRunner(ControllableTestStreamProviderCommands.AdapterFactoryEcho, echoArg);
        }

        private async Task ControllableAdapterEchoTestRunner(ControllableTestStreamProviderCommands command, object echoArg)
        {
            this.fixture.Logger.Info("************************ ControllableAdapterEchoTest *********************************");
            var mgmt = this.fixture.GrainFactory.GetGrain<IManagementGrain>(0);

            object[] results = await mgmt.SendControlCommandToProvider(this.fixture.StreamProviderTypeName, Fixture.StreamProviderName, (int)command, echoArg);
            Assert.Equal(2, results.Length);
            Tuple<ControllableTestStreamProviderCommands, object>[] echos = results.Cast<Tuple<ControllableTestStreamProviderCommands, object>>().ToArray();
            foreach (var echo in echos)
            {
                Assert.Equal(command, echo.Item1);
                Assert.Equal(echoArg, echo.Item2);
            }
        }
    }
}
