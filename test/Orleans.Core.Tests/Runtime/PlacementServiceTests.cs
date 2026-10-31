using System.Collections.Immutable;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Diagnostics;
using Orleans.GrainDirectory;
using Orleans.Metadata;
using Orleans.Placement;
using Orleans.Runtime;
using Orleans.Runtime.Diagnostics;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Placement;
using Orleans.Runtime.Placement.Filtering;
using Orleans.Runtime.Utilities;
using Orleans.Runtime.Versions;
using Orleans.Runtime.Versions.Compatibility;
using Orleans.Runtime.Versions.Selector;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Selector;
using Orleans.TestingHost.Diagnostics;
using TestExtensions;
using Xunit;

namespace UnitTests.Runtime
{
    [TestSuite("BVT")]
    [TestProvider("None")]
    [TestArea("Placement")]
    [TestCategory("BVT"), TestCategory("Placement")]
    public class PlacementServiceTests
    {
        private static readonly GrainType TestGrainType = GrainType.Create("test");
        private static readonly GrainInterfaceType TestInterfaceType = GrainInterfaceType.Create("test.interface");
        private static int _siloGeneration;

        [Fact]
        public async Task LifecycleStop_CompletesWorkerTasks()
        {
            var target = CreateTarget();
            var testAccessor = GetTestAccessor(target);
            using var collector = new DiagnosticEventCollector(PlacementServiceEvents.ListenerName);

            await StopAsync(target, TestContext.Current.CancellationToken);

            Assert.All(testAccessor.WorkerTasks, task => Assert.True(task.IsCompleted));
            await AssertWorkerStopEventsAsync(target, collector, TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task LifecycleStop_WithCanceledToken_CompletesWorkerTasks()
        {
            var target = CreateTarget();
            var testAccessor = GetTestAccessor(target);
            using var collector = new DiagnosticEventCollector(PlacementServiceEvents.ListenerName);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cts.Cancel();

            var lifecycle = await StartAsync(target, TestContext.Current.CancellationToken);
            await lifecycle.OnStop(cts.Token);

            Assert.All(testAccessor.WorkerTasks, task => Assert.True(task.IsCompleted));
            await AssertWorkerStopEventsAsync(target, collector, TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task AddressMessage_AfterLifecycleStop_ThrowsSiloUnavailableException()
        {
            var target = CreateTarget();
            var message = new Message
            {
                TargetGrain = GrainId.Create("test", "grain-1"),
            };

            await StopAsync(target, TestContext.Current.CancellationToken);

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

            await StopAsync(target, TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<SiloUnavailableException>(() => GetTestAccessor(target).GetOrPlaceActivationAsync(message));
        }

        [Fact]
        public async Task GetOrPlaceActivationAsync_ExpiredMessage_DoesNotRetry()
        {
            var target = CreateTarget();
            var message = new Message
            {
                TargetGrain = GrainId.Create("test", "grain-1"),
                InterfaceType = GrainInterfaceType.Create("test.interface"),
                InterfaceVersion = 1,
                TimeToLive = TimeSpan.FromMilliseconds(-1),
            };

            await Assert.ThrowsAsync<OperationCanceledException>(() => GetTestAccessor(target).GetOrPlaceActivationAsync(message));
            await StopAsync(target, TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task GetOrPlaceActivationAsync_WhenLookupDoesNotComplete_TimesOut()
        {
            var lookupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var lookupCompletion = new TaskCompletionSource<AddressAndTag>(TaskCreationOptions.RunContinuationsAsynchronously);
            var localGrainDirectory = Substitute.For<ILocalGrainDirectory>();
            localGrainDirectory.LookupAsync(Arg.Any<GrainId>(), Arg.Any<int>()).Returns(_ =>
            {
                lookupStarted.TrySetResult();
                return lookupCompletion.Task;
            });

            var timeProvider = new FakeTimeProvider();
            var messagingOptions = new SiloMessagingOptions
            {
                PlacementTimeout = TimeSpan.FromSeconds(10),
                PlacementMaxRetries = 0,
            };
            var fixture = new PlacementServiceFixture(
                messagingOptions: messagingOptions,
                timeProvider: timeProvider,
                localGrainDirectory: localGrainDirectory);
            var message = new Message
            {
                TargetGrain = GrainId.Create("test", "grain-1"),
                InterfaceType = GrainInterfaceType.Create("test.interface"),
                InterfaceVersion = 1,
            };

            var placementTask = GetTestAccessor(fixture.Target).GetOrPlaceActivationAsync(message);
            await lookupStarted.Task;
            timeProvider.Advance(messagingOptions.PlacementTimeout);

            var exception = await Assert.ThrowsAsync<TimeoutException>(
                () => placementTask.WaitAsync(TestContext.Current.CancellationToken));
            Assert.IsType<Polly.Timeout.TimeoutRejectedException>(exception.InnerException);

            lookupCompletion.TrySetResult(default);
            await StopAsync(fixture.Target, TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task GetOrPlaceActivationAsync_WhenShutdownCancelsLookup_ThrowsSiloUnavailableException()
        {
            var lookupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var lookupCompletion = new TaskCompletionSource<AddressAndTag>(TaskCreationOptions.RunContinuationsAsynchronously);
            var localGrainDirectory = Substitute.For<ILocalGrainDirectory>();
            localGrainDirectory.LookupAsync(Arg.Any<GrainId>(), Arg.Any<int>()).Returns(_ =>
            {
                lookupStarted.TrySetResult();
                return lookupCompletion.Task;
            });

            var fixture = new PlacementServiceFixture(
                messagingOptions: new SiloMessagingOptions { PlacementMaxRetries = 0 },
                localGrainDirectory: localGrainDirectory);
            var message = new Message
            {
                TargetGrain = GrainId.Create("test", "grain-1"),
                InterfaceType = GrainInterfaceType.Create("test.interface"),
                InterfaceVersion = 1,
            };

            var placementTask = GetTestAccessor(fixture.Target).GetOrPlaceActivationAsync(message);
            await lookupStarted.Task;
            await StopAsync(fixture.Target, TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<SiloUnavailableException>(() => placementTask);
            lookupCompletion.TrySetResult(default);
        }

        [Fact]
        public async Task GetCompatibleSilos_AfterLifecycleStop_ThrowsSiloUnavailableException()
        {
            var target = CreateTarget();
            var placementTarget = new PlacementTarget(GrainId.Create("test", "grain-1"), new Dictionary<string, object>(), default, 0);

            await StopAsync(target, TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<SiloUnavailableException>(() => target.GetCompatibleSilosAsync(placementTarget, TestContext.Current.CancellationToken));
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

            await StopAsync(target, TestContext.Current.CancellationToken);

            Assert.Throws<SiloUnavailableException>(() => target.GetCompatibleSilosWithVersions(placementTarget));
        }

        [Fact]
        public async Task GetCompatibleSilos_WithoutFilters_UsesCachedResult()
        {
            var silos = CreateSilos(2);
            var fixture = new PlacementServiceFixture(activeSilos: silos, manifestSilos: silos);
            var placementTarget = CreatePlacementTarget();

            var first = await fixture.Target.GetCompatibleSilosAsync(placementTarget, TestContext.Current.CancellationToken);
            var second = await fixture.Target.GetCompatibleSilosAsync(placementTarget, TestContext.Current.CancellationToken);

            Assert.Same(first, second);
            Assert.True(silos.ToHashSet().SetEquals(first));

            await StopAsync(fixture.Target, TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task GetCompatibleSilos_WithoutInterfaceVersion_ReusesCacheAcrossInterfaces()
        {
            var silos = CreateSilos(2);
            var fixture = new PlacementServiceFixture(activeSilos: silos, manifestSilos: silos);
            var firstTarget = CreatePlacementTarget(interfaceType: GrainInterfaceType.Create("test.interface.one"));
            var secondTarget = CreatePlacementTarget(interfaceType: GrainInterfaceType.Create("test.interface.two"));

            var first = await fixture.Target.GetCompatibleSilosAsync(firstTarget, TestContext.Current.CancellationToken);
            var second = await fixture.Target.GetCompatibleSilosAsync(secondTarget, TestContext.Current.CancellationToken);

            Assert.Same(first, second);

            await StopAsync(fixture.Target, TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task GetCompatibleSilos_LocalSiloShuttingDown_ExcludesLocalSiloFromCachedManifest()
        {
            var silos = CreateSilos(2);
            var fixture = new PlacementServiceFixture(
                activeSilos: silos,
                manifestSilos: [silos[1]],
                localSiloStatus: SiloStatus.ShuttingDown);

            var result = await fixture.Target.GetCompatibleSilosAsync(CreatePlacementTarget(), TestContext.Current.CancellationToken);

            Assert.Equal(new[] { silos[1] }, result);

            await StopAsync(fixture.Target, TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task GetCompatibleSilosWithVersions_LocalSiloShuttingDown_ExcludesLocalSiloFromCachedManifest()
        {
            var silos = CreateSilos(2);
            var fixture = new PlacementServiceFixture(
                activeSilos: silos,
                manifestSilos: [silos[1]],
                localSiloStatus: SiloStatus.ShuttingDown,
                interfaceVersion: 1);
            var target = new PlacementTarget(
                GrainId.Create(TestGrainType, "grain-1"),
                new Dictionary<string, object>(),
                TestInterfaceType,
                1);

            var result = fixture.Target.GetCompatibleSilosWithVersions(target);

            Assert.Equal(new[] { silos[1] }, result[1]);

            await StopAsync(fixture.Target, TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task GetCompatibleSilos_MembershipChange_InvalidatesCachedResult()
        {
            var silos = CreateSilos(2);
            var fixture = new PlacementServiceFixture(activeSilos: silos, manifestSilos: silos);
            var placementTarget = CreatePlacementTarget();

            var first = await fixture.Target.GetCompatibleSilosAsync(placementTarget, TestContext.Current.CancellationToken);

            fixture.ClusterManifestProvider.SetCurrent(CreateClusterManifest(new[] { silos[1] }, version: new MajorMinorVersion(1, 0)));
            fixture.SetActiveSilos(silos[1]);
            var second = await fixture.Target.GetCompatibleSilosAsync(placementTarget, TestContext.Current.CancellationToken);

            Assert.NotSame(first, second);
            Assert.Equal(new[] { silos[1] }, second);

            await StopAsync(fixture.Target, TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task GetCompatibleSilos_ManifestUpdate_InvalidatesCachedResult()
        {
            var silos = CreateSilos(2);
            var manifestProvider = new TestClusterManifestProvider(CreateClusterManifest(new[] { silos[0] }));
            var fixture = new PlacementServiceFixture(activeSilos: silos, manifestSilos: new[] { silos[0] }, manifestProvider: manifestProvider);
            var placementTarget = CreatePlacementTarget();
            var first = await fixture.Target.GetCompatibleSilosAsync(placementTarget, TestContext.Current.CancellationToken);
            Assert.Equal(new[] { silos[0] }, first);

            manifestProvider.Publish(CreateClusterManifest(new[] { silos[1] }, version: new MajorMinorVersion(1, 0)));
            fixture.SetActiveSilos(silos[1]);

            var second = await fixture.Target.GetCompatibleSilosAsync(placementTarget, TestContext.Current.CancellationToken);

            Assert.NotSame(first, second);
            Assert.Equal(new[] { silos[1] }, second);

            await StopAsync(fixture.Target, TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task GetCompatibleSilos_WithFilters_RunsFiltersPerRequest()
        {
            var silos = CreateSilos(2);
            var fixture = new PlacementServiceFixture(activeSilos: silos, manifestSilos: silos, useFilter: true);

            var first = await fixture.Target.GetCompatibleSilosAsync(CreatePlacementTarget(new Dictionary<string, object> { ["target-silo"] = silos[0] }), TestContext.Current.CancellationToken);
            var second = await fixture.Target.GetCompatibleSilosAsync(CreatePlacementTarget(new Dictionary<string, object> { ["target-silo"] = silos[1] }), TestContext.Current.CancellationToken);

            Assert.Equal(new[] { silos[0] }, first);
            Assert.Equal(new[] { silos[1] }, second);
            Assert.Equal(2, fixture.FilterDirector!.CallCount);

            await StopAsync(fixture.Target, TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task GetCompatibleSilosAsync_AwaitsFiltersInOrderAndCopiesResults()
        {
            var silos = CreateSilos(3);
            await using var fixture = new PlacementServiceFixture(activeSilos: silos, useFilter: true, secondFilter: true);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var completion = new TaskCompletionSource<IReadOnlyList<SiloAddress>>(TaskCreationOptions.RunContinuationsAsynchronously);
            IReadOnlyList<SiloAddress>? firstInput = null;
            var output = new[] { silos[2], silos[0], silos[2] };
            fixture.FilterDirector.Callback = (_, candidates, _) =>
            {
                firstInput = candidates;
                started.SetResult();
                return completion.Task;
            };
            fixture.SecondFilterDirector.Callback = (_, candidates, _) =>
            {
                Assert.NotSame(output, candidates);
                Assert.Equal(output, candidates);
                return Task.FromResult(candidates);
            };

            var pending = fixture.Target.GetCompatibleSilosAsync(CreatePlacementTarget(), TestContext.Current.CancellationToken);
            await started.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.False(pending.IsCompleted);
            Assert.Equal(0, fixture.SecondFilterDirector.CallCount);
            Assert.NotSame(fixture.VersionSelectorManager.GetSupportedSilos(TestGrainType), firstInput);

            completion.SetResult(output);
            var result = await pending.WaitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(output, result);
            Assert.NotSame(output, result);
            Assert.Equal(1, fixture.SecondFilterDirector.CallCount);
            output[0] = silos[1];
            Assert.Equal(silos[2], result[0]);
        }

        [Fact]
        public async Task GetCompatibleSilosAsync_MembershipChangeDuringFilter_PreservesSnapshot()
        {
            var silos = CreateSilos(2);
            await using var fixture = new PlacementServiceFixture(activeSilos: silos, useFilter: true);
            var completion = new TaskCompletionSource<IReadOnlyList<SiloAddress>>(TaskCreationOptions.RunContinuationsAsynchronously);
            IReadOnlyList<SiloAddress>? input = null;
            fixture.FilterDirector.Callback = (_, candidates, _) =>
            {
                input = candidates;
                return completion.Task;
            };
            var pending = fixture.Target.GetCompatibleSilosAsync(CreatePlacementTarget(), TestContext.Current.CancellationToken);
            Assert.NotNull(input);
            fixture.ClusterManifestProvider.SetCurrent(CreateClusterManifest([silos[1]], useFilter: true, version: new MajorMinorVersion(1, 0)));
            fixture.SetActiveSilos(silos[1]);
            completion.SetResult(input);
            Assert.Equal(input, await pending.WaitAsync(TestContext.Current.CancellationToken));
            Assert.Contains(silos[0], input);
        }

        [Fact]
        public async Task GetCompatibleSilosWithVersions_DoesNotRunFilters()
        {
            await using var fixture = new PlacementServiceFixture(useFilter: true, interfaceVersion: 1);
            var target = new PlacementTarget(GrainId.Create(TestGrainType, "grain-1"), [], TestInterfaceType, 1);
            Assert.NotEmpty(fixture.Target.GetCompatibleSilosWithVersions(target));
            Assert.Equal(0, fixture.FilterDirector.CallCount);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task GetCompatibleSilosAsync_PropagatesFilterExceptions(bool asynchronous)
        {
            await using var fixture = new PlacementServiceFixture(useFilter: true, secondFilter: true);
            var exception = new InvalidOperationException("policy unavailable");
            fixture.FilterDirector.Callback = asynchronous
                ? async (_, _, _) =>
                {
                    await Task.Yield();
                    throw exception;
                }
                : (_, _, _) => throw exception;

            var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Target.GetCompatibleSilosAsync(CreatePlacementTarget(), TestContext.Current.CancellationToken));
            Assert.Same(exception, actual);
            Assert.Equal(0, fixture.SecondFilterDirector.CallCount);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task GetCompatibleSilosAsync_NullFilterResultFailsExplicitly(bool nullTask)
        {
            await using var fixture = new PlacementServiceFixture(useFilter: true, secondFilter: true);
            fixture.FilterDirector.Callback = (_, _, _) => nullTask ? null! : Task.FromResult<IReadOnlyList<SiloAddress>>(null!);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Target.GetCompatibleSilosAsync(CreatePlacementTarget(), TestContext.Current.CancellationToken));
            Assert.Contains("Placement filter 'TestPlacementFilterStrategy' returned a null", exception.Message);
            Assert.Equal(0, fixture.SecondFilterDirector.CallCount);
        }

        [Fact]
        public async Task GetCompatibleSilosAsync_EmptyResultPreservesDiagnostics()
        {
            await using var fixture = new PlacementServiceFixture(useFilter: true);
            fixture.FilterDirector.Callback = (_, _, _) => Task.FromResult<IReadOnlyList<SiloAddress>>([]);
            var exception = await Assert.ThrowsAsync<OrleansException>(() =>
                fixture.Target.GetCompatibleSilosAsync(CreatePlacementTarget(), TestContext.Current.CancellationToken));
            Assert.Contains("No active nodes are compatible with grain test", exception.Message);
            Assert.Contains("Known nodes with grain type:", exception.Message);
            Assert.Contains("All known nodes compatible with interface version:", exception.Message);
        }

        [Fact]
        public async Task GetOrPlaceActivationAsync_FilterReceivesTimeoutTokenWithoutDirectorForwarding()
        {
            var time = new FakeTimeProvider();
            var options = new SiloMessagingOptions { PlacementTimeout = TimeSpan.FromSeconds(10), PlacementMaxRetries = 0 };
            await using var fixture = new PlacementServiceFixture(useFilter: true, secondFilter: true, messagingOptions: options, timeProvider: time);
            var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            var completion = new TaskCompletionSource<IReadOnlyList<SiloAddress>>(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.FilterDirector.Callback = (_, _, token) =>
            {
                started.SetResult(token);
                return completion.Task;
            };

            var pending = GetTestAccessor(fixture.Target).GetOrPlaceActivationAsync(CreateMessage("timeout"));
            var token = await started.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.True(token.CanBeCanceled);
            time.Advance(options.PlacementTimeout);
            var exception = await Assert.ThrowsAsync<TimeoutException>(() => pending.WaitAsync(TestContext.Current.CancellationToken));
            Assert.IsType<Polly.Timeout.TimeoutRejectedException>(exception.InnerException);
            Assert.True(token.IsCancellationRequested);
            completion.SetResult([fixture.Target.LocalSilo]);
            Assert.Equal(0, fixture.SecondFilterDirector.CallCount);
            fixture.LocalGrainDirectory.DidNotReceive().AddOrUpdateCacheEntry(Arg.Any<GrainId>(), Arg.Any<SiloAddress>());
        }

        [Fact]
        public async Task GetOrPlaceActivationAsync_ShutdownCancelsFilterAndCompletesWorkers()
        {
            await using var fixture = new PlacementServiceFixture(useFilter: true);
            var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            var completion = new TaskCompletionSource<IReadOnlyList<SiloAddress>>(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.FilterDirector.Callback = (_, _, token) =>
            {
                started.SetResult(token);
                return completion.Task;
            };
            var pending = fixture.Target.AddressMessage(CreateMessage("shutdown"));
            var token = await started.Task.WaitAsync(TestContext.Current.CancellationToken);
            await StopAsync(fixture.Target, TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<SiloUnavailableException>(() => pending);
            Assert.True(token.IsCancellationRequested);
            Assert.All(GetTestAccessor(fixture.Target).WorkerTasks, task => Assert.True(task.IsCompleted));
            completion.SetResult([fixture.Target.LocalSilo]);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task LifecycleStop_ThrowingFilterCancellationCallback_CompletesWorkerTasks(bool messagePlacement)
        {
            await using var fixture = new PlacementServiceFixture(useFilter: true);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var completion = new TaskCompletionSource<IReadOnlyList<SiloAddress>>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationTokenRegistration registration = default;
            fixture.FilterDirector.Callback = (_, _, token) =>
            {
                registration = token.Register(() => throw new InvalidOperationException("policy cancellation failed"));
                started.SetResult();
                return completion.Task;
            };

            try
            {
                Task pending = messagePlacement
                    ? fixture.Target.AddressMessage(CreateMessage("throwing-callback"))
                    : fixture.Target.GetCompatibleSilosAsync(CreatePlacementTarget(), TestContext.Current.CancellationToken);
                await started.Task.WaitAsync(TestContext.Current.CancellationToken);
                await StopAsync(fixture.Target, TestContext.Current.CancellationToken);
                if (messagePlacement)
                {
                    await Assert.ThrowsAsync<SiloUnavailableException>(() => pending);
                }
                else
                {
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
                }

                Assert.All(GetTestAccessor(fixture.Target).WorkerTasks, task => Assert.True(task.IsCompleted));
            }
            finally
            {
                completion.TrySetResult([fixture.Target.LocalSilo]);
                registration.Dispose();
            }
        }

        [Fact]
        public async Task PlaceGrainAsync_CallerCancellationReachesFilter()
        {
            await using var fixture = new PlacementServiceFixture(useFilter: true, secondFilter: true);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            var completion = new TaskCompletionSource<IReadOnlyList<SiloAddress>>(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.FilterDirector.Callback = (_, _, token) =>
            {
                started.SetResult(token);
                return completion.Task;
            };
            var pending = fixture.Target.PlaceGrainAsync(GrainId.Create(TestGrainType, "migration"), [], new RandomPlacement(), cts.Token);
            var token = await started.Task.WaitAsync(TestContext.Current.CancellationToken);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.True(token.IsCancellationRequested);
            completion.SetResult([fixture.Target.LocalSilo]);
            Assert.Equal(0, fixture.SecondFilterDirector.CallCount);
        }

        [Fact]
        public async Task PlaceGrainAsync_QueryCancellationIsLinkedAndIsolated()
        {
            using var query = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var director = new CallbackPlacementDirector(async (target, context) =>
            {
                var token = target.GrainIdentity.Key.ToString() == "cancel" ? query.Token : default;
                var candidates = await context.GetCompatibleSilosAsync(target, token);
                return candidates[0];
            });
            await using var fixture = new PlacementServiceFixture(useFilter: true, placementDirector: director);
            var canceledStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            var otherStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            var completion = new TaskCompletionSource<IReadOnlyList<SiloAddress>>(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.FilterDirector.Callback = (target, _, token) =>
            {
                (target.GrainIdentity.Key.ToString() == "cancel" ? canceledStarted : otherStarted).SetResult(token);
                return completion.Task;
            };

            var canceled = fixture.Target.PlaceGrainAsync(GrainId.Create(TestGrainType, "cancel"), [], new RandomPlacement(), TestContext.Current.CancellationToken);
            var other = fixture.Target.PlaceGrainAsync(GrainId.Create(TestGrainType, "other"), [], new RandomPlacement(), TestContext.Current.CancellationToken);
            var canceledToken = await canceledStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
            var otherToken = await otherStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
            query.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
            Assert.True(canceledToken.IsCancellationRequested);
            Assert.False(otherToken.IsCancellationRequested);
            Assert.False(other.IsCompleted);
            completion.SetResult([fixture.Target.LocalSilo]);
            Assert.Equal(fixture.Target.LocalSilo, await other.WaitAsync(TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task AddressMessage_PendingFilterCoalescesSameGrainAndAllowsOtherGrains()
        {
            await using var fixture = new PlacementServiceFixture(useFilter: true);
            var first = CreateMessage("blocked");
            var duplicate = CreateMessage("blocked");
            var independent = Enumerable.Range(0, 256).Select(i => CreateMessage($"independent-{i}"))
                .First(message => message.TargetGrain.GetUniformHashCode() % 16 == first.TargetGrain.GetUniformHashCode() % 16);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var completion = new TaskCompletionSource<IReadOnlyList<SiloAddress>>(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.FilterDirector.Callback = (target, candidates, _) =>
            {
                if (target.GrainIdentity == first.TargetGrain)
                {
                    started.SetResult();
                    return completion.Task;
                }

                return Task.FromResult(candidates);
            };
            var firstPending = fixture.Target.AddressMessage(first);
            await started.Task.WaitAsync(TestContext.Current.CancellationToken);
            var duplicatePending = fixture.Target.AddressMessage(duplicate);
            await fixture.Target.AddressMessage(independent).WaitAsync(TestContext.Current.CancellationToken);
            Assert.False(firstPending.IsCompleted);
            Assert.False(duplicatePending.IsCompleted);
            Assert.Equal(2, fixture.FilterDirector.CallCount);
            completion.SetResult([fixture.Target.LocalSilo]);
            await Task.WhenAll(firstPending, duplicatePending).WaitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(first.TargetSilo, duplicate.TargetSilo);
            fixture.LocalGrainDirectory.Received(1).AddOrUpdateCacheEntry(first.TargetGrain, fixture.Target.LocalSilo);
        }

        [Theory]
        [InlineData(true, 2)]
        [InlineData(false, 1)]
        public async Task GetOrPlaceActivationAsync_FilterFailureUsesExistingRetryClassification(bool transient, int expectedCalls)
        {
            var options = new SiloMessagingOptions { PlacementMaxRetries = 1, PlacementRetryBaseDelay = TimeSpan.Zero };
            await using var fixture = new PlacementServiceFixture(useFilter: true, messagingOptions: options);
            Exception exception = transient ? new OrleansException("policy unavailable") : new InvalidOperationException("invalid policy");
            fixture.FilterDirector.Callback = (_, _, _) => Task.FromException<IReadOnlyList<SiloAddress>>(exception);
            var actual = await Assert.ThrowsAnyAsync<Exception>(() =>
                GetTestAccessor(fixture.Target).GetOrPlaceActivationAsync(CreateMessage("retry")));
            Assert.Same(exception, actual);
            Assert.Equal(expectedCalls, fixture.FilterDirector.CallCount);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task GetCompatibleSilosAsync_FilterActivitiesCoverAwaitAndCloseOnCancellation(bool cancel)
        {
            await using var fixture = new PlacementServiceFixture(useFilter: true, secondFilter: true);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            using var parent = new Activity("placement-filter-test").Start();
            var startedSpans = new ConcurrentQueue<Activity>();
            var stoppedSpans = new ConcurrentQueue<Activity>();
            using var listener = new ActivityListener
            {
                ShouldListenTo = source => source == ActivitySources.LifecycleGrainSource,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStarted = activity =>
                {
                    if (activity.ParentSpanId == parent.SpanId)
                    {
                        startedSpans.Enqueue(activity);
                    }
                },
                ActivityStopped = activity =>
                {
                    if (activity.ParentSpanId == parent.SpanId)
                    {
                        stoppedSpans.Enqueue(activity);
                    }
                },
            };
            ActivitySource.AddActivityListener(listener);
            var completion = new TaskCompletionSource<IReadOnlyList<SiloAddress>>(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.FilterDirector.Callback = (_, _, _) => completion.Task;

            var pending = fixture.Target.GetCompatibleSilosAsync(CreatePlacementTarget(), cts.Token);
            var firstSpan = Assert.Single(startedSpans);
            Assert.Equal(ActivityNames.FilterPlacementCandidates, firstSpan.OperationName);
            Assert.Equal(nameof(TestPlacementFilterStrategy), firstSpan.GetTagItem(ActivityTagKeys.PlacementFilterType));
            Assert.Equal(TestGrainType.ToString(), firstSpan.GetTagItem(ActivityTagKeys.GrainType));
            Assert.Empty(stoppedSpans);
            Assert.Same(parent, Activity.Current);
            if (cancel)
            {
                cts.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
                Assert.Single(stoppedSpans);
                Assert.Equal(0, fixture.SecondFilterDirector.CallCount);
            }
            else
            {
                completion.SetResult([fixture.Target.LocalSilo]);
                await pending.WaitAsync(TestContext.Current.CancellationToken);
                Assert.Equal(2, startedSpans.Count);
                Assert.Equal(2, stoppedSpans.Count);
                Assert.All(startedSpans, span => Assert.Equal(parent.SpanId, span.ParentSpanId));
            }

            Assert.Same(parent, Activity.Current);
            completion.TrySetResult([fixture.Target.LocalSilo]);
        }

        private static Message CreateMessage(string key) => new()
        {
            TargetGrain = GrainId.Create(TestGrainType, key),
            RequestContextData = [],
            InterfaceType = TestInterfaceType,
            InterfaceVersion = 0,
        };

        private static PlacementService CreateTarget()
        {
            return new PlacementServiceFixture().Target;
        }

        private static PlacementTarget CreatePlacementTarget(
            Dictionary<string, object>? requestContextData = null,
            GrainInterfaceType? interfaceType = null) =>
            new(
                GrainId.Create(TestGrainType, "grain-1"),
                requestContextData ?? new Dictionary<string, object>(),
                interfaceType ?? TestInterfaceType,
                0);

        private static PlacementService CreateTarget(
            IOptionsMonitor<SiloMessagingOptions> optionsMonitor,
            ILocalSiloDetails localSiloDetails,
            ISiloStatusOracle siloStatusOracle,
            TestClusterManifestProvider clusterManifestProvider,
            IServiceProvider serviceProvider,
            CachedVersionSelectorManager versionSelectorManager,
            GrainLocator grainLocator)
        {
            var grainVersionManifest = new GrainVersionManifest(clusterManifestProvider);
            var filterStrategyResolver = new PlacementFilterStrategyResolver(serviceProvider, new GrainPropertiesResolver(clusterManifestProvider));
            var placementFilterDirectorResolver = new PlacementFilterDirectorResolver(serviceProvider);

            return new PlacementService(
                optionsMonitor,
                localSiloDetails,
                siloStatusOracle,
                NullLoggerFactory.Instance.CreateLogger<PlacementService>(),
                grainLocator,
                grainInterfaceVersions: grainVersionManifest,
                versionSelectorManager,
                directorResolver: new PlacementDirectorResolver(serviceProvider),
                strategyResolver: new PlacementStrategyResolver(serviceProvider, [], new GrainPropertiesResolver(clusterManifestProvider)),
                filterStrategyResolver,
                placementFilterDirectorResolver,
                serviceProvider.GetRequiredService<Polly.Registry.ResiliencePipelineProvider<string>>());
        }

        private static SiloAddress[] CreateSilos(int count)
        {
            var result = new SiloAddress[count];
            for (var i = 0; i < count; i++)
            {
                result[i] = SiloAddress.New(IPAddress.Loopback, 11111, Interlocked.Increment(ref _siloGeneration));
            }

            return result;
        }

        private static ClusterMembershipSnapshot CreateMembershipSnapshot(
            long version,
            IReadOnlyDictionary<SiloAddress, SiloStatus> statuses)
        {
            var members = statuses.ToImmutableDictionary(
                static entry => entry.Key,
                static entry => new ClusterMember(entry.Key, entry.Value, entry.Key.ToString()));
            return new ClusterMembershipSnapshot(members, new MembershipVersion(version));
        }

        private static ClusterManifest CreateClusterManifest(
            SiloAddress[] silos,
            bool useFilter = false,
            MajorMinorVersion? version = null,
            ushort interfaceVersion = 0,
            bool secondFilter = false)
        {
            var manifest = CreateGrainManifest(useFilter, interfaceVersion, secondFilter);
            var manifests = silos.ToImmutableDictionary(silo => silo, _ => manifest);
            return new ClusterManifest(version ?? MajorMinorVersion.Zero, manifests);
        }

        private static GrainManifest CreateGrainManifest(bool useFilter, ushort interfaceVersion = 0, bool secondFilter = false)
        {
            var grainProperties = ImmutableDictionary.Create<string, string>(StringComparer.Ordinal);
            if (useFilter)
            {
                var filterName = typeof(TestPlacementFilterStrategy).Name;
                var secondFilterName = nameof(SecondTestPlacementFilterStrategy);
                grainProperties = grainProperties
                    .Add(WellKnownGrainTypeProperties.PlacementFilter, secondFilter ? $"{filterName},{secondFilterName}" : filterName)
                    .Add($"{WellKnownGrainTypeProperties.PlacementFilter}.{filterName}.order", "0");
                if (secondFilter)
                {
                    grainProperties = grainProperties.Add($"{WellKnownGrainTypeProperties.PlacementFilter}.{secondFilterName}.order", "1");
                }
            }

            return new GrainManifest(
                ImmutableDictionary.CreateRange(new[] { new KeyValuePair<GrainType, GrainProperties>(TestGrainType, new GrainProperties(grainProperties)) }),
                ImmutableDictionary.CreateRange(new[]
                {
                    new KeyValuePair<GrainInterfaceType, GrainInterfaceProperties>(
                        TestInterfaceType,
                        new GrainInterfaceProperties(
                            ImmutableDictionary.Create<string, string>(StringComparer.Ordinal)
                           .Add(WellKnownGrainInterfaceProperties.Version, interfaceVersion.ToString())))
                }));
        }

        private static CachedVersionSelectorManager CreateCachedVersionSelectorManager(GrainVersionManifest manifest)
        {
            var services = new ServiceCollection();
            services.AddOptions<GrainVersioningOptions>();
            services.AddKeyedSingleton<VersionSelectorStrategy, AllCompatibleVersions>(nameof(AllCompatibleVersions));
            services.AddKeyedSingleton<CompatibilityStrategy, BackwardCompatible>(nameof(BackwardCompatible));
            services.AddKeyedSingleton<IVersionSelector, AllCompatibleVersionsSelector>(typeof(AllCompatibleVersions));
            services.AddKeyedSingleton<ICompatibilityDirector, BackwardCompatilityDirector>(typeof(BackwardCompatible));
            var serviceProvider = services.BuildServiceProvider();
            var options = serviceProvider.GetRequiredService<IOptions<GrainVersioningOptions>>();

            return new CachedVersionSelectorManager(
                manifest,
                new VersionSelectorManager(serviceProvider, options),
                new CompatibilityDirectorManager(serviceProvider, options));
        }

        private static ServiceProvider CreateServiceProvider(
            TestPlacementFilterDirector? filterDirector = null,
            SiloMessagingOptions? messagingOptions = null,
            TimeProvider? timeProvider = null,
            TestPlacementFilterDirector? secondFilterDirector = null,
            IPlacementDirector? placementDirector = null)
        {
            messagingOptions ??= new SiloMessagingOptions();
            IServiceCollection services = new ServiceCollection();
            services.AddOptions<SiloMessagingOptions>().Configure(options =>
            {
                options.PlacementTimeout = messagingOptions.PlacementTimeout;
                options.PlacementMaxRetries = messagingOptions.PlacementMaxRetries;
                options.PlacementRetryBaseDelay = messagingOptions.PlacementRetryBaseDelay;
            });
            services.AddSingleton(timeProvider ?? TimeProvider.System);
            services.AddLogging();
            services.AddSingleton<PlacementStrategy>(new RandomPlacement());
            services.AddKeyedSingleton<IPlacementDirector>(typeof(RandomPlacement), placementDirector ?? new RandomPlacementDirector());
            OrleansRuntimeResiliencePolicies.AddOrleansRuntimeResiliencePolicies(services);
            if (filterDirector is not null)
            {
                services.Add(ServiceDescriptor.DescribeKeyed(
                    typeof(PlacementFilterStrategy),
                    typeof(TestPlacementFilterStrategy).Name,
                    typeof(TestPlacementFilterStrategy),
                    ServiceLifetime.Transient));
                services.AddKeyedSingleton<IPlacementFilterDirector>(typeof(TestPlacementFilterStrategy), filterDirector);
            }
            if (secondFilterDirector is not null)
            {
                services.AddKeyedTransient<PlacementFilterStrategy, SecondTestPlacementFilterStrategy>(nameof(SecondTestPlacementFilterStrategy));
                services.AddKeyedSingleton<IPlacementFilterDirector>(typeof(SecondTestPlacementFilterStrategy), secondFilterDirector);
            }

            return services.BuildServiceProvider();
        }

        private static GrainLocator CreateGrainLocator(
            IServiceProvider serviceProvider,
            TestClusterManifestProvider clusterManifestProvider,
            ILocalGrainDirectory localGrainDirectory)
        {
            var grainPropertiesResolver = new GrainPropertiesResolver(clusterManifestProvider);
            var grainDirectoryResolver = new GrainDirectoryResolver(serviceProvider, grainPropertiesResolver, []);
            var dhtGrainLocator = new DhtGrainLocator(localGrainDirectory, Substitute.For<IGrainContext>());
            var grainLocatorResolver = new GrainLocatorResolver(serviceProvider, grainDirectoryResolver, null!, dhtGrainLocator);
            return new GrainLocator(grainLocatorResolver, null!);
        }

        private static async Task<SiloLifecycleSubject> StartAsync(
            PlacementService target,
            CancellationToken cancellationToken)
        {
            var lifecycle = new SiloLifecycleSubject(NullLoggerFactory.Instance.CreateLogger<SiloLifecycleSubject>());
            ((ILifecycleParticipant<ISiloLifecycle>)target).Participate(lifecycle);
            await lifecycle.OnStart(cancellationToken);
            return lifecycle;
        }

        private static async Task StopAsync(PlacementService target, CancellationToken cancellationToken)
        {
            var lifecycle = await StartAsync(target, cancellationToken);
            await lifecycle.OnStop(cancellationToken);
        }

        private static PlacementService.ITestAccessor GetTestAccessor(PlacementService target) => target;

        private static async Task AssertWorkerStopEventsAsync(
            PlacementService target,
            DiagnosticEventCollector collector,
            CancellationToken cancellationToken)
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
                    TimeSpan.FromSeconds(10),
                    cancellationToken);

                stoppedEvents.Add(Assert.IsType<PlacementServiceEvents.WorkerStopped>(diagnosticEvent.Payload));
            }

            Assert.Equal(workerCount, stoppedEvents.Count);
        }

        private sealed class PlacementServiceFixture : IAsyncDisposable
        {
            private Dictionary<SiloAddress, SiloStatus> _siloStatuses = new();
            private long _membershipVersion;
            private readonly TestPlacementFilterDirector? _filterDirector;
            private readonly TestPlacementFilterDirector? _secondFilterDirector;

            public PlacementServiceFixture(
                SiloAddress[]? activeSilos = null,
                SiloAddress[]? manifestSilos = null,
                TestClusterManifestProvider? manifestProvider = null,
                bool useFilter = false,
                SiloStatus localSiloStatus = SiloStatus.Active,
                ushort interfaceVersion = 0,
                SiloMessagingOptions? messagingOptions = null,
                TimeProvider? timeProvider = null,
                ILocalGrainDirectory? localGrainDirectory = null,
                bool secondFilter = false,
                IPlacementDirector? placementDirector = null)
            {
                messagingOptions ??= new SiloMessagingOptions();
                activeSilos ??= CreateSilos(1);
                manifestSilos ??= activeSilos;
                SetActiveSilos(activeSilos);
                _siloStatuses[activeSilos[0]] = localSiloStatus;

                ClusterManifestProvider = manifestProvider ?? new TestClusterManifestProvider(CreateClusterManifest(manifestSilos, useFilter, interfaceVersion: interfaceVersion, secondFilter: secondFilter));
                _membershipVersion = ClusterManifestProvider.Current.Version.Major;
                ClusterMembershipService = new TestClusterMembershipService(CreateMembershipSnapshot(_membershipVersion, _siloStatuses));
                VersionSelectorManager = CreateCachedVersionSelectorManager(new GrainVersionManifest(ClusterManifestProvider));
                _filterDirector = useFilter ? new TestPlacementFilterDirector() : null;
                _secondFilterDirector = secondFilter ? new TestPlacementFilterDirector() : null;
                ServiceProvider = CreateServiceProvider(_filterDirector, messagingOptions, timeProvider, _secondFilterDirector, placementDirector);

                var optionsMonitor = Substitute.For<IOptionsMonitor<SiloMessagingOptions>>();
                optionsMonitor.CurrentValue.Returns(messagingOptions);

                var localSiloDetails = Substitute.For<ILocalSiloDetails>();
                localSiloDetails.SiloAddress.Returns(activeSilos[0]);

                SiloStatusOracle = Substitute.For<ISiloStatusOracle>();
                SiloStatusOracle.CurrentStatus.Returns(localSiloStatus);
                SiloStatusOracle.GetActiveSilos().Returns(_ => _siloStatuses
                    .Where(static entry => entry.Value == SiloStatus.Active)
                    .Select(static entry => entry.Key)
                    .ToArray());
                SiloStatusOracle.GetApproximateSiloStatuses(onlyActive: true).Returns(_ => _siloStatuses
                    .Where(static entry => entry.Value == SiloStatus.Active)
                    .ToDictionary());
                LocalGrainDirectory = localGrainDirectory ?? Substitute.For<ILocalGrainDirectory>();
                var grainLocator = CreateGrainLocator(ServiceProvider, ClusterManifestProvider, LocalGrainDirectory);
                Target = CreateTarget(
                    optionsMonitor,
                    localSiloDetails,
                    SiloStatusOracle,
                    ClusterManifestProvider,
                    ServiceProvider,
                    VersionSelectorManager,
                    grainLocator);
            }

            public PlacementService Target { get; }

            public ISiloStatusOracle SiloStatusOracle { get; }

            public TestClusterManifestProvider ClusterManifestProvider { get; }

            public TestClusterMembershipService ClusterMembershipService { get; }

            public ServiceProvider ServiceProvider { get; }

            public CachedVersionSelectorManager VersionSelectorManager { get; }

            public TestPlacementFilterDirector FilterDirector => _filterDirector ?? throw new InvalidOperationException("Filter not configured.");

            public TestPlacementFilterDirector SecondFilterDirector => _secondFilterDirector ?? throw new InvalidOperationException("Second filter not configured.");

            public ILocalGrainDirectory LocalGrainDirectory { get; }

            public async ValueTask DisposeAsync()
            {
                await StopAsync(Target, TestContext.Current.CancellationToken);
                await ServiceProvider.DisposeAsync();
            }

            public void SetActiveSilos(params SiloAddress[] silos)
            {
                _siloStatuses = silos.ToDictionary(silo => silo, _ => SiloStatus.Active);
                if (ClusterMembershipService is not null)
                {
                    ClusterMembershipService.Update(CreateMembershipSnapshot(++_membershipVersion, _siloStatuses));
                }
            }
        }

        private sealed class TestClusterManifestProvider : IClusterManifestProvider
        {
            private readonly Channel<ClusterManifest> _updates = Channel.CreateUnbounded<ClusterManifest>();
            private ClusterManifest _current;

            public TestClusterManifestProvider(ClusterManifest current)
            {
                _current = current;
                LocalGrainManifest = current.AllGrainManifests.FirstOrDefault() ?? CreateGrainManifest(useFilter: false);
            }

            public ClusterManifest Current => Volatile.Read(ref _current);

            public IAsyncEnumerable<ClusterManifest> Updates => ReadUpdates();

            public GrainManifest LocalGrainManifest { get; }

            public void Publish(ClusterManifest manifest)
            {
                SetCurrent(manifest);
                Assert.True(_updates.Writer.TryWrite(manifest));
            }

            public void SetCurrent(ClusterManifest manifest) => Volatile.Write(ref _current, manifest);

            private async IAsyncEnumerable<ClusterManifest> ReadUpdates([EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                yield return Current;

                await foreach (var manifest in _updates.Reader.ReadAllAsync(cancellationToken))
                {
                    yield return manifest;
                }
            }
        }

        public sealed class TestClusterMembershipService : IClusterMembershipService
        {
            private readonly AsyncEnumerable<ClusterMembershipSnapshot> _updates;
            private ClusterMembershipSnapshot _current;

            public TestClusterMembershipService(ClusterMembershipSnapshot current)
            {
                _current = current;
                _updates = new AsyncEnumerable<ClusterMembershipSnapshot>(
                    initialValue: current,
                    updateValidator: (previous, proposed) => proposed.Version > previous.Version,
                    onPublished: update => Volatile.Write(ref _current, update));
            }

            public ClusterMembershipSnapshot CurrentSnapshot => Volatile.Read(ref _current);

            public IAsyncEnumerable<ClusterMembershipSnapshot> MembershipUpdates => _updates;

            public void Update(ClusterMembershipSnapshot snapshot) => _updates.Publish(snapshot);

            public ValueTask Refresh(MembershipVersion minimumVersion = default, CancellationToken cancellationToken = default) => default;

            public Task<bool> TryKill(SiloAddress siloAddress) => Task.FromResult(false);
        }

        private sealed class TestPlacementFilterStrategy : PlacementFilterStrategy
        {
            public TestPlacementFilterStrategy()
                : base(0)
            {
            }
        }

        private sealed class TestPlacementFilterDirector : IPlacementFilterDirector
        {
            private int _callCount;

            public int CallCount => Volatile.Read(ref _callCount);

            public Func<PlacementTarget, IReadOnlyList<SiloAddress>, CancellationToken, Task<IReadOnlyList<SiloAddress>>>? Callback { get; set; }

            public Task<IReadOnlyList<SiloAddress>> FilterAsync(
                PlacementFilterStrategy filterStrategy,
                PlacementTarget target,
                IReadOnlyList<SiloAddress> silos,
                CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref _callCount);
                if (Callback is { } callback)
                {
                    return callback(target, silos, cancellationToken);
                }

                if (target.RequestContextData.TryGetValue("target-silo", out var value) && value is SiloAddress requestedSilo)
                {
                    return Task.FromResult<IReadOnlyList<SiloAddress>>(silos.Where(silo => silo.Equals(requestedSilo)).ToArray());
                }

                return Task.FromResult(silos);
            }
        }

        private sealed class SecondTestPlacementFilterStrategy() : PlacementFilterStrategy(1);

        private sealed class CallbackPlacementDirector(
            Func<PlacementTarget, IPlacementContext, Task<SiloAddress>> callback) : IPlacementDirector
        {
            public Task<SiloAddress> OnAddActivation(PlacementStrategy strategy, PlacementTarget target, IPlacementContext context) =>
                callback(target, context);
        }
    }
}
