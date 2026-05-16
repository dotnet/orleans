using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Diagnostics;
using Orleans.Runtime.Placement;
using Orleans.TestingHost.Diagnostics;
using TestExtensions;
using Xunit;

namespace UnitTests.Runtime
{
    [TestCategory("BVT"), TestCategory("Placement")]
    public class PlacementServiceTests
    {
        private static int _siloGeneration;

        [Fact]
        public async Task LifecycleStop_CompletesWorkerTasks()
        {
            var target = CreateTarget();
            var testAccessor = GetTestAccessor(target);
            using var collector = new DiagnosticEventCollector(PlacementServiceEvents.ListenerName);

            await StopAsync(target);

            Assert.All(testAccessor.WorkerTasks, task => Assert.True(task.IsCompleted));
            await AssertWorkerStopEventsAsync(target, collector);
        }

        [Fact]
        public async Task LifecycleStop_WithCanceledToken_CompletesWorkerTasks()
        {
            var target = CreateTarget();
            var testAccessor = GetTestAccessor(target);
            using var collector = new DiagnosticEventCollector(PlacementServiceEvents.ListenerName);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await StopAsync(target, cts.Token);

            Assert.All(testAccessor.WorkerTasks, task => Assert.True(task.IsCompleted));
            await AssertWorkerStopEventsAsync(target, collector);
        }

        [Fact]
        public async Task AddressMessage_AfterLifecycleStop_ThrowsSiloUnavailableException()
        {
            var target = CreateTarget();
            var message = new Message
            {
                TargetGrain = GrainId.Create("test", "grain-1"),
            };

            await StopAsync(target);

            await Assert.ThrowsAsync<SiloUnavailableException>(() => target.AddressMessage(message));
        }

        [Fact]
        public async Task GetOrPlaceActivationAsync_AfterLifecycleStop_ThrowsSiloUnavailableException()
        {
            var target = CreateTarget();
            var message = new Message
            {
                TargetGrain = GrainId.Create("test", "grain-1"),
                InterfaceType = GrainInterfaceType.Create("test.interface"),
                InterfaceVersion = 1,
            };

            await StopAsync(target);

            await Assert.ThrowsAsync<SiloUnavailableException>(() => GetTestAccessor(target).GetOrPlaceActivationAsync(message));
        }

        [Fact]
        public async Task GetCompatibleSilos_AfterLifecycleStop_ThrowsSiloUnavailableException()
        {
            var target = CreateTarget();
            var placementTarget = new PlacementTarget(GrainId.Create("test", "grain-1"), new Dictionary<string, object>(), default, 0);

            await StopAsync(target);

            Assert.Throws<SiloUnavailableException>(() => target.GetCompatibleSilos(placementTarget));
        }

        [Fact]
        public async Task GetCompatibleSilosWithVersions_AfterLifecycleStop_ThrowsSiloUnavailableException()
        {
            var target = CreateTarget();
            var placementTarget = new PlacementTarget(
                GrainId.Create("test", "grain-1"),
                new Dictionary<string, object>(),
                GrainInterfaceType.Create("test.interface"),
                1);

            await StopAsync(target);

            Assert.Throws<SiloUnavailableException>(() => target.GetCompatibleSilosWithVersions(placementTarget));
        }

        private static PlacementService CreateTarget()
        {
            var optionsMonitor = Substitute.For<IOptionsMonitor<SiloMessagingOptions>>();
            optionsMonitor.CurrentValue.Returns(new SiloMessagingOptions());

            var localSiloDetails = Substitute.For<ILocalSiloDetails>();
            localSiloDetails.SiloAddress.Returns(SiloAddress.New(IPAddress.Loopback, 11111, Interlocked.Increment(ref _siloGeneration)));

            var siloStatusOracle = Substitute.For<ISiloStatusOracle>();
            siloStatusOracle.CurrentStatus.Returns(SiloStatus.Active);

            return new PlacementService(
                optionsMonitor,
                localSiloDetails,
                siloStatusOracle,
                NullLoggerFactory.Instance.CreateLogger<PlacementService>(),
                grainLocator: null!,
                grainInterfaceVersions: null!,
                versionSelectorManager: null!,
                directorResolver: null!,
                strategyResolver: null!,
                filterStrategyResolver: null!,
                placementFilterDirectoryResolver: null!);
        }

        private static async Task StopAsync(PlacementService target, CancellationToken cancellationToken = default)
        {
            var lifecycle = new SiloLifecycleSubject(NullLoggerFactory.Instance.CreateLogger<SiloLifecycleSubject>());
            ((ILifecycleParticipant<ISiloLifecycle>)target).Participate(lifecycle);
            await lifecycle.OnStart();
            await lifecycle.OnStop(cancellationToken);
        }

        private static PlacementService.ITestAccessor GetTestAccessor(PlacementService target) => target;

        private static async Task AssertWorkerStopEventsAsync(PlacementService target, DiagnosticEventCollector collector)
        {
            var workerCount = GetTestAccessor(target).WorkerTasks.Length;
            var stoppedEvents = new List<PlacementServiceEvents.WorkerStopped>(workerCount);

            while (stoppedEvents.Count < workerCount)
            {
                var diagnosticEvent = await collector.WaitForEventAsync(
                    nameof(PlacementServiceEvents.WorkerStopped),
                    evt => evt.Payload is PlacementServiceEvents.WorkerStopped stopped
                        && stopped.SiloAddress == target.LocalSilo
                        && stoppedEvents.All(existing => existing.WorkerIndex != stopped.WorkerIndex),
                    TimeSpan.FromSeconds(10));

                stoppedEvents.Add(Assert.IsType<PlacementServiceEvents.WorkerStopped>(diagnosticEvent.Payload));
            }

            Assert.Equal(workerCount, stoppedEvents.Count);
        }
    }
}
