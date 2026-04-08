using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Placement;
using TestExtensions;
using Xunit;

namespace UnitTests.Runtime
{
    [TestCategory("BVT"), TestCategory("Placement")]
    public class PlacementServiceTests
    {
        [Fact]
        public async Task LifecycleStop_CompletesWorkerTasks()
        {
            var target = CreateTarget();

            await StopAsync(target);

            Assert.All(GetWorkerTasks(target), task => Assert.True(task.IsCompleted));
        }

        [Fact]
        public async Task LifecycleStop_WithCanceledToken_CompletesWorkerTasks()
        {
            var target = CreateTarget();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await StopAsync(target, cts.Token);

            Assert.All(GetWorkerTasks(target), task => Assert.True(task.IsCompleted));
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
        public async Task PlacementWorker_AfterLifecycleStop_ThrowsSiloUnavailableException()
        {
            var target = CreateTarget();
            var message = new Message
            {
                TargetGrain = GrainId.Create("test", "grain-1"),
                InterfaceType = GrainInterfaceType.Create("test.interface"),
                InterfaceVersion = 1,
            };

            await StopAsync(target);

            await Assert.ThrowsAsync<SiloUnavailableException>(() => InvokeGetOrPlaceActivationAsync(target, message));
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
            localSiloDetails.SiloAddress.Returns(SiloAddress.FromParsableString("127.0.0.1:100@1"));

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

        private static Task<SiloAddress> InvokeGetOrPlaceActivationAsync(PlacementService target, Message message)
        {
            var worker = GetWorkers(target)[0];
            var method = worker.GetType().GetMethod("GetOrPlaceActivationAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            var result = method.Invoke(worker, [message]);
            if (result is Task<SiloAddress> task)
            {
                return task;
            }

            var taskProperty = result?.GetType().GetProperty("Task", BindingFlags.Instance | BindingFlags.Public);
            Assert.NotNull(taskProperty);
            return Assert.IsAssignableFrom<Task<SiloAddress>>(taskProperty.GetValue(result));
        }

        private static Task[] GetWorkerTasks(PlacementService target)
        {
            return GetWorkers(target)
                .Select(worker =>
                {
                    var field = worker.GetType().GetField("_processLoopTask", BindingFlags.Instance | BindingFlags.NonPublic);
                    Assert.NotNull(field);
                    return Assert.IsAssignableFrom<Task>(field.GetValue(worker));
                })
                .ToArray();
        }

        private static object[] GetWorkers(PlacementService target)
        {
            var field = typeof(PlacementService).GetField("_workers", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            return Assert.IsAssignableFrom<Array>(field.GetValue(target)).Cast<object>().ToArray();
        }
    }
}
