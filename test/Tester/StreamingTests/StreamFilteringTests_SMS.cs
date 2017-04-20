using System;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using System.Threading.Tasks;
using Xunit;
using TestExtensions;
using UnitTests.StreamingTests;

namespace Tester.StreamingTests
{
    public class StreamFilteringTests_SMS : StreamFilteringTestsBase, IClassFixture<StreamFilteringTests_SMS.Fixture>
    {
        public class Fixture : BaseTestClusterFixture
        {
            public const string StreamProvider = StreamTestsConstants.SMS_STREAM_PROVIDER_NAME;
            protected override TestCluster CreateTestCluster()
            {
                var options = new TestClusterOptions(2);

                options.ClusterConfiguration.AddMemoryStorageProvider("MemoryStore");
                options.ClusterConfiguration.AddMemoryStorageProvider("PubSubStore");

                options.ClusterConfiguration.AddSimpleMessageStreamProvider(StreamProvider, false);
                options.ClientConfiguration.AddSimpleMessageStreamProvider(StreamProvider, false);
                return new TestCluster(options);
            }
        }

        public StreamFilteringTests_SMS(Fixture fixture) : base(fixture)
        {
            streamProviderName = Fixture.StreamProvider;
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Filters")]
        public async Task SMS_Filter_Basic()
        {
            await Test_Filter_EvenOdd(true);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Filters")]
        public async Task SMS_Filter_EvenOdd()
        {
            await Test_Filter_EvenOdd();
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Filters")]
        public async Task SMS_Filter_BadFunc()
        {
            await Assert.ThrowsAsync(typeof(ArgumentException), () =>
                 Test_Filter_BadFunc());
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Filters")]
        public async Task SMS_Filter_TwoObsv_Different()
        {
            await Test_Filter_TwoObsv_Different();
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Filters")]
        public async Task SMS_Filter_TwoObsv_Same()
        {
            await Test_Filter_TwoObsv_Same();
        }
    }
}
