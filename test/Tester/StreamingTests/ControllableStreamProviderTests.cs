using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
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

            private class MySiloBuilderConfigurator : ISiloConfigurator
            {
                public void Configure(ISiloBuilder hostBuilder)
                {
                    hostBuilder
                        .AddPersistentStreams(
                            StreamProviderName,
                            ControllableTestAdapterFactory.Create,
                            b=>
                            {
                                b.ConfigureStreamPubSub(StreamPubSubType.ImplicitOnly);
                                b.UseDynamicClusterConfigDeploymentBalancer();
                            });
                }
            }
        }

        private readonly Fixture _fixture;

        public ControllableStreamProviderTests(Fixture fixture)
        {
            _fixture = fixture;
        }        

        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task ControllableAdapterEchoTest()
        {
            _fixture.Logger.LogInformation("************************ ControllableAdapterEchoTest *********************************");
            const string echoArg = "blarg";
            await ControllableAdapterEchoTestRunner(ControllableTestStreamProviderCommands.AdapterEcho, echoArg);
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task ControllableAdapterFactoryEchoTest()
        {
            _fixture.Logger.LogInformation("************************ ControllableAdapterFactoryEchoTest *********************************");
            const string echoArg = "blarg";
            await ControllableAdapterEchoTestRunner(ControllableTestStreamProviderCommands.AdapterFactoryEcho, echoArg);
        }

        private async Task ControllableAdapterEchoTestRunner(ControllableTestStreamProviderCommands command, object echoArg)
        {
            _fixture.Logger.LogInformation("************************ ControllableAdapterEchoTest *********************************");
            var mgmt = _fixture.GrainFactory.GetGrain<IManagementGrain>(0);

            object[] results = await mgmt.SendControlCommandToProvider(_fixture.StreamProviderTypeName, Fixture.StreamProviderName, (int)command, echoArg);
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
