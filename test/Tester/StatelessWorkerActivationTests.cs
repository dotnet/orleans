using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;

namespace UnitTests.General;

/// <summary>
/// Tests for stateless worker grain activation and scaling behavior.
/// 
/// Stateless worker grains are a special type of grain marked with [StatelessWorker] attribute that:
/// - Can have multiple activations on the same silo (up to MaxLocalWorkers limit)
/// - Are automatically scaled based on load
/// - Don't maintain state between calls
/// - Are ideal for CPU-intensive or I/O-bound operations that can be parallelized
/// 
/// These tests verify:
/// - Single activation behavior when load is low
/// - Automatic scaling up to MaxLocalWorkers under concurrent load
/// - Proper cleanup when activations are deactivated
/// </summary>
public class StatelessWorkerActivationTests : IClassFixture<StatelessWorkerActivationTests.Fixture>
{
    public class Fixture : BaseTestClusterFixture
    {
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = 1;
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        }

        private class SiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                // Shared state service used by test grains to track activation counts
                hostBuilder.Services.AddSingleton<StatelessWorkerScalingGrainSharedState>();
            }
        }
    }

    private readonly Fixture _fixture;

    public StatelessWorkerActivationTests(Fixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Verifies that a stateless worker grain maintains a single activation
    /// when requests are sequential (no concurrent load).
    /// This demonstrates that Orleans doesn't unnecessarily create multiple
    /// activations when they're not needed.
    /// </summary>
    [Fact, TestCategory("BVT"), TestCategory("StatelessWorker")]
    public async Task SingleWorkerInvocationUnderLoad()
    {
        var workerGrain = _fixture.GrainFactory.GetGrain<IStatelessWorkerScalingGrain>(0);

        for (var i = 0; i < 100; i++)
        {
            var activationCount = await workerGrain.GetActivationCount();
            Assert.Equal(1, activationCount);
        }
    }

    /// <summary>
    /// Tests automatic scaling of stateless worker activations under concurrent load.
    /// 
    /// Process:
    /// 1. Start with single activation
    /// 2. Create concurrent requests that block (using Wait())
    /// 3. Orleans detects blocked activations and creates new ones
    /// 4. Scaling continues up to MaxLocalWorkers (4 in this test)
    /// 
    /// This demonstrates Orleans' ability to automatically scale stateless
    /// workers based on actual load, not just request count.
    /// </summary>
    [Fact, TestCategory("BVT"), TestCategory("StatelessWorker")]
    public async Task MultipleWorkerInvocationUnderLoad()
    {
        const int MaxLocalWorkers = 4;  // Maximum activations per silo
        var waiters = new List<Task>();  // Track blocking calls
        var worker = _fixture.GrainFactory.GetGrain<IStatelessWorkerScalingGrain>(1);

        // Initially, only one activation exists
        var activationCount = await worker.GetActivationCount();
        Assert.Equal(1, activationCount);

        // First blocking call triggers creation of second activation
        waiters.Add(worker.Wait());
        await Until(async () => 2 == await worker.GetActivationCount());
        activationCount = await worker.GetActivationCount();
        Assert.Equal(2, activationCount);

        waiters.Add(worker.Wait());
        await Until(async () => 3 == await worker.GetActivationCount());
        activationCount = await worker.GetActivationCount();
        Assert.Equal(3, activationCount);

        waiters.Add(worker.Wait());
        await Until(async () => 4 == await worker.GetActivationCount());
        activationCount = await worker.GetActivationCount();
        Assert.Equal(4, activationCount);

        var waitingCount = await worker.GetWaitingCount();
        Assert.Equal(3, waitingCount);

        for (var i = 0; i < MaxLocalWorkers; i++)
        {
            waiters.Add(worker.Wait());
        }

        await Until(async () => MaxLocalWorkers == await worker.GetActivationCount());
        await Until(async () => MaxLocalWorkers == await worker.GetWaitingCount());
        activationCount = await worker.GetActivationCount();
        Assert.Equal(MaxLocalWorkers, activationCount);
        waitingCount = await worker.GetWaitingCount();
        Assert.Equal(MaxLocalWorkers, waitingCount);

        // Release all the waiting workers to clean up
        for (var i = 0; i < waiters.Count; i++)
        {
            await worker.Release();
        }

        // Ensure all blocking calls complete properly
        await Task.WhenAll(waiters);
    }

    /// <summary>
    /// Tests that stateless worker activations are properly cleaned up from the catalog
    /// when deactivated. This is important for:
    /// - Preventing memory leaks
    /// - Ensuring accurate activation counts
    /// - Verifying the grain directory properly tracks stateless workers
    /// 
    /// Uses management grain to force activation collection and verify cleanup.
    /// </summary>
    [Fact, TestCategory("BVT"), TestCategory("StatelessWorker")]
    public async Task CatalogCleanupOnDeactivation()
    {
        var workerGrain = _fixture.GrainFactory.GetGrain<IStatelessWorkerGrain>(0);
        var mgmt = _fixture.GrainFactory.GetGrain<IManagementGrain>(0);
        
        var numActivations = await mgmt.GetGrainActivationCount((GrainReference)workerGrain);
        Assert.Equal(0, numActivations);
        
        // Activate grain with a dummy call
        await workerGrain.DummyCall();
        
        numActivations = await mgmt.GetGrainActivationCount((GrainReference)workerGrain);
        Assert.Equal(1, numActivations);
        
        // Force immediate activation collection to trigger deactivation
        // TimeSpan.Zero means collect all idle activations immediately
        await mgmt.ForceActivationCollection(TimeSpan.Zero);
        
        // The activation count for the stateless worker grain should become 0 again
        await Until(
            async () => await mgmt.GetGrainActivationCount((GrainReference)workerGrain) == 0,
            5_000
        );
    }

    /// <summary>
    /// Helper method to wait for an async condition to become true.
    /// Used to handle eventual consistency in activation creation/destruction.
    /// </summary>
    private static async Task Until(Func<Task<bool>> condition, int maxTimeout = 40_000)
    {
        while (!await condition() && (maxTimeout -= 10) > 0) await Task.Delay(10);
        Assert.True(maxTimeout > 0);
    }
}
