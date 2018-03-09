using System;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.WindowsAzure.Storage.Table;
using Orleans.Hosting;
using Orleans.Runtime.Configuration;
using Orleans.Storage;
using Orleans.Streams;
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
                builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
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
                        .ConfigureEventHubStreams(StreamProviderName)
                        .ConfigureEventHub(ob=>ob.Configure(options =>
                        {
                            options.ConnectionString = TestDefaultConfiguration.EventHubConnectionString;
                            options.ConsumerGroup = EHConsumerGroup;
                            options.Path = EHPath;
                        }))
                        .UseEventHubCheckpointer(ob=>ob.Configure(options =>
                        {
                            
                            options.ConnectionString = TestDefaultConfiguration.DataConnectionString;
                            options.TableName = EHCheckpointTable;
                            options.Namespace = CheckpointNamespace;
                            options.PersistInterval = TimeSpan.FromSeconds(10);
                        }));

                    hostBuilder
                        .ConfigureEventHubStreams(StreamProviderName2)
                        .ConfigureEventHub(ob => ob.Configure(options =>
                        {
                            options.ConnectionString = TestDefaultConfiguration.EventHubConnectionString;
                            options.ConsumerGroup = EHConsumerGroup;
                            options.Path = EHPath;
                          
                        }))
                        .UseEventHubCheckpointer(ob => ob.Configure(options => {
                            options.ConnectionString = TestDefaultConfiguration.DataConnectionString;
                            options.TableName = EHCheckpointTable;
                            options.Namespace = CheckpointNamespace2;
                            options.PersistInterval = TimeSpan.FromSeconds(10);
                        }));

                    hostBuilder
                          .AddMemoryGrainStorage("PubSubStore");
                }
            }
        }

        public EHProgrammaticSubscribeTest(ITestOutputHelper output, Fixture fixture)
            : base(fixture)
        {
        }
    }
}
