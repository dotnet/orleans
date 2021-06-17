using System;
using System.Threading.Tasks;
using Orleans.Hosting;
using Orleans.TestingHost.Tests.Grains;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.TestingHost.Tests
{
    public class T0
    {
        [Fact, TestCategory("Functional")]
        public async Task CanInitialize()
        {
            var builder = new TestClusterBuilder(2);
            builder.Options.ServiceId = Guid.NewGuid().ToString();
            builder.ConfigureHostConfiguration(TestDefaultConfiguration.ConfigureHostConfiguration);
            using var testCluster = builder.Build();

            await testCluster.DeployAsync();
        }
    }

    public class T1
    {
        [Fact, TestCategory("Functional")]
        public async Task CanInitialize()
        {
            var builder = new TestClusterBuilder(2);
            builder.Options.ServiceId = Guid.NewGuid().ToString();
            builder.ConfigureHostConfiguration(TestDefaultConfiguration.ConfigureHostConfiguration);
            using var testCluster = builder.Build();

            await testCluster.DeployAsync();
        }
    }

    public class T2
    {
        [Fact, TestCategory("Functional")]
        public async Task CanInitialize()
        {
            var builder = new TestClusterBuilder(2);
            builder.Options.ServiceId = Guid.NewGuid().ToString();
            builder.ConfigureHostConfiguration(TestDefaultConfiguration.ConfigureHostConfiguration);
            using var testCluster = builder.Build();

            await testCluster.DeployAsync();
        }
    }

    public class T3
    {
        [Fact, TestCategory("Functional")]
        public async Task CanInitialize()
        {
            var builder = new TestClusterBuilder(2);
            builder.Options.ServiceId = Guid.NewGuid().ToString();
            builder.ConfigureHostConfiguration(TestDefaultConfiguration.ConfigureHostConfiguration);
            using var testCluster = builder.Build();

            await testCluster.DeployAsync();
            await testCluster.StopAllSilosAsync();
        }
    }

    public class T4
    {
        [Fact, TestCategory("Functional")]
        public async Task CanInitialize()
        {
            var builder = new TestClusterBuilder(2);
            builder.Options.ServiceId = Guid.NewGuid().ToString();
            builder.ConfigureHostConfiguration(TestDefaultConfiguration.ConfigureHostConfiguration);
            using var testCluster = builder.Build();

            await testCluster.DeployAsync();
            await testCluster.StopAllSilosAsync();
        }
    }

    public class T5
    {
        [Fact, TestCategory("Functional")]
        public async Task CanInitialize()
        {
            var builder = new TestClusterBuilder(2);
            builder.Options.ServiceId = Guid.NewGuid().ToString();
            builder.ConfigureHostConfiguration(TestDefaultConfiguration.ConfigureHostConfiguration);
            using var testCluster = builder.Build();

            await testCluster.DeployAsync();
            await testCluster.StopAllSilosAsync();
        }
    }

    public class T6
    {
        [Fact, TestCategory("Functional")]
        public async Task CanInitialize()
        {
            var builder = new TestClusterBuilder(2);
            builder.Options.ServiceId = Guid.NewGuid().ToString();
            builder.ConfigureHostConfiguration(TestDefaultConfiguration.ConfigureHostConfiguration);
            using var testCluster = builder.Build();

            await testCluster.DeployAsync();
            await testCluster.StopAllSilosAsync();
        }
    }

    public class T7
    {
        [Fact, TestCategory("Functional")]
        public async Task CanInitialize()
        {
            var builder = new TestClusterBuilder(2);
            builder.Options.ServiceId = Guid.NewGuid().ToString();
            builder.ConfigureHostConfiguration(TestDefaultConfiguration.ConfigureHostConfiguration);
            using var testCluster = builder.Build();

            await testCluster.DeployAsync();
            await testCluster.StopAllSilosAsync();
        }
    }

    public class T8
    {
        [Fact, TestCategory("Functional")]
        public async Task CanInitialize()
        {
            var builder = new TestClusterBuilder(2);
            builder.Options.ServiceId = Guid.NewGuid().ToString();
            builder.ConfigureHostConfiguration(TestDefaultConfiguration.ConfigureHostConfiguration);
            using var testCluster = builder.Build();

            await testCluster.DeployAsync();
            await testCluster.StopAllSilosAsync();
        }
    }

    public class T9
    {
        [Fact, TestCategory("Functional")]
        public async Task CanInitialize()
        {
            var builder = new TestClusterBuilder(2);
            builder.Options.ServiceId = Guid.NewGuid().ToString();
            builder.ConfigureHostConfiguration(TestDefaultConfiguration.ConfigureHostConfiguration);
            using var testCluster = builder.Build();

            await testCluster.DeployAsync();
            await testCluster.StopAllSilosAsync();
        }
    }

    public class TestClusterTests : IDisposable, IAsyncLifetime
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

        public class SiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder.AddMemoryGrainStorageAsDefault();
            }
        }
        public void Dispose()
        {
            this.testCluster?.StopAllSilos();
        }

        public Task InitializeAsync()
        {
            return Task.CompletedTask;
        }

        public async Task DisposeAsync()
        {
            await this.testCluster.StopAllSilosAsync();
        }
    }
}
