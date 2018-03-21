using System;
using Xunit;
using Xunit.Abstractions;
using Orleans.Hosting;
using Orleans.TestingHost;
using Orleans.Streams;
using Orleans.LeaseProviders;
using Tester.StreamingTests;
using TestExtensions;

namespace ServiceBus.Tests.StreamingTests
{
    [TestCategory("EventHub")]
    public class EHPersistentStreamQueueRebalancingTestRunner : PersistentStreamQueueRebalancingTestRunner, IClassFixture<EHPersistentStreamQueueRebalancingTestRunner.Fixture>
    {
        private const string StreamProviderName = "EventHubStreamProvider";
        private const string EHPath = "ehorleanstest";
        private const string EHConsumerGroup = "orleansnightly";

        public EHPersistentStreamQueueRebalancingTestRunner(Fixture fixture, ITestOutputHelper output)
            : base(StreamProviderName, fixture.GrainFactory, output)
        {
        }

        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
            }

            private class MySiloBuilderConfigurator : ISiloBuilderConfigurator
            {
                public void Configure(ISiloHostBuilder hostBuilder)
                {
                    EHPersistentStreamQueueRebalancingTestRunner.ConfigureSiloHostBuilder(hostBuilder)
                        .AddEventHubStreams(StreamProviderName)
                            .ConfigureEventHub(ob => ob.Configure(options =>
                                {
                                    options.ConnectionString = TestDefaultConfiguration.EventHubConnectionString;
                                    options.ConsumerGroup = EHConsumerGroup;
                                    options.Path = EHPath;

                                }))
                            .UseEventHubCheckpointer(ob => ob.Configure(options =>
                                {
                                    options.ConnectionString = TestDefaultConfiguration.DataConnectionString;
                                    options.PersistInterval = TimeSpan.FromSeconds(1);
                                }))
                            .UseClusterConfigDeploymentLeaseBasedBalancer(ob => ob.Configure(options =>
                            {
                                options.LeaseProviderType = typeof(ILeaseProvider);
                                options.LeaseLength = TimeSpan.FromSeconds(2);
                                options.SiloMaturityPeriod = TimeSpan.FromSeconds(6);
                            }));
                }
            }
        }
    }
}
