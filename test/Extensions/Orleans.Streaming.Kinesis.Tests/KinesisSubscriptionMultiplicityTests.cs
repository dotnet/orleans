using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;
using Orleans.TestingHost;
using Orleans.Streaming.Kinesis;
using TestExtensions;
using UnitTests.StreamingTests;

namespace Orleans.Streaming.Kinesis.Tests
{
    /// <summary>
    /// Tests multiple subscription scenarios for Kinesis streams including parallel, linear, and resubscription patterns.
    /// </summary>
    [TestSuite("Functional")]
    [TestArea("Streaming")]
    [TestProvider("Kinesis")]
    [TestCategory("AWS"), TestCategory("Kinesis")]
    public class KinesisSubscriptionMultiplicityTests : TestClusterPerTest
    {
        private const string KinesisStreamProviderName = "KinesisProvider";
        private const string KinesisStreamName = "OrleansKinesisSubscriptionMultiplicityTests";
        private const string StreamNamespace = "KinesisSubscriptionMultiplicityTestsNamespace";
        private SubscriptionMultiplicityTestRunner runner = null!;
        private bool streamCreated;

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
            builder.AddClientBuilderConfigurator<MyClientBuilderConfigurator>();
        }

        protected override void CheckPreconditionsOrThrow()
            => KinesisTestConstants.CheckPreconditionsOrThrow();

        private class MySiloBuilderConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder
                    .AddMemoryGrainStorage("PubSubStore")
                    .AddKinesisStreams(KinesisStreamProviderName, options =>
                    {
                        options.ConnectionString = KinesisTestConstants.ConnectionString;
                        options.StreamName = KinesisStreamName;
                    });
            }
        }

        private class MyClientBuilderConfigurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder
                    .AddKinesisStreams(KinesisStreamProviderName, options =>
                    {
                        options.ConnectionString = KinesisTestConstants.ConnectionString;
                        options.StreamName = KinesisStreamName;
                    });
            }
        }

        public override async ValueTask InitializeAsync()
        {
            EnsurePreconditionsMet();
            await KinesisStreamTestResource.Create(KinesisStreamName);
            streamCreated = true;
            await base.InitializeAsync();
            if (!PreconditionsMet)
            {
                return;
            }
            runner = new SubscriptionMultiplicityTestRunner(KinesisStreamProviderName, this.HostedCluster);
        }

        public override async ValueTask DisposeAsync()
        {
            try
            {
                await base.DisposeAsync();
            }
            finally
            {
                if (streamCreated)
                {
                    await KinesisStreamTestResource.Delete(KinesisStreamName);
                }
            }
        }

        [Fact]
        public async Task KinesisMultipleParallelSubscriptionTest()
        {
            logger.LogInformation("************************ KinesisMultipleParallelSubscriptionTest *********************************");
            await runner.MultipleParallelSubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [Fact]
        public async Task KinesisMultipleLinearSubscriptionTest()
        {
            logger.LogInformation("************************ KinesisMultipleLinearSubscriptionTest *********************************");
            await runner.MultipleLinearSubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [Fact]
        public async Task KinesisMultipleSubscriptionTest_AddRemoveSubscriptions()
        {
            logger.LogInformation("************************ KinesisMultipleSubscriptionTest_AddRemoveSubscriptions *********************************");
            await runner.MultipleSubscriptionTest_AddRemove(Guid.NewGuid(), StreamNamespace);
        }

        [Fact]
        public async Task KinesisResubscriptionTest()
        {
            logger.LogInformation("************************ KinesisResubscriptionTest *********************************");
            await runner.ResubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [Fact]
        public async Task KinesisResubscriptionAfterDeactivationTest()
        {
            logger.LogInformation("************************ KinesisResubscriptionAfterDeactivationTest *********************************");
            await runner.ResubscriptionAfterDeactivationTest(Guid.NewGuid(), StreamNamespace);
        }

        [Fact]
        public async Task KinesisActiveSubscriptionTest()
        {
            logger.LogInformation("************************ KinesisActiveSubscriptionTest *********************************");
            await runner.ActiveSubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [Fact]
        public async Task KinesisTwoIntermitentStreamTest()
        {
            logger.LogInformation("************************ KinesisTwoIntermitentStreamTest *********************************");
            await runner.TwoIntermittentStreamTest(Guid.NewGuid());
        }

        [Fact]
        public async Task KinesisSubscribeFromClientTest()
        {
            logger.LogInformation("************************ KinesisSubscribeFromClientTest *********************************");
            await runner.SubscribeFromClientTest(Guid.NewGuid(), StreamNamespace);
        }
    }
}