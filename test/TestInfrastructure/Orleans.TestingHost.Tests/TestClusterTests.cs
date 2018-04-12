using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Orleans.Hosting;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost.Tests.Grains;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.TestingHost.Tests
{
    public class TestClusterTests : IDisposable
    {
        private readonly ITestOutputHelper output;
        private TestCluster testCluster;

        public TestClusterTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Fact, TestCategory("Functional")]
        public async Task CanInitialize()
        {
            var builder = new TestClusterBuilder(2);
            builder.Options.ServiceId = Guid.NewGuid().ToString();
            builder.ConfigureHostConfiguration(TestDefaultConfiguration.ConfigureHostConfiguration);
            this.testCluster = builder.Build();

            await this.testCluster.DeployAsync();

            var grain = this.testCluster.Client.GetGrain<ISimpleGrain>(1);

            await grain.SetA(2);
            Assert.Equal(2, await grain.GetA());
        }

        [Fact, TestCategory("Functional")]
        public async Task CanInitializeWithLegacyConfiguration()
        {
            var builder = new TestClusterBuilder(2);
            builder.ConfigureHostConfiguration(TestDefaultConfiguration.ConfigureHostConfiguration);
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
            this.testCluster = builder.Build();

            await this.testCluster.DeployAsync();

            var grain = this.testCluster.Client.GetGrain<ISimpleGrain>(1);

            await grain.SetA(2);
            Assert.Equal(2, await grain.GetA());
        }

        public class SiloConfigurator : ISiloBuilderConfigurator
        {
            public void Configure(ISiloHostBuilder hostBuilder)
            {
                hostBuilder.AddMemoryGrainStorageAsDefault();
            }
        }
        public void Dispose()
        {
            this.testCluster?.StopAllSilos();
        }
     }
}
