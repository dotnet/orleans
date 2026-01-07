using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
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
    /// Tests for stuck grain detection and handling.
    /// Uses FakeTimeProvider for deterministic testing of time-dependent stuck detection.
    /// </summary>
    public class StuckGrainTests : OrleansTestingBase, IClassFixture<StuckGrainTests.Fixture>, IDisposable
    {
        private readonly Fixture fixture;
        private readonly GrainDiagnosticObserver _grainObserver;

        public class Fixture : BaseTestClusterFixture
        {
            /// <summary>
            /// Shared FakeTimeProvider instance used by all silos and tests.
            /// This enables virtual time control for fast, deterministic stuck detection testing.
            /// </summary>
            internal static FakeTimeProvider SharedTimeProvider { get; private set; } = null!;

            public override async Task InitializeAsync()
            {
                // Create the shared FakeTimeProvider BEFORE starting the cluster
                SharedTimeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
                await base.InitializeAsync();
            }

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

                    // Register the shared FakeTimeProvider for deterministic stuck detection
                    hostBuilder.Services.AddSingleton<TimeProvider>(SharedTimeProvider);
                }
            }
        }

        public StuckGrainTests(Fixture fixture)
        {
            this.fixture = fixture;
            _grainObserver = GrainDiagnosticObserver.Create();
        }

        public void Dispose()
        {
            _grainObserver?.Dispose();
        }

        [Fact, TestCategory("Functional"), TestCategory("ActivationCollection")]
        public async Task StuckGrainTest_Basic()
        {
            var id = Guid.NewGuid();
            var stuckGrain = this.fixture.GrainFactory.GetGrain<IStuckGrain>(id);
            var task = stuckGrain.RunForever();

            // Should timeout
            await Assert.ThrowsAsync<TimeoutException>(() => task.WaitAsync(TimeSpan.FromSeconds(1)));

            var cleaner = this.fixture.GrainFactory.GetGrain<IStuckCleanGrain>(id);
            await cleaner.Release(id);

            // Should complete now
            await task.WaitAsync(TimeSpan.FromSeconds(1));

            // Wait for the grain to be deactivated by activation collection.
            // The ActivationCollector uses TimeProvider, so we need to advance FakeTimeProvider
            // to trigger the collection loop. We advance time in a loop while waiting for the event.
            var grainId = stuckGrain.GetGrainId();
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                // Advance virtual time past CollectionAge (2s) + CollectionQuantum (1s)
                Fixture.SharedTimeProvider.Advance(TimeSpan.FromSeconds(1));
                
                // Brief yield to allow activation collection to run
                await Task.Delay(10);
                
                // Check if grain was deactivated
                if (!await cleaner.IsActivated(id))
                {
                    break;
                }
            }

            Assert.False(await cleaner.IsActivated(id), "Grain activation is supposed be garbage collected, but it is still running.");
        }

        [Fact, TestCategory("Functional"), TestCategory("ActivationCollection")]
        public async Task StuckGrainTest_StuckDetectionAndForward()
        {
            var id = Guid.NewGuid();
            var stuckGrain = this.fixture.GrainFactory.GetGrain<IStuckGrain>(id);
            var task = stuckGrain.RunForever();

            // Should timeout
            await Assert.ThrowsAsync<TimeoutException>(() => task.WaitAsync(TimeSpan.FromSeconds(1)));

            for (var i = 0; i < 3; i++)
            {
                await Assert.ThrowsAsync<TimeoutException>(
                    () => stuckGrain.NonBlockingCall().WaitAsync(TimeSpan.FromMilliseconds(500)));
            }

            // Advance virtual time past MaxRequestProcessingTime (3 seconds).
            // Stuck detection triggers when a NEW message arrives and checks that the current
            // request has been processing longer than MaxRequestProcessingTime.
            // By advancing FakeTimeProvider, we make the runtime think 4 seconds have passed
            // without actually waiting - enabling fast, deterministic testing.
            Fixture.SharedTimeProvider.Advance(TimeSpan.FromSeconds(4));

            // Brief yield to allow any pending work to be scheduled
            await Task.Yield();

            // This call triggers stuck detection, which causes the grain to be unregistered
            // and all pending requests (including this one) to be forwarded to a new activation.
            await stuckGrain.NonBlockingCall();

            // All 4 otherwise stuck calls should have been forwarded to a new activation
            Assert.Equal(4, await stuckGrain.GetNonBlockingCallCounter());
        }

        [Fact, TestCategory("Functional"), TestCategory("ActivationCollection")]
        public async Task StuckGrainTest_StuckDetectionOnDeactivation()
        {
            var id = Guid.NewGuid();
            var stuckGrain = this.fixture.GrainFactory.GetGrain<IStuckGrain>(id);
            await stuckGrain.BlockingDeactivation();

            await StuckGrain.WaitForDeactivationStart(stuckGrain.GetGrainId());

            for (var i = 0; i < 3; i++)
            {
                await Assert.ThrowsAsync<TimeoutException>(
                    () => stuckGrain.NonBlockingCall().WaitAsync(TimeSpan.FromMilliseconds(500)));
            }

            // No issue on this one
            await stuckGrain.NonBlockingCall();

            // All 4 otherwise stuck calls should have been forwarded to a new activation
            Assert.Equal(4, await stuckGrain.GetNonBlockingCallCounter());
        }

        [Fact, TestCategory("Functional"), TestCategory("ActivationCollection")]
        public async Task StuckGrainTest_StuckDetectionOnActivation()
        {
            var id = Guid.NewGuid();
            var stuckGrain = this.fixture.GrainFactory.GetGrain<IStuckGrain>(id);

            // The cancellation token passed to OnActivateAsync should become cancelled and this will cause activation to fail.
            RequestContext.Set("block_activation_seconds", 30);
            await Assert.ThrowsAsync<TaskCanceledException>(() => stuckGrain.NonBlockingCall());

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
