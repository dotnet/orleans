using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests.General
{
    /// <summary>
    /// Tests for creating grain observers during lifecycle participation.
    /// This validates that grain observers can be created during silo lifecycle stages,
    /// which previously failed because lifecycle events were executed on a SystemTarget
    /// (grain context), preventing CreateObjectReference from working.
    /// </summary>
    [TestCategory("BVT"), TestCategory("Lifecycle"), TestCategory("Observer")]
    public class LifecycleObserverCreationTests
    {
        /// <summary>
        /// Tests that grain observers can be created during lifecycle participation.
        /// This validates the fix for the issue where CreateObjectReference would fail
        /// with "Cannot create a local object reference from a grain" when called during
        /// lifecycle events, because those events were previously scheduled on a SystemTarget.
        /// </summary>
        [Fact]
        public async Task LifecycleParticipant_CanCreateGrainObserver()
        {
            var observerCreated = false;
            var grainCalled = false;

            // Create a test cluster with a lifecycle participant that creates an observer
            var builder = new InProcessTestClusterBuilder();
            builder.ConfigureSilo((options, siloBuilder) =>
            {
                siloBuilder.ConfigureServices(services =>
                {
                    services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>>(serviceProvider =>
                        new TestLifecycleParticipant(
                            serviceProvider.GetRequiredService<IGrainFactory>(),
                            () => observerCreated = true,
                            () => grainCalled = true));
                });
            });

            var cluster = builder.Build();
            await cluster.DeployAsync();

            try
            {
                // Verify the observer was created during lifecycle participation
                Assert.True(observerCreated, "Observer should have been created during lifecycle participation");
                
                // Verify that a grain call was successfully made during lifecycle participation
                Assert.True(grainCalled, "Grain should have been called during lifecycle participation");
                
                // Also verify the cluster is operational by calling a grain after startup
                var grain = cluster.Client.GetGrain<ISimpleGrain>(42);
                await grain.SetA(100);
                var value = await grain.GetA();
                Assert.Equal(100, value);
            }
            finally
            {
                await cluster.DisposeAsync();
            }
        }

        /// <summary>
        /// Lifecycle participant that creates a grain observer during the Active stage.
        /// This simulates the issue scenario where a hosted service or lifecycle participant
        /// needs to create grain observers during silo startup.
        /// </summary>
        private class TestLifecycleParticipant : ILifecycleParticipant<ISiloLifecycle>
        {
            private readonly IGrainFactory _grainFactory;
            private readonly Action _onObserverCreated;
            private readonly Action _onGrainCalled;

            public TestLifecycleParticipant(IGrainFactory grainFactory, Action onObserverCreated, Action onGrainCalled)
            {
                _grainFactory = grainFactory;
                _onObserverCreated = onObserverCreated;
                _onGrainCalled = onGrainCalled;
            }

            public void Participate(ISiloLifecycle lifecycle)
            {
                lifecycle.Subscribe(
                    nameof(TestLifecycleParticipant),
                    ServiceLifecycleStage.Active,
                    OnStart,
                    OnStop);
            }

            private async Task OnStart(CancellationToken ct)
            {
                // This is the critical test - creating a grain observer during lifecycle participation
                // should work now that lifecycle events run via Task.Run instead of on a SystemTarget
                var observer = new TestObserver();
                var reference = _grainFactory.CreateObjectReference<ISimpleGrainObserver>(observer);
                
                // Verify the reference was created successfully
                Assert.NotNull(reference);
                _onObserverCreated();
                
                // Also test that we can make grain calls during lifecycle participation
                var grain = _grainFactory.GetGrain<ISimpleGrain>(123);
                await grain.SetA(50);
                var value = await grain.GetA();
                Assert.Equal(50, value);
                _onGrainCalled();
                
                // Clean up
                _grainFactory.DeleteObjectReference<ISimpleGrainObserver>(reference);
            }

            private Task OnStop(CancellationToken ct)
            {
                return Task.CompletedTask;
            }
        }

        /// <summary>
        /// Simple observer implementation for testing.
        /// </summary>
        private class TestObserver : ISimpleGrainObserver
        {
            public void StateChanged(int a, int b)
            {
                // No-op for testing
            }
        }
    }
}
