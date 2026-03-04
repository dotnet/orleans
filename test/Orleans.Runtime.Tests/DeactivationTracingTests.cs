using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Orleans.Core.Internal;
using Orleans.Diagnostics;
using Orleans.Placement;
using Orleans.Runtime.Placement;
using Orleans.Storage;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.General
{
    /// <summary>
    /// Tests for verifying OnDeactivateAsync tracing spans are correctly created
    /// during grain deactivation across various scenarios.
    /// </summary>
    [Collection("ActivationTracing")]
    public class DeactivationTracingTests : OrleansTestingBase, IClassFixture<ActivationTracingTests.Fixture>
    {
        private static readonly ConcurrentBag<Activity> Started = new();

        static DeactivationTracingTests()
        {
            var listener = new ActivityListener
            {
                ShouldListenTo = src => src.Name == ActivitySources.ApplicationGrainActivitySourceName
                                        || src.Name == ActivitySources.LifecycleActivitySourceName
                                        || src.Name == ActivitySources.StorageActivitySourceName,
                Sample = (ref _) => ActivitySamplingResult.AllData,
                SampleUsingParentId = (ref _) => ActivitySamplingResult.AllData,
                ActivityStarted = activity => Started.Add(activity),
            };
            ActivitySource.AddActivityListener(listener);
        }

        private readonly ActivationTracingTests.Fixture _fixture;
        private readonly ITestOutputHelper _output;

        public DeactivationTracingTests(ActivationTracingTests.Fixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture;
            _output = output;
        }

        [Fact]
        [TestCategory("BVT")]
        public async Task OnDeactivateSpanIsCreatedOnDeactivateOnIdle()
        {
            Started.Clear();

            using var parent = ActivitySources.ApplicationGrainSource.StartActivity("test-parent-deactivate");
            parent?.Start();
            try
            {
                var grain = _fixture.GrainFactory.GetGrain<IDeactivationTracingTestGrain>(Random.Shared.Next());
                var grainId = grain.GetGrainId();

                _ = await grain.GetActivityId();

                var testParentTraceId = parent.TraceId.ToString();
                Started.Clear();

                await grain.TriggerDeactivation();
                await _fixture.HostedCluster.WaitForDeactivationAsync(grainId);

                _ = await grain.GetActivityId();

                var onDeactivateSpans = Started.Where(a => a.OperationName == ActivityNames.OnDeactivate).ToList();
                Assert.True(onDeactivateSpans.Count > 0, "Expected at least one OnDeactivate span to be created during deactivation");

                var onDeactivateSpan = onDeactivateSpans.First();

                Assert.NotNull(onDeactivateSpan.Tags.FirstOrDefault(t => t.Key == "orleans.grain.id").Value);
                Assert.NotNull(onDeactivateSpan.Tags.FirstOrDefault(t => t.Key == "orleans.grain.type").Value);
                Assert.NotNull(onDeactivateSpan.Tags.FirstOrDefault(t => t.Key == "orleans.silo.id").Value);
                Assert.NotNull(onDeactivateSpan.Tags.FirstOrDefault(t => t.Key == "orleans.activation.id").Value);

                var deactivationReasonTag = onDeactivateSpan.Tags.FirstOrDefault(t => t.Key == "orleans.deactivation.reason").Value;
                Assert.NotNull(deactivationReasonTag);
                Assert.Contains("ApplicationRequested", deactivationReasonTag);

                Assert.Equal(testParentTraceId, onDeactivateSpan.TraceId.ToString());
            }
            finally
            {
                parent?.Stop();
                PrintActivityDiagnostics();
            }
        }

        [Fact]
        [TestCategory("BVT")]
        public async Task OnDeactivateSpanIncludesStorageWriteDuringDeactivation()
        {
            Started.Clear();

            using var parent = ActivitySources.ApplicationGrainSource.StartActivity("test-parent-deactivate-storage");
            parent?.Start();
            try
            {
                var grain = _fixture.GrainFactory.GetGrain<IDeactivationWithWorkTracingTestGrain>(Random.Shared.Next());
                var grainId = grain.GetGrainId();

                _ = await grain.GetActivityId();
                Started.Clear();

                await grain.TriggerDeactivation();
                await _fixture.HostedCluster.WaitForDeactivationAsync(grainId);

                var wasDeactivated = await grain.WasDeactivated();
                Assert.True(wasDeactivated, "Expected grain to have been deactivated");

                var onDeactivateSpans = Started.Where(a => a.OperationName == ActivityNames.OnDeactivate).ToList();
                Assert.True(onDeactivateSpans.Count > 0, "Expected at least one OnDeactivate span to be created during deactivation");

                var storageWriteSpans = Started.Where(a => a.OperationName == ActivityNames.StorageWrite).ToList();
                Assert.True(storageWriteSpans.Count > 0, "Expected at least one storage write span to be created during OnDeactivateAsync");

                var storageWriteSpan = storageWriteSpans.First();
                Assert.Equal("MemoryGrainStorage", storageWriteSpan.Tags.FirstOrDefault(t => t.Key == "orleans.storage.provider").Value);
            }
            finally
            {
                parent?.Stop();
                PrintActivityDiagnostics();
            }
        }

        [Fact]
        [TestCategory("BVT")]
        public async Task OnDeactivateSpanCapturesExceptionDuringDeactivation()
        {
            Started.Clear();

            using var parent = ActivitySources.ApplicationGrainSource.StartActivity("test-parent-deactivate-exception");
            parent?.Start();
            try
            {
                var grain = _fixture.GrainFactory.GetGrain<IDeactivationWithExceptionTracingTestGrain>(Random.Shared.Next());
                var grainId = grain.GetGrainId();

                _ = await grain.GetActivityId();
                Started.Clear();

                await grain.TriggerDeactivation();
                await _fixture.HostedCluster.WaitForDeactivationAsync(grainId);

                _ = await grain.GetActivityId();

                var onDeactivateSpans = Started.Where(a => a.OperationName == ActivityNames.OnDeactivate).ToList();
                Assert.True(onDeactivateSpans.Count > 0, "Expected at least one OnDeactivate span to be created during deactivation");

                var onDeactivateSpan = onDeactivateSpans.First();
                Assert.Equal(ActivityStatusCode.Error, onDeactivateSpan.Status);
                Assert.Equal("on-deactivate-failed", onDeactivateSpan.StatusDescription);
            }
            finally
            {
                parent?.Stop();
                PrintActivityDiagnostics();
            }
        }

        [Fact]
        [TestCategory("BVT")]
        public async Task OnDeactivateSpanPrecedesDehydrateDuringMigration()
        {
            Started.Clear();

            using var parent = ActivitySources.ApplicationGrainSource.StartActivity("test-parent-deactivate-migrate");
            parent?.Start();
            try
            {
                var grain = _fixture.GrainFactory.GetGrain<IDeactivationMigrationTracingTestGrain>(Random.Shared.Next());
                var grainId = grain.GetGrainId();
                var expectedState = Random.Shared.Next();
                await grain.SetState(expectedState);
                var originalAddress = await grain.GetGrainAddress();
                var originalHost = originalAddress.SiloAddress;

                var targetHost = _fixture.HostedCluster.GetActiveSilos()
                    .Select(s => s.SiloAddress)
                    .First(address => address != originalHost);

                Started.Clear();

                var deactivated = _fixture.HostedCluster.WaitForDeactivationAsync(grainId);
                RequestContext.Set(IPlacementDirector.PlacementHintKey, targetHost);
                await grain.Cast<IGrainManagementExtension>().MigrateOnIdle();

                await deactivated;

                var newState = await grain.GetState();
                Assert.Equal(expectedState, newState);

                var testParentTraceId = parent.TraceId.ToString();

                var onDeactivateSpans = Started.Where(a => a.OperationName == ActivityNames.OnDeactivate).ToList();
                Assert.True(onDeactivateSpans.Count > 0, "Expected at least one OnDeactivate span during migration");

                var dehydrateSpans = Started.Where(a => a.OperationName == ActivityNames.ActivationDehydrate).ToList();
                Assert.True(dehydrateSpans.Count > 0, "Expected at least one dehydrate span during migration");

                var onDeactivateSpan = onDeactivateSpans.First();
                var dehydrateSpan = dehydrateSpans.First();

                Assert.True(onDeactivateSpan.StartTimeUtc <= dehydrateSpan.StartTimeUtc,
                    "OnDeactivateAsync should start before or at the same time as Dehydrate");

                Assert.NotNull(onDeactivateSpan.Tags.FirstOrDefault(t => t.Key == "orleans.grain.id").Value);
                Assert.Contains("Migrating", onDeactivateSpan.Tags.FirstOrDefault(t => t.Key == "orleans.deactivation.reason").Value);

                Assert.NotNull(dehydrateSpan.Tags.FirstOrDefault(t => t.Key == "orleans.grain.id").Value);
                Assert.Equal(targetHost.ToString(), dehydrateSpan.Tags.FirstOrDefault(t => t.Key == "orleans.migration.target.silo").Value);
            }
            finally
            {
                parent?.Stop();
                PrintActivityDiagnostics();
            }
        }

        [Fact]
        [TestCategory("BVT")]
        public async Task OnDeactivateSpanIsNotCreatedForNonGrainBaseGrain()
        {
            Started.Clear();

            using var parent = ActivitySources.ApplicationGrainSource.StartActivity("test-parent-no-deactivate");
            parent?.Start();
            try
            {
                var grain = _fixture.GrainFactory.GetGrain<IActivityGrain>(Random.Shared.Next());

                _ = await grain.GetActivityId();
                Started.Clear();

                await grain.Cast<IGrainManagementExtension>().DeactivateOnIdle();

                _ = await grain.GetActivityId();

                var onDeactivateSpans = Started.Where(a => a.OperationName == ActivityNames.OnDeactivate).ToList();

                Assert.True(onDeactivateSpans.Count == 0,
                    $"Expected no OnDeactivate spans for grain not implementing IGrainBase, but found {onDeactivateSpans.Count}");
            }
            finally
            {
                parent?.Stop();
                PrintActivityDiagnostics();
            }
        }

        [Fact]
        [TestCategory("BVT")]
        public async Task OnDeactivateSpanInheritsTraceContextFromTriggeringCall()
        {
            Started.Clear();

            using var parent = ActivitySources.ApplicationGrainSource.StartActivity("test-parent-trace-context");
            parent?.Start();
            try
            {
                var grain = _fixture.GrainFactory.GetGrain<IDeactivationTracingTestGrain>(Random.Shared.Next());
                var grainId = grain.GetGrainId();

                _ = await grain.GetActivityId();

                var testParentTraceId = parent.TraceId.ToString();

                await grain.TriggerDeactivation();
                await _fixture.HostedCluster.WaitForDeactivationAsync(grainId);

                _ = await grain.GetActivityId();

                var onDeactivateSpans = Started.Where(a => a.OperationName == ActivityNames.OnDeactivate).ToList();
                Assert.True(onDeactivateSpans.Count > 0, "Expected at least one OnDeactivate span");

                var onDeactivateSpan = onDeactivateSpans.First();
                Assert.Equal(testParentTraceId, onDeactivateSpan.TraceId.ToString());
            }
            finally
            {
                parent?.Stop();
                PrintActivityDiagnostics();
            }
        }

        [Fact]
        [TestCategory("BVT")]
        public async Task OnDeactivateSpanIsParentedToAsyncEnumerableMethodCall()
        {
            Started.Clear();

            using var parent = ActivitySources.ApplicationGrainSource.StartActivity("test-parent-async-enum-deactivate");
            parent?.Start();
            try
            {
                var grain = _fixture.GrainFactory.GetGrain<IAsyncEnumerableDeactivationGrain>(Random.Shared.Next());
                var grainId = grain.GetGrainId();
                var testParentTraceId = parent.TraceId.ToString();
                const int elementCount = 3;

                var deactivated = _fixture.HostedCluster.WaitForDeactivationAsync(grainId);
                var values = new List<int>();
                await foreach (var value in grain.GetValuesAndDeactivate(elementCount).WithBatchSize(1))
                {
                    values.Add(value);
                }

                Assert.Equal(elementCount, values.Count);

                await deactivated;

                _ = await grain.GetActivityId();

                var sessionSpans = Started
                    .Where(a => a.Source.Name == ActivitySources.ApplicationGrainActivitySourceName
                               && a.OperationName.Contains("GetValuesAndDeactivate"))
                    .ToList();
                Assert.True(sessionSpans.Count >= 1, "Expected at least one session span with GetValuesAndDeactivate operation name");

                var sessionSpan = sessionSpans.First();
                Assert.Equal(testParentTraceId, sessionSpan.TraceId.ToString());

                var onDeactivateSpans = Started.Where(a => a.OperationName == ActivityNames.OnDeactivate).ToList();
                Assert.True(onDeactivateSpans.Count > 0, "Expected at least one OnDeactivate span to be created during enumeration");

                var onDeactivateSpan = onDeactivateSpans.First();
                Assert.Equal(testParentTraceId, onDeactivateSpan.TraceId.ToString());
                Assert.Equal(sessionSpan.TraceId, onDeactivateSpan.TraceId);

                var deactivationReasonTag = onDeactivateSpan.Tags.FirstOrDefault(t => t.Key == "orleans.deactivation.reason").Value;
                Assert.NotNull(deactivationReasonTag);
                Assert.Contains("ApplicationRequested", deactivationReasonTag);
            }
            finally
            {
                parent?.Stop();
                PrintActivityDiagnostics();
            }
        }

        [Fact]
        [TestCategory("BVT")]
        public async Task OnDeactivateSpanHasCorrectReasonTagForMigration()
        {
            Started.Clear();

            using var parent = ActivitySources.ApplicationGrainSource.StartActivity("test-parent-reason-migration");
            parent?.Start();
            try
            {
                var grain = _fixture.GrainFactory.GetGrain<IDeactivationMigrationTracingTestGrain>(Random.Shared.Next());
                var grainId = grain.GetGrainId();
                var testParentTraceId = parent.TraceId.ToString();
                await grain.SetState(42);
                var originalAddress = await grain.GetGrainAddress();
                var originalHost = originalAddress.SiloAddress;

                var targetHost = _fixture.HostedCluster.GetActiveSilos()
                    .Select(s => s.SiloAddress)
                    .First(address => address != originalHost);

                Started.Clear();

                var deactivated = _fixture.HostedCluster.WaitForDeactivationAsync(grainId);
                RequestContext.Set(IPlacementDirector.PlacementHintKey, targetHost);
                await grain.Cast<IGrainManagementExtension>().MigrateOnIdle();

                await deactivated;

                var onDeactivateSpans = Started.Where(a => a.OperationName == ActivityNames.OnDeactivate).ToList();
                Assert.True(onDeactivateSpans.Count > 0, "Expected at least one OnDeactivate span during migration");

                var onDeactivateSpan = onDeactivateSpans.First();

                var deactivationReasonTag = onDeactivateSpan.Tags.FirstOrDefault(t => t.Key == "orleans.deactivation.reason").Value;
                Assert.NotNull(deactivationReasonTag);
                Assert.Contains("Migrating", deactivationReasonTag);

                Assert.Equal(testParentTraceId, onDeactivateSpan.TraceId.ToString());
            }
            finally
            {
                parent?.Stop();
                PrintActivityDiagnostics();
            }
        }

        [Fact]
        [TestCategory("BVT")]
        public async Task OnDeactivateSpanIsCreatedForInconsistentStateException()
        {
            Started.Clear();

            using var parent = ActivitySources.ApplicationGrainSource.StartActivity("test-parent-inconsistent-state");
            parent?.Start();
            try
            {
                var grain = _fixture.GrainFactory.GetGrain<IInconsistentStateDeactivationGrain>(Random.Shared.Next());
                var grainId = grain.GetGrainId();

                _ = await grain.GetActivityId();

                var deactivated = _fixture.HostedCluster.WaitForDeactivationAsync(grainId);
                try
                {
                    await grain.ThrowInconsistentStateException();
                }
                catch (InconsistentStateException)
                {
                    // Expected
                }

                await deactivated;

                _ = await grain.GetActivityId();

                var onDeactivateSpans = Started.Where(a => a.OperationName == ActivityNames.OnDeactivate).ToList();
                Assert.True(onDeactivateSpans.Count > 0, "Expected at least one OnDeactivate span to be created during deactivation");

                var onDeactivateSpan = onDeactivateSpans.First();

                Assert.NotNull(onDeactivateSpan.Tags.FirstOrDefault(t => t.Key == "orleans.grain.id").Value);
                Assert.NotNull(onDeactivateSpan.Tags.FirstOrDefault(t => t.Key == "orleans.grain.type").Value);

                var deactivationReasonTag = onDeactivateSpan.Tags.FirstOrDefault(t => t.Key == "orleans.deactivation.reason").Value;
                Assert.NotNull(deactivationReasonTag);
                Assert.Contains("ApplicationError", deactivationReasonTag);

                Assert.NotEqual(default(ActivityTraceId).ToString(), onDeactivateSpan.TraceId.ToString());
            }
            finally
            {
                parent?.Stop();
                PrintActivityDiagnostics();
            }
        }

        [Fact]
        [TestCategory("BVT")]
        public async Task OnDeactivateSpanIsNotCreatedForActivationFailure()
        {
            Started.Clear();

            using var parent = ActivitySources.ApplicationGrainSource.StartActivity("test-parent-activation-failure");
            parent?.Start();
            try
            {
                var grain = _fixture.GrainFactory.GetGrain<IActivationFailureDeactivationGrain>(Random.Shared.Next());

                Started.Clear();

                try
                {
                    await grain.GetActivityId();
                }
                catch
                {
                    // Expected - activation fails
                }

                // Wait briefly for any deactivation to complete
                await Task.Delay(500);

                var onDeactivateSpans = Started.Where(a => a.OperationName == ActivityNames.OnDeactivate).ToList();
                Assert.True(onDeactivateSpans.Count == 0,
                    $"Expected no OnDeactivate spans for grain that failed during activation, but found {onDeactivateSpans.Count}");

                var activationSpans = Started.Where(a => a.OperationName == ActivityNames.ActivateGrain).ToList();
                Assert.True(activationSpans.Count > 0, "Expected at least one activation span");
            }
            finally
            {
                parent?.Stop();
                PrintActivityDiagnostics();
            }
        }

        [Fact]
        [TestCategory("BVT")]
        public async Task OnDeactivateSpanIsCreatedForGrainContextDeactivate()
        {
            Started.Clear();

            using var parent = ActivitySources.ApplicationGrainSource.StartActivity("test-parent-grain-context-deactivate");
            parent?.Start();
            try
            {
                var grain = _fixture.GrainFactory.GetGrain<IGrainContextDeactivationGrain>(Random.Shared.Next());
                var grainId = grain.GetGrainId();
                var testParentTraceId = parent.TraceId.ToString();

                _ = await grain.GetActivityId();
                Started.Clear();

                await grain.DeactivateWithCustomReason("Custom deactivation reason for testing");
                await _fixture.HostedCluster.WaitForDeactivationAsync(grainId);

                _ = await grain.GetActivityId();

                var onDeactivateSpans = Started.Where(a => a.OperationName == ActivityNames.OnDeactivate).ToList();
                Assert.True(onDeactivateSpans.Count > 0, "Expected at least one OnDeactivate span to be created during deactivation");

                var onDeactivateSpan = onDeactivateSpans.First();

                Assert.NotNull(onDeactivateSpan.Tags.FirstOrDefault(t => t.Key == "orleans.grain.id").Value);
                Assert.NotNull(onDeactivateSpan.Tags.FirstOrDefault(t => t.Key == "orleans.grain.type").Value);

                var deactivationReasonTag = onDeactivateSpan.Tags.FirstOrDefault(t => t.Key == "orleans.deactivation.reason").Value;
                Assert.NotNull(deactivationReasonTag);
                Assert.Contains("ApplicationRequested", deactivationReasonTag);
                Assert.Contains("Custom deactivation reason for testing", deactivationReasonTag);

                Assert.Equal(testParentTraceId, onDeactivateSpan.TraceId.ToString());
            }
            finally
            {
                parent?.Stop();
                PrintActivityDiagnostics();
            }
        }

        [Fact]
        [TestCategory("BVT")]
        public async Task OnDeactivateSpanHasCorrectParentWhenTriggeredExternally()
        {
            Started.Clear();

            using var parent = ActivitySources.ApplicationGrainSource.StartActivity("test-parent-external-deactivate");
            parent?.Start();
            try
            {
                var grain = _fixture.GrainFactory.GetGrain<IDeactivationTracingTestGrain>(Random.Shared.Next());
                var grainId = grain.GetGrainId();
                var testParentTraceId = parent.TraceId.ToString();

                _ = await grain.GetActivityId();
                Started.Clear();

                await grain.Cast<IGrainManagementExtension>().DeactivateOnIdle();
                await _fixture.HostedCluster.WaitForDeactivationAsync(grainId);

                _ = await grain.GetActivityId();

                var onDeactivateSpans = Started.Where(a => a.OperationName == ActivityNames.OnDeactivate).ToList();
                Assert.True(onDeactivateSpans.Count > 0, "Expected at least one OnDeactivate span to be created during deactivation");

                var onDeactivateSpan = onDeactivateSpans.First();

                Assert.Equal(testParentTraceId, onDeactivateSpan.TraceId.ToString());

                var deactivationReasonTag = onDeactivateSpan.Tags.FirstOrDefault(t => t.Key == "orleans.deactivation.reason").Value;
                Assert.NotNull(deactivationReasonTag);
                Assert.Contains("ApplicationRequested", deactivationReasonTag);
            }
            finally
            {
                parent?.Stop();
                PrintActivityDiagnostics();
            }
        }

        private void PrintActivityDiagnostics()
        {
            var activities = Started.ToList();
            if (activities.Count == 0)
            {
                _output.WriteLine("No activities captured.");
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("=== CAPTURED ACTIVITIES ===");
            sb.AppendLine($"Total: {activities.Count}");
            sb.AppendLine();

            foreach (var activity in activities.OrderBy(a => a.StartTimeUtc))
            {
                sb.AppendLine($"[{activity.Source.Name}] {activity.OperationName}");
                sb.AppendLine($"  ID: {activity.Id}");
                sb.AppendLine($"  TraceId: {activity.TraceId}");
                sb.AppendLine($"  ParentId: {activity.ParentId}");
                sb.AppendLine($"  Duration: {activity.Duration.TotalMilliseconds:F2}ms");
                sb.AppendLine($"  Status: {activity.Status}");
                sb.AppendLine();
            }

            _output.WriteLine(sb.ToString());
        }
    }

    /// <summary>
    /// Test grain interface for basic deactivation tracing tests.
    /// </summary>
    public interface IDeactivationTracingTestGrain : IGrainWithIntegerKey
    {
        Task<ActivityData> GetActivityId();
        Task TriggerDeactivation();
    }

    /// <summary>
    /// Test grain implementation for basic deactivation tracing tests.
    /// Implements a simple OnDeactivateAsync to verify the span is created.
    /// </summary>
    public class DeactivationTracingTestGrain : Grain, IDeactivationTracingTestGrain
    {
        public Task<ActivityData> GetActivityId()
        {
            var activity = Activity.Current;
            if (activity is null)
            {
                return Task.FromResult(default(ActivityData));
            }

            return Task.FromResult(new ActivityData
            {
                Id = activity.Id,
                TraceState = activity.TraceStateString,
                Baggage = activity.Baggage.ToList(),
            });
        }

        public Task TriggerDeactivation()
        {
            this.DeactivateOnIdle();
            return Task.CompletedTask;
        }

        public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Test grain interface for deactivation tracing with work in OnDeactivateAsync.
    /// </summary>
    public interface IDeactivationWithWorkTracingTestGrain : IGrainWithIntegerKey
    {
        Task<ActivityData> GetActivityId();
        Task TriggerDeactivation();
        Task<bool> WasDeactivated();
    }

    /// <summary>
    /// Test grain state for tracking deactivation.
    /// </summary>
    [GenerateSerializer]
    public class DeactivationWorkState
    {
        [Id(0)]
        public bool WasDeactivated { get; set; }

        [Id(1)]
        public string DeactivationReason { get; set; }
    }

    /// <summary>
    /// Test grain implementation that performs work during OnDeactivateAsync.
    /// Uses persistent state to track that deactivation occurred.
    /// </summary>
    public class DeactivationWithWorkTracingTestGrain : Grain, IDeactivationWithWorkTracingTestGrain
    {
        private readonly IPersistentState<DeactivationWorkState> _state;

        public DeactivationWithWorkTracingTestGrain(
            [PersistentState("deactivationState")] IPersistentState<DeactivationWorkState> state)
        {
            _state = state;
        }

        public Task<ActivityData> GetActivityId()
        {
            var activity = Activity.Current;
            if (activity is null)
            {
                return Task.FromResult(default(ActivityData));
            }

            return Task.FromResult(new ActivityData
            {
                Id = activity.Id,
                TraceState = activity.TraceStateString,
                Baggage = activity.Baggage.ToList(),
            });
        }

        public Task TriggerDeactivation()
        {
            this.DeactivateOnIdle();
            return Task.CompletedTask;
        }

        public Task<bool> WasDeactivated() => Task.FromResult(_state.State.WasDeactivated);

        public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            _state.State.WasDeactivated = true;
            _state.State.DeactivationReason = reason.ToString();
            await _state.WriteStateAsync();
        }
    }

    /// <summary>
    /// Test grain interface for deactivation tracing with exception in OnDeactivateAsync.
    /// </summary>
    public interface IDeactivationWithExceptionTracingTestGrain : IGrainWithIntegerKey
    {
        Task<ActivityData> GetActivityId();
        Task TriggerDeactivation();
    }

    /// <summary>
    /// Test grain implementation that throws an exception during OnDeactivateAsync.
    /// Used to verify that the OnDeactivate span captures errors correctly.
    /// </summary>
    public class DeactivationWithExceptionTracingTestGrain : Grain, IDeactivationWithExceptionTracingTestGrain
    {
        public Task<ActivityData> GetActivityId()
        {
            var activity = Activity.Current;
            if (activity is null)
            {
                return Task.FromResult(default(ActivityData));
            }

            return Task.FromResult(new ActivityData
            {
                Id = activity.Id,
                TraceState = activity.TraceStateString,
                Baggage = activity.Baggage.ToList(),
            });
        }

        public Task TriggerDeactivation()
        {
            this.DeactivateOnIdle();
            return Task.CompletedTask;
        }

        public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Simulated error during deactivation");
        }
    }

    /// <summary>
    /// Test grain interface for deactivation tracing with migration participant.
    /// </summary>
    public interface IDeactivationMigrationTracingTestGrain : IGrainWithIntegerKey
    {
        ValueTask<GrainAddress> GetGrainAddress();
        ValueTask SetState(int state);
        ValueTask<int> GetState();
        ValueTask TriggerDeactivation();
    }

    /// <summary>
    /// Test grain implementation that implements IGrainMigrationParticipant for deactivation tracing.
    /// Used to verify OnDeactivate span is created before dehydration during migration.
    /// </summary>
    [RandomPlacement]
    public class DeactivationMigrationTracingTestGrain : Grain, IDeactivationMigrationTracingTestGrain, IGrainMigrationParticipant
    {
        private int _state;
        private bool _onDeactivateCalled;

        public ValueTask<int> GetState() => new(_state);

        public ValueTask SetState(int state)
        {
            _state = state;
            return default;
        }

        public ValueTask TriggerDeactivation()
        {
            this.DeactivateOnIdle();
            return default;
        }

        public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            _onDeactivateCalled = true;
            return Task.CompletedTask;
        }

        public void OnDehydrate(IDehydrationContext migrationContext)
        {
            migrationContext.TryAddValue("state", _state);
            migrationContext.TryAddValue("onDeactivateCalled", _onDeactivateCalled);
        }

        public void OnRehydrate(IRehydrationContext migrationContext)
        {
            migrationContext.TryGetValue("state", out _state);
            migrationContext.TryGetValue("onDeactivateCalled", out _onDeactivateCalled);
        }

        public ValueTask<GrainAddress> GetGrainAddress() => new(GrainContext.Address);
    }

    /// <summary>
    /// Test grain interface for InconsistentStateException deactivation tracing tests.
    /// </summary>
    public interface IInconsistentStateDeactivationGrain : IGrainWithIntegerKey
    {
        Task<ActivityData> GetActivityId();
        Task ThrowInconsistentStateException();
    }

    /// <summary>
    /// Test grain implementation that throws InconsistentStateException.
    /// Used to verify OnDeactivate span is created with ApplicationError reason.
    /// </summary>
    public class InconsistentStateDeactivationGrain : Grain, IInconsistentStateDeactivationGrain
    {
        public Task<ActivityData> GetActivityId()
        {
            var activity = Activity.Current;
            if (activity is null)
            {
                return Task.FromResult(default(ActivityData));
            }

            return Task.FromResult(new ActivityData
            {
                Id = activity.Id,
                TraceState = activity.TraceStateString,
                Baggage = activity.Baggage.ToList(),
            });
        }

        public Task ThrowInconsistentStateException()
        {
            throw new InconsistentStateException("Simulated inconsistent state for testing deactivation tracing")
            {
                IsSourceActivation = true
            };
        }

        public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Test grain interface for activation failure deactivation tracing tests.
    /// </summary>
    public interface IActivationFailureDeactivationGrain : IGrainWithIntegerKey
    {
        Task<ActivityData> GetActivityId();
    }

    /// <summary>
    /// Test grain implementation that fails during activation.
    /// Used to verify OnDeactivate span is NOT created when PreviousState != Valid.
    /// </summary>
    public class ActivationFailureDeactivationGrain : Grain, IActivationFailureDeactivationGrain
    {
        public ActivationFailureDeactivationGrain()
        {
            // Throw exception in constructor to fail activation
            throw new InvalidOperationException("Simulated activation failure for testing deactivation tracing");
        }

        public Task<ActivityData> GetActivityId()
        {
            var activity = Activity.Current;
            if (activity is null)
            {
                return Task.FromResult(default(ActivityData));
            }

            return Task.FromResult(new ActivityData
            {
                Id = activity.Id,
                TraceState = activity.TraceStateString,
                Baggage = activity.Baggage.ToList(),
            });
        }

        public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            // This should never be called since activation fails
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Test grain interface for GrainContext.Deactivate deactivation tracing tests.
    /// </summary>
    public interface IGrainContextDeactivationGrain : IGrainWithIntegerKey
    {
        Task<ActivityData> GetActivityId();
        Task DeactivateWithCustomReason(string reason);
    }

    /// <summary>
    /// Test grain implementation that uses GrainContext.Deactivate with custom reason.
    /// Used to verify OnDeactivate span is created with the custom reason.
    /// </summary>
    public class GrainContextDeactivationGrain : Grain, IGrainContextDeactivationGrain
    {
        public Task<ActivityData> GetActivityId()
        {
            var activity = Activity.Current;
            if (activity is null)
            {
                return Task.FromResult(default(ActivityData));
            }

            return Task.FromResult(new ActivityData
            {
                Id = activity.Id,
                TraceState = activity.TraceStateString,
                Baggage = activity.Baggage.ToList(),
            });
        }

        public Task DeactivateWithCustomReason(string reason)
        {
            GrainContext.Deactivate(new DeactivationReason(DeactivationReasonCode.ApplicationRequested, reason));
            return Task.CompletedTask;
        }

        public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }



    /// <summary>
    /// Test grain interface for IAsyncEnumerable deactivation tracing tests.
    /// </summary>
    public interface IAsyncEnumerableDeactivationGrain : IGrainWithIntegerKey
    {
        IAsyncEnumerable<int> GetValuesAndDeactivate(int count);
        Task<ActivityData> GetActivityId();
    }


    /// <summary>
    /// Test grain implementation that yields values via IAsyncEnumerable and then deactivates after DisposeAsync.
    /// Uses a grain call filter to trigger deactivation after the async enumerable is disposed.
    /// </summary>
    public class AsyncEnumerableDeactivationGrain : Grain, IAsyncEnumerableDeactivationGrain
    {
        public async IAsyncEnumerable<int> GetValuesAndDeactivate(int count)
        {
            for (int i = 0; i < count; i++)
            {
                await Task.Delay(10);
                yield return i;
            }
        }

        public Task<ActivityData> GetActivityId()
        {
            var activity = Activity.Current;
            if (activity is null)
            {
                return Task.FromResult(default(ActivityData));
            }

            return Task.FromResult(new ActivityData
            {
                Id = activity.Id,
                TraceState = activity.TraceStateString,
                Baggage = activity.Baggage.ToList(),
            });
        }

        public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

}
