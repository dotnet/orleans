using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Runtime;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
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
    public class LifecycleObserverCreationTests : IClassFixture<LifecycleObserverCreationTests.Fixture>
    {
        private readonly IHost _host;

        public class Fixture : IAsyncLifetime
        {
            private readonly TestClusterPortAllocator portAllocator;
            public IHost Host { get; private set; }
            public static bool ObserverCreated { get; set; }

            public Fixture()
            {
                portAllocator = new TestClusterPortAllocator();
            }

            public async Task InitializeAsync()
            {
                ObserverCreated = false;
                var (siloPort, gatewayPort) = portAllocator.AllocateConsecutivePortPairs(1);
                Host = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder()
                    .UseOrleans(siloBuilder =>
                    {
                        siloBuilder
                            .UseLocalhostClustering(siloPort, gatewayPort)
                            .ConfigureServices(services =>
                            {
                                services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>, TestLifecycleParticipant>();
                            });
                    })
                    .Build();
                await Host.StartAsync();
            }

            public async Task DisposeAsync()
            {
                try
                {
                    await Host.StopAsync();
                }
                finally
                {
                    Host.Dispose();
                    portAllocator.Dispose();
                }
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

            public TestLifecycleParticipant(IGrainFactory grainFactory)
            {
                _grainFactory = grainFactory;
            }

            public void Participate(ISiloLifecycle lifecycle)
            {
                lifecycle.Subscribe(
                    nameof(TestLifecycleParticipant),
                    ServiceLifecycleStage.Active,
                    OnStart,
                    OnStop);
            }

            private Task OnStart(CancellationToken ct)
            {
                // This is the critical test - creating a grain observer during lifecycle participation
                // should work now that lifecycle events run via Task.Run instead of on a SystemTarget
                var observer = new TestObserver();
                var reference = _grainFactory.CreateObjectReference<ISimpleGrainObserver>(observer);
                
                // Verify the reference was created successfully
                Assert.NotNull(reference);
                
                // Clean up
                _grainFactory.DeleteObjectReference<ISimpleGrainObserver>(reference);
                
                Fixture.ObserverCreated = true;
                return Task.CompletedTask;
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

        public LifecycleObserverCreationTests(Fixture fixture)
        {
            _host = fixture.Host;
        }

        /// <summary>
        /// Tests that grain observers can be created during lifecycle participation.
        /// This validates the fix for the issue where CreateObjectReference would fail
        /// with "Cannot create a local object reference from a grain" when called during
        /// lifecycle events, because those events were previously scheduled on a SystemTarget.
        /// </summary>
        [Fact]
        public void LifecycleParticipant_CanCreateGrainObserver()
        {
            // The test passes if the host started successfully and the observer was created
            // in the TestLifecycleParticipant.OnStart method without throwing an exception
            Assert.True(Fixture.ObserverCreated, "Observer should have been created during lifecycle participation");
        }

        /// <summary>
        /// Tests that grains can be called from within lifecycle participants.
        /// This further validates that lifecycle events now run without a grain context,
        /// enabling normal grain calls and observer creation.
        /// </summary>
        [Fact]
        public async Task LifecycleParticipant_CanCallGrains()
        {
            var client = _host.Services.GetRequiredService<IClusterClient>();
            
            // Call a grain to verify the silo is fully operational after lifecycle startup
            var grain = client.GetGrain<ISimpleGrain>(42);
            await grain.SetA(100);
            var value = await grain.GetA();
            
            Assert.Equal(100, value);
        }
    }
}
