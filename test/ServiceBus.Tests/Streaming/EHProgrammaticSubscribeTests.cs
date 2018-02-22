using System;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.WindowsAzure.Storage.Table;
using Orleans.Hosting;
using Orleans.Runtime.Configuration;
using Orleans.Storage;
using Orleans.Streaming.EventHubs;
using Orleans.TestingHost;
using Tester.StreamingTests;
using Tester.TestStreamProviders;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace ServiceBus.Tests.Streaming
{
    [TestCategory("EventHub"), TestCategory("Streaming")]
    public class EHProgrammaticSubscribeTest : ProgrammaticSubcribeTestsRunner, IClassFixture<EHProgrammaticSubscribeTest.Fixture>
    {
        private const string EHPath = "ehorleanstest";
        private const string EHConsumerGroup = "orleansnightly";
        private const string EHCheckpointTable = "ehcheckpoint";
        private static readonly string CheckpointNamespace = Guid.NewGuid().ToString();
        private static readonly string CheckpointNamespace2 = Guid.NewGuid().ToString();

        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.ConfigureLegacyConfiguration(legacy =>
                {
                    AdjustClusterConfiguration(legacy.ClusterConfiguration);
                });
                builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
            }

            private static void AdjustClusterConfiguration(ClusterConfiguration config)
            {
                config.Globals.RegisterStorageProvider<MemoryStorage>("PubSubStore");
            }

            public override void Dispose()
            {
                base.Dispose();
                var dataManager = new AzureTableDataManager<TableEntity>(EHCheckpointTable, TestDefaultConfiguration.DataConnectionString, NullLoggerFactory.Instance);
                dataManager.InitTableAsync().Wait();
                dataManager.ClearTableAsync().Wait();
                TestAzureTableStorageStreamFailureHandler.DeleteAll().Wait();
            }

            private class MySiloBuilderConfigurator : ISiloBuilderConfigurator
            {
                public void Configure(ISiloHostBuilder hostBuilder)
                {
                    hostBuilder
                        .AddEventHubStreams(StreamProviderName,
                        options =>
                        {
                            options.ConnectionString = TestDefaultConfiguration.EventHubConnectionString;
                            options.ConsumerGroup = EHConsumerGroup;
                            options.Path = EHPath;
                            options.CheckpointConnectionString = TestDefaultConfiguration.DataConnectionString;
                            options.CheckpointTableName = EHCheckpointTable;
                            options.CheckpointNamespace = CheckpointNamespace;
                            options.CheckpointPersistInterval = TimeSpan.FromSeconds(10);
                        })
                        .AddEventHubStreams(StreamProviderName2, options =>
                        {
                            options.ConnectionString = TestDefaultConfiguration.EventHubConnectionString;
                            options.ConsumerGroup = EHConsumerGroup;
                            options.Path = EHPath;
                            options.CheckpointConnectionString = TestDefaultConfiguration.DataConnectionString;
                            options.CheckpointTableName = EHCheckpointTable;
                            options.CheckpointNamespace = CheckpointNamespace2;
                            options.CheckpointPersistInterval = TimeSpan.FromSeconds(10);
                        });
                }
            }
        }

        public EHProgrammaticSubscribeTest(ITestOutputHelper output, Fixture fixture)
            : base(fixture)
        {
        }
    }
}
