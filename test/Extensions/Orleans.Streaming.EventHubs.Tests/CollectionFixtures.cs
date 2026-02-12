using Orleans.Runtime;
using Tester;
using TestExtensions;
using Xunit;

namespace ServiceBus.Tests
{
    // Assembly collections must be defined once in each assembly
    
    /// <summary>
    /// Defines a test collection for tests that require a default Orleans cluster setup.
    /// Tests in this collection share a single cluster instance for improved performance.
    /// </summary>
    [CollectionDefinition("DefaultCluster")]
    public class DefaultClusterTestCollection : ICollectionFixture<DefaultClusterFixture> { }
    

    /// <summary>
    /// Defines a test collection for tests that require shared test environment configuration.
    /// Provides Azure Service Bus and Event Hub specific test environment setup.
    /// </summary>
    [CollectionDefinition(TestEnvironmentFixture.DefaultCollection)]
    public class TestEnvironmentFixtureCollection : ICollectionFixture<TestEnvironmentFixture> { }

    /// <summary>
    /// Base test cluster fixture for Event Hub integration tests.
    /// Ensures Event Hub connectivity and waits for stream queue initialization.
    /// </summary>
    public abstract class BaseEventHubTestClusterFixture : BaseTestClusterFixture
    {
        protected override void CheckPreconditionsOrThrow()
        {
            base.CheckPreconditionsOrThrow();
            TestUtils.CheckForEventHub();
        }

        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();
            var collector = new Microsoft.Extensions.Diagnostics.Metrics.Testing.MetricCollector<long>(Instruments.Meter, "orleans-streams-queue-read-duration");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            // Wait for 10 queue read
            await collector.WaitForMeasurementsAsync(10, cts.Token);
        }
    }
}
