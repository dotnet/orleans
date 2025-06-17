using Microsoft.Extensions.Configuration;
using Orleans.Providers;
using Orleans.TestingHost;

namespace Tester.StreamingTests
{
    /// <summary>
    /// Tests memory stream resume functionality with configurable stream inactivity periods and cache eviction settings.
    /// </summary>
    [TestCategory("SlowBVT"), TestCategory("Streaming"), TestCategory("StreamingResume")]
    public class MemoryStreamResumeTests : StreamingResumeTests
    {
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
            builder.AddClientBuilderConfigurator<MyClientBuilderConfigurator>();
        }

        #region Configuration stuff
        private class MySiloBuilderConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder
                    .AddMemoryGrainStorageAsDefault()
                    .AddMemoryGrainStorage("PubSubStore")
                    .AddMemoryStreams<DefaultMemoryMessageBodySerializer>(StreamProviderName, b =>
                    {
                        b.ConfigurePullingAgent(ob => ob.Configure(options =>
                        {
                            options.StreamInactivityPeriod = StreamInactivityPeriod;
                        }));
                        b.ConfigureCacheEviction(ob => ob.Configure(options =>
                        {
                            options.MetadataMinTimeInCache = MetadataMinTimeInCache;
                            options.DataMaxAgeInCache = DataMaxAgeInCache;
                            options.DataMinTimeInCache = DataMinTimeInCache;
                        }));
                    });
            }
        }

        private class MyClientBuilderConfigurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder
                    .AddMemoryStreams<DefaultMemoryMessageBodySerializer>(StreamProviderName);
            }
        }

        #endregion
    }
}
