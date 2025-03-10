using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.Streaming;
using UnitTests.StreamingTests;
using Xunit;

namespace Tester.AzureUtils.Streaming
{
    [TestCategory("Streaming"), TestCategory("AzureStorage"), TestCategory("AzureQueue")]
    public class AQStreamingTests(AQStreamingTests.Fixture fixture) : IClassFixture<AQStreamingTests.Fixture>
    {
        public const string AzureQueueStreamProviderName = StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME;
        public const string SmsStreamProviderName = StreamTestsConstants.SMS_STREAM_PROVIDER_NAME;
        private const int queueCount = 8;

        public sealed class Fixture : IAsyncLifetime
        {
            private readonly ExceptionDispatchInfo _preconditionsException;

            public Fixture()
            {
                var builder = new InProcessTestClusterBuilder();
                try
                {
                    TestUtils.CheckForAzureStorage();
                }
                catch (Exception ex)
                {
                    _preconditionsException = ExceptionDispatchInfo.Capture(ex);
                    return;
                }

                builder.ConfigureHost(cb =>
                {
                    Dictionary<string, string> queueConfig = [];
                    void ConfigureStreaming(string option, string value)
                    {
                        var prefix = $"Orleans:Streaming:{AzureQueueStreamProviderName}:";
                        queueConfig[$"{prefix}{option}"] = value;
                    }

                    ConfigureStreaming("ProviderType", "AzureQueueStorage");
                    if (TestDefaultConfiguration.UseAadAuthentication)
                    {
                        cb.AddKeyedAzureQueueClient(AzureQueueStreamProviderName, settings =>
                        {
                            settings.ServiceUri = TestDefaultConfiguration.DataQueueUri;
                            settings.Credential = TestDefaultConfiguration.TokenCredential;
                        });
                        ConfigureStreaming("ServiceKey", AzureQueueStreamProviderName);
                    }
                    else
                    {
                        ConfigureStreaming("ConnectionString", TestDefaultConfiguration.DataConnectionString);
                    }

                    var names = AzureQueueUtilities.GenerateQueueNames(builder.Options.ClusterId, queueCount);
                    for (var i = 0; i < names.Count; i++)
                    {
                        ConfigureStreaming($"QueueNames:{i}", names[i]);
                    }

                    cb.Configuration.AddInMemoryCollection(queueConfig);
                });
                builder.ConfigureSilo((options, siloBuilder) =>
                {
                    siloBuilder
                        .AddAzureTableGrainStorage("AzureStore", builder => builder.Configure(options =>
                        {
                            options.ConfigureTestDefaults();
                            options.DeleteStateOnClear = true;
                        }))
                        .AddAzureTableGrainStorage("PubSubStore", builder => builder.Configure(options =>
                            {
                                options.ConfigureTestDefaults();
                                options.DeleteStateOnClear = true;
                            }))
                        .AddMemoryGrainStorage("MemoryStore");
                });
                builder.ConfigureClient(clientBuilder =>
                {
                    clientBuilder
                        .AddAzureQueueStreams(AzureQueueStreamProviderName, b=>
                        b.ConfigureAzureQueue(ob=>ob.Configure<IOptions<ClusterOptions>>(
                            (options, dep) =>
                            {
                                options.ConfigureTestDefaults();
                                options.QueueNames = AzureQueueUtilities.GenerateQueueNames(dep.Value.ClusterId, queueCount);
                            })));
                });
                Cluster = builder.Build();
            }

            public InProcessTestCluster Cluster { get; }
            public SingleStreamTestRunner Runner { get; private set; }

            public async Task DisposeAsync()
            {
                if (Cluster is null)
                {
                    return;
                }

                try
                {
                    TestUtils.CheckForAzureStorage();
                    await AzureQueueStreamProviderUtils.ClearAllUsedAzureQueues(NullLoggerFactory.Instance,
                        AzureQueueUtilities.GenerateQueueNames(Cluster.Options.ClusterId, queueCount),
                        new AzureQueueOptions().ConfigureTestDefaults());
                }
                catch (SkipException)
                {
                    // ignore
                }

                await Cluster.DisposeAsync();
            }

            public async Task InitializeAsync()
            {
                _preconditionsException?.Throw();
                await Cluster.DeployAsync();
                Runner = new SingleStreamTestRunner(Cluster.InternalClient, SingleStreamTestRunner.AQ_STREAM_PROVIDER_NAME);
            }
        }

        ////------------------------ One to One ----------------------//

