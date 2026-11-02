using Orleans.Configuration;
using Orleans.TestingHost;
using Orleans.Internal;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;
using Orleans.Runtime;
using UnitTests.Grains;

namespace UnitTests.StuckGrainTests
{
    /// <summary>
    /// Summary description for PersistenceTest
    /// </summary>
    [TestSuite("Functional")]
    [TestProvider("None")]
    [TestArea("Runtime")]
    public class StuckGrainTests : OrleansTestingBase, IClassFixture<StuckGrainTests.Fixture>
    {
        private readonly Fixture fixture;

        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.Options.InitialSilosCount = 1;
                builder.AddSiloBuilderConfigurator<SiloHostConfigurator>();
            }

            private class SiloHostConfigurator : ISiloConfigurator
            {
                public void Configure(ISiloBuilder hostBuilder)
                {
                    hostBuilder.Configure<GrainCollectionOptions>(options =>
                    {
                        options.CollectionAge = TimeSpan.FromSeconds(2);
                        options.CollectionQuantum = TimeSpan.FromSeconds(1);

                        options.ActivationTimeout = TimeSpan.FromSeconds(2);
                        options.DeactivationTimeout = TimeSpan.FromSeconds(2);
                    });

                    hostBuilder.Configure<SiloMessagingOptions>(options =>
                    {
                        options.MaxRequestProcessingTime = TimeSpan.FromSeconds(3);
                    });
                }
            }
        }

        public StuckGrainTests(Fixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact, TestCategory("Functional"), TestCategory("ActivationCollection")]
        public async Task StuckGrainTest_Basic()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var id = Guid.NewGuid();
            var stuckGrain = this.fixture.GrainFactory.GetGrain<IStuckGrain>(id);
            var task = stuckGrain.RunForever();

            // Should timeout
            await Assert.ThrowsAsync<TimeoutException>(() => task.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken));

            var cleaner = this.fixture.GrainFactory.GetGrain<IStuckCleanGrain>(id);
            await cleaner.Release(id);

            // Should complete now
            await task.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken);

            // wait for activation collection
            await Task.Delay(TimeSpan.FromSeconds(6), cancellationToken);

            Assert.False(await cleaner.IsActivated(id), "Grain activation is supposed be garbage collected, but it is still running.");
        }

        [Fact, TestCategory("Functional"), TestCategory("ActivationCollection")]
        public async Task StuckGrainTest_StuckDetectionAndForward()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var id = Guid.NewGuid();
            var stuckGrain = this.fixture.GrainFactory.GetGrain<IStuckGrain>(id);
            var task = stuckGrain.RunForever();

            // Should timeout
            await Assert.ThrowsAsync<TimeoutException>(() => task.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken));

            var calls = new Task[3];
            for (var i = 0; i < calls.Length; i++)
            {
                var call = stuckGrain.NonBlockingCall();
                calls[i] = call;
                await Task.WhenAny(call, Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken));
            }

            // Wait so the first task will reach with DefaultCollectionAge timeout
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);

            // No issue on this one
            await stuckGrain.NonBlockingCall();

            // All otherwise stuck calls should have been forwarded to a new activation.
            await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }

        [Fact, TestCategory("Functional"), TestCategory("ActivationCollection")]
        public async Task StuckGrainTest_DeactivationForwardsCallsAfterOnDeactivateAsyncCompletes()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var id = Guid.NewGuid();
            var stuckGrain = this.fixture.GrainFactory.GetGrain<IStuckGrain>(id);
            await stuckGrain.BlockingDeactivation();

            await StuckGrain.WaitForDeactivationStart(stuckGrain.GetGrainId());

            var calls = new Task[3];
            for (var i = 0; i < calls.Length; i++)
            {
                calls[i] = stuckGrain.NonBlockingCall();
            }

            // Readiness guarantees that the deactivation callback's release task is registered.
            Assert.True(StuckGrain.Release(id));
            await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

            // No issue on this one
            await stuckGrain.NonBlockingCall();

            Assert.Equal(4, await stuckGrain.GetNonBlockingCallCounter(cancellationToken));
        }

        [Fact, TestCategory("Functional"), TestCategory("ActivationCollection")]
        public async Task StuckGrainTest_StuckDetectionOnActivation()
        {
            var id = Guid.NewGuid();
            var stuckGrain = this.fixture.GrainFactory.GetGrain<IStuckGrain>(id);

            // The cancellation token passed to OnActivateAsync becomes cancelled, and activation fails when OnActivateAsync throws.
            RequestContext.Set("block_activation_seconds", 30);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stuckGrain.NonBlockingCall());

            // Check to see that it did try to activate.
            RequestContext.Clear();
            var unstuckGrain = this.fixture.GrainFactory.GetGrain<IStuckGrain>(Guid.NewGuid());
            await unstuckGrain.NonBlockingCall();

            var activationAttempted = await unstuckGrain.DidActivationTryToStart(stuckGrain.GetGrainId());
            Assert.True(activationAttempted);

            // Now that activation is not blocked (we cleared the request context value which told it to block), let's check that our previously stuck grain works.
            await stuckGrain.NonBlockingCall();
        }
    }
}
