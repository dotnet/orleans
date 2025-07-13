using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Orleans.TestingHost.Tests.Grains;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.TestingHost.Tests
{
    /// <summary>
    /// Tests for Orleans TestCluster functionality.
    /// 
    /// TestCluster is Orleans' primary testing infrastructure component that provides an in-memory
    /// cluster environment for integration testing. It allows developers to:
    /// - Spin up multiple silos in a single process
    /// - Test grain interactions and cluster behavior
    /// - Verify distributed system scenarios without external dependencies
    /// - Configure and customize the test environment
    /// 
    /// These tests verify TestCluster initialization, configuration, and lifecycle management.
    /// 
    /// Each T0-T9 class tests cluster initialization in isolation to ensure
    /// no static state interference between tests.
    /// </summary>
    public class T0
    {
        [Fact, TestCategory("Functional")]
        public async Task CanInitialize()
        {
            var builder = new TestClusterBuilder(2);
            builder.Options.ServiceId = Guid.NewGuid().ToString();
            builder.ConfigureHostConfiguration(TestDefaultConfiguration.ConfigureHostConfiguration);
            await using var testCluster = builder.Build();

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
            await using var testCluster = builder.Build();

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
            await using var testCluster = builder.Build();

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
            await using var testCluster = builder.Build();

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
            await using var testCluster = builder.Build();

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
            await using var testCluster = builder.Build();

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
            await using var testCluster = builder.Build();

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
            await using var testCluster = builder.Build();

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
            await using var testCluster = builder.Build();

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
            await using var testCluster = builder.Build();

            await testCluster.DeployAsync();
            await testCluster.StopAllSilosAsync();
        }
    }

    /// <summary>
    /// Tests for TestCluster configurator functionality.
    /// Verifies that different types of configurators (Host, Client, ClientHost)
    /// are properly invoked during cluster initialization.
    /// </summary>
    public class T10
    {
        private static bool _hostWasInvoked;
        private static bool _clientWasInvoked;

        [Fact, TestCategory("Functional")]
        public async Task ClientBuilder_HostConfigurator() => await Test<HostConfigurator>(true, false);

        [Fact, TestCategory("Functional")]
        public async Task ClientBuilder_ClientHostConfigurator() => await Test<ClientHostConfigurator>(true, true);

        [Fact, TestCategory("Functional")]
        public async Task ClientBuilder_ClientConfigurator() => await Test<ClientConfigurator>(false, true);

        private static async Task Test<TConfigurator>(bool hostInvoked, bool clientInvoked)
            where TConfigurator : new()
        {
            _hostWasInvoked = false;
            _clientWasInvoked = false;

            var builder = new TestClusterBuilder(2);
            builder.Options.ServiceId = Guid.NewGuid().ToString();
            builder.AddClientBuilderConfigurator<TConfigurator>();
            using var testCluster = builder.Build();

            await testCluster.DeployAsync();
            await testCluster.StopAllSilosAsync();

            _hostWasInvoked.Should().Be(hostInvoked);
            _clientWasInvoked.Should().Be(clientInvoked);
        }

        /// <summary>
        /// Test configurator that only configures the host.
        /// Used to verify host-only configuration scenarios.
        /// </summary>
        private class HostConfigurator : IHostConfigurator
        {
            public void Configure(IHostBuilder hostBuilder) => _hostWasInvoked = true;
        }

        /// <summary>
        /// Test configurator that configures both host and client.
        /// Demonstrates how a single configurator can handle both aspects
        /// of the test cluster setup.
        /// </summary>
        private class ClientHostConfigurator : IHostConfigurator, IClientBuilderConfigurator
        {

            public void Configure(IHostBuilder hostBuilder) => _hostWasInvoked = true;
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder) => _clientWasInvoked = true;
        }

        /// <summary>
        /// Test configurator that only configures the client.
        /// Used to verify client-only configuration scenarios.
        /// </summary>
        private class ClientConfigurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder) => _clientWasInvoked = true;
        }
    }

    /// <summary>
    /// Main test class for TestCluster functionality.
    /// Implements IAsyncLifetime to properly manage cluster lifecycle
    /// and ensure cleanup after tests complete.
    /// </summary>
    public class TestClusterTests : IAsyncLifetime
    {
        private TestCluster _testCluster;

        /// <summary>
        /// Tests basic TestCluster initialization and grain communication.
        /// Verifies that:
        /// - A cluster with multiple silos can be created
        /// - Grains can be activated and called
        /// - State can be set and retrieved from grains
        /// </summary>
        [Fact, TestCategory("Functional")]
        public async Task CanInitialize()
        {
            var builder = new TestClusterBuilder(2);
            builder.Options.ServiceId = Guid.NewGuid().ToString();
            builder.ConfigureHostConfiguration(TestDefaultConfiguration.ConfigureHostConfiguration);
            _testCluster = builder.Build();

            await _testCluster.DeployAsync();

            var grain = _testCluster.Client.GetGrain<ISimpleGrain>(1);

            await grain.SetA(2);
            Assert.Equal(2, await grain.GetA());
        }

        /// <summary>
        /// Tests TestCluster initialization using legacy configuration approach.
        /// Demonstrates how to use ISiloConfigurator for backwards compatibility
        /// with older test configuration patterns.
        /// </summary>
        [Fact, TestCategory("Functional")]
        public async Task CanInitializeWithLegacyConfiguration()
        {
            var builder = new TestClusterBuilder(2);
            builder.ConfigureHostConfiguration(TestDefaultConfiguration.ConfigureHostConfiguration);
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
            _testCluster = builder.Build();

            await _testCluster.DeployAsync();

            var grain = _testCluster.Client.GetGrain<ISimpleGrain>(1);

            await grain.SetA(2);
            Assert.Equal(2, await grain.GetA());
        }

        /// <summary>
        /// Example silo configurator that adds memory grain storage.
        /// Demonstrates how to customize silo configuration in tests
        /// using the ISiloConfigurator interface.
        /// </summary>
        public class SiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder.AddMemoryGrainStorageAsDefault();
            }
        }
        
        public Task InitializeAsync()
        {
            return Task.CompletedTask;
        }

        public async Task DisposeAsync()
        {
            if (_testCluster is not null)
            {
                await _testCluster.DisposeAsync();
            }
        }
    }
}