        [SkippableFact, TestCategory("Functional")]
        public async Task AQ_01_OneProducerGrainOneConsumerGrain()
        {
            await fixture.Runner.StreamTest_01_OneProducerGrainOneConsumerGrain();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AQ_02_OneProducerGrainOneConsumerClient()
        {
            await fixture.Runner.StreamTest_02_OneProducerGrainOneConsumerClient();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AQ_03_OneProducerClientOneConsumerGrain()
        {
            await fixture.Runner.StreamTest_03_OneProducerClientOneConsumerGrain();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AQ_04_OneProducerClientOneConsumerClient()
        {
            await fixture.Runner.StreamTest_04_OneProducerClientOneConsumerClient();
        }

        //------------------------ MANY to Many different grains ----------------------//

        [SkippableFact, TestCategory("Functional")]
        public async Task AQ_05_ManyDifferent_ManyProducerGrainsManyConsumerGrains()
        {
            await fixture.Runner.StreamTest_05_ManyDifferent_ManyProducerGrainsManyConsumerGrains();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AQ_06_ManyDifferent_ManyProducerGrainManyConsumerClients()
        {
            await fixture.Runner.StreamTest_06_ManyDifferent_ManyProducerGrainManyConsumerClients();
        }

        [SkippableFact(Skip = "https://github.com/dotnet/orleans/issues/5648"), TestCategory("Functional")]
        public async Task AQ_07_ManyDifferent_ManyProducerClientsManyConsumerGrains()
        {
            await fixture.Runner.StreamTest_07_ManyDifferent_ManyProducerClientsManyConsumerGrains();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AQ_08_ManyDifferent_ManyProducerClientsManyConsumerClients()
        {
            await fixture.Runner.StreamTest_08_ManyDifferent_ManyProducerClientsManyConsumerClients();
        }

        //------------------------ MANY to Many Same grains ----------------------//
        [SkippableFact, TestCategory("Functional")]
        public async Task AQ_09_ManySame_ManyProducerGrainsManyConsumerGrains()
        {
            await fixture.Runner.StreamTest_09_ManySame_ManyProducerGrainsManyConsumerGrains();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AQ_10_ManySame_ManyConsumerGrainsManyProducerGrains()
        {
            await fixture.Runner.StreamTest_10_ManySame_ManyConsumerGrainsManyProducerGrains();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AQ_11_ManySame_ManyProducerGrainsManyConsumerClients()
        {
            await fixture.Runner.StreamTest_11_ManySame_ManyProducerGrainsManyConsumerClients();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AQ_12_ManySame_ManyProducerClientsManyConsumerGrains()
        {
            await fixture.Runner.StreamTest_12_ManySame_ManyProducerClientsManyConsumerGrains();
        }

        //------------------------ MANY to Many producer consumer same grain ----------------------//

        [SkippableFact, TestCategory("Functional")]
        public async Task AQ_13_SameGrain_ConsumerFirstProducerLater()
        {
            await fixture.Runner.StreamTest_13_SameGrain_ConsumerFirstProducerLater(false);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AQ_14_SameGrain_ProducerFirstConsumerLater()
        {
            await fixture.Runner.StreamTest_14_SameGrain_ProducerFirstConsumerLater(false);
        }

        //----------------------------------------------//

        [SkippableFact, TestCategory("Functional")]
        public async Task AQ_15_ConsumeAtProducersRequest()
        {
            await fixture.Runner.StreamTest_15_ConsumeAtProducersRequest();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AQ_16_MultipleStreams_ManyDifferent_ManyProducerGrainsManyConsumerGrains()
        {
            var multiRunner = new MultipleStreamsTestRunner(fixture.Cluster.InternalClient, SingleStreamTestRunner.AQ_STREAM_PROVIDER_NAME, 16, false);
            await multiRunner.StreamTest_MultipleStreams_ManyDifferent_ManyProducerGrainsManyConsumerGrains();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AQ_17_MultipleStreams_1J_ManyProducerGrainsManyConsumerGrains()
        {
            var multiRunner = new MultipleStreamsTestRunner(fixture.Cluster.InternalClient, SingleStreamTestRunner.AQ_STREAM_PROVIDER_NAME, 17, false);
            await multiRunner.StreamTest_MultipleStreams_ManyDifferent_ManyProducerGrainsManyConsumerGrains(
                fixture.Cluster.StartAdditionalSilo);
        }

        //[SkippableFact, TestCategory("BVT")]
        /*public async Task AQ_18_MultipleStreams_1J_1F_ManyProducerGrainsManyConsumerGrains()
        {
            var multiRunner = new MultipleStreamsTestRunner(this.InternalClient, SingleStreamTestRunner.AQ_STREAM_PROVIDER_NAME, 18, false);
            await multiRunner.StreamTest_MultipleStreams_ManyDifferent_ManyProducerGrainsManyConsumerGrains(
                this.HostedCluster.StartAdditionalSilo,
                this.HostedCluster.StopSilo);
        }*/

        [SkippableFact]
        public async Task AQ_19_ConsumerImplicitlySubscribedToProducerClient()
        {
            // todo: currently, the Azure queue queue adaptor doesn't support namespaces, so this test will fail.
            await fixture.Runner.StreamTest_19_ConsumerImplicitlySubscribedToProducerClient();
        }

        [SkippableFact]
        public async Task AQ_20_ConsumerImplicitlySubscribedToProducerGrain()
        {
            // todo: currently, the Azure queue queue adaptor doesn't support namespaces, so this test will fail.
            await fixture.Runner.StreamTest_20_ConsumerImplicitlySubscribedToProducerGrain();
        }

        [SkippableFact(Skip = "Ignored"), TestCategory("Failures")]
        public async Task AQ_21_GenericConsumerImplicitlySubscribedToProducerGrain()
        {
            // todo: currently, the Azure queue queue adaptor doesn't support namespaces, so this test will fail.
            await fixture.Runner.StreamTest_21_GenericConsumerImplicitlySubscribedToProducerGrain();
        }
    }
}
