using Orleans.Runtime;
using Tester;
using TestExtensions;
using Xunit;

namespace ServiceBus.Tests
{
    // Assembly collections must be defined once in each assembly
    [CollectionDefinition("DefaultCluster")]
    public class DefaultClusterTestCollection : ICollectionFixture<DefaultClusterFixture> { }
    

    [CollectionDefinition(TestEnvironmentFixture.DefaultCollection)]
    public class TestEnvironmentFixtureCollection : ICollectionFixture<TestEnvironmentFixture> { }

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
