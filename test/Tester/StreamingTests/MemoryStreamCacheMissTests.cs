using Microsoft.Extensions.Configuration;
using Orleans.Providers;
using Orleans.TestingHost;
using Xunit.Abstractions;

namespace Tester.StreamingTests
{
    [TestCategory("Functional"), TestCategory("Streaming"), TestCategory("StreamingCacheMiss")]
    public class MemoryStreamCacheMissTests : StreamingCacheMissTests
    {
        public MemoryStreamCacheMissTests(ITestOutputHelper output)
            : base(output)
        {
        }

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
                        b.ConfigureCacheEviction(ob => ob.Configure(options =>
                        {
                            options.DataMaxAgeInCache = TimeSpan.FromSeconds(5);
                            options.DataMinTimeInCache = TimeSpan.FromSeconds(0);
                        }));
                    })
                    .AddStreamFilter<CustomStreamFilter>(StreamProviderName);
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
