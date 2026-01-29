using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Core.Internal;
using Orleans.Diagnostics;
using Orleans.Placement;
using Orleans.Runtime.Placement;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.General
{
    /// <summary>
    /// Failing test demonstrating missing activation tracing spans.
    /// Expects an activation Activity to be created on first grain activation.
    /// </summary>
    [Collection("ActivationTracing")]
    public class ActivationTracingTests : OrleansTestingBase, IClassFixture<ActivationTracingTests.Fixture>
    {
        private static readonly ConcurrentBag<Activity> Started = new();

        static ActivationTracingTests()
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

        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.Options.InitialSilosCount = 2; // Need 2 silos for migration tests
                builder.ConfigureHostConfiguration(TestDefaultConfiguration.ConfigureHostConfiguration);
                builder.AddSiloBuilderConfigurator<SiloCfg>();
                builder.AddClientBuilderConfigurator<ClientCfg>();
            }

            private class SiloCfg : ISiloConfigurator
            {
                public void Configure(ISiloBuilder hostBuilder)
                {
                    hostBuilder
                        .AddActivityPropagation()
                        .AddMemoryGrainStorageAsDefault()
                        .AddMemoryGrainStorage("PubSubStore");
                    hostBuilder.Services.AddPlacementFilter<TracingTestPlacementFilterStrategy, TracingTestPlacementFilterDirector>(ServiceLifetime.Singleton);
                }
            }

            private class ClientCfg : IClientBuilderConfigurator
            {
                public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
                {
                    clientBuilder.AddActivityPropagation();
                }
            }
        }

        private readonly Fixture _fixture;
        private readonly ITestOutputHelper _output;

        public ActivationTracingTests(Fixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture;
            _output = output;
        }

        [Fact]
        [TestCategory("BVT")]
        public async Task ActivationSpanIsCreatedOnFirstCall()
        {
            Started.Clear();

            using var parent = ActivitySources.ApplicationGrainSource.StartActivity("test-parent");
            parent?.Start();
            try
            {
                var grain = _fixture.GrainFactory.GetGrain<IActivityGrain>(Random.Shared.Next());
                // First call should force activation
                _ = await grain.GetActivityId();

                // Expect at least one activation-related activity
                var activationActivities = Started.Where(a => a.Source.Name == ActivitySources.LifecycleActivitySourceName).ToList();
                Assert.True(activationActivities.Count > 0, "Expected activation tracing activity to be created, but none were observed.");

                // Verify all expected spans are present and properly parented under test-parent
                var testParentTraceId = parent.TraceId.ToString();

                // Find the placement span - should be parented to the grain call which is parented to test-parent
                var placementSpan = Started.FirstOrDefault(a => a.OperationName == ActivityNames.PlaceGrain);
                Assert.NotNull(placementSpan);
                Assert.Equal(testParentTraceId, placementSpan.TraceId.ToString());

                // Find the placement filter span - should share the same trace ID as test-parent
                var placementFilterSpan = Started.FirstOrDefault(a => a.OperationName == ActivityNames.FilterPlacementCandidates);
                Assert.Null(placementFilterSpan);

                // Find the activation span - should be parented to the grain call which is parented to test-parent
                var activationSpan = Started.FirstOrDefault(a => a.OperationName == ActivityNames.ActivateGrain);
                Assert.NotNull(activationSpan);
                Assert.Equal(testParentTraceId, activationSpan.TraceId.ToString());

                // Find the OnActivateAsync span - should be parented to the activation span
                var onActivateSpan = Started.FirstOrDefault(a => a.OperationName == ActivityNames.OnActivate);
                Assert.Null(onActivateSpan);

                // Find the directory register span - should be parented to activation span
                var directoryRegisterSpan = Started.FirstOrDefault(a => a.OperationName == ActivityNames.RegisterDirectoryEntry);
                Assert.NotNull(directoryRegisterSpan);
                Assert.Equal(testParentTraceId, directoryRegisterSpan.TraceId.ToString());
                Assert.Equal(activationSpan.SpanId.ToString(), directoryRegisterSpan.ParentSpanId.ToString());
            }
            finally
            {
                parent.Stop();
                AssertNoApplicationSpansParentedByRuntimeSpans();
                PrintActivityDiagnostics();
            }
        }

        [Fact]
        [TestCategory("BVT")]
        public async Task ActivationSpanIncludesFilter()
        {
            Started.Clear();

            using var parent = ActivitySources.ApplicationGrainSource.StartActivity("test-parent-filter");
            parent?.Start();
            try
            {
                var grain = _fixture.GrainFactory.GetGrain<IFilteredActivityGrain>(Random.Shared.Next());
                // First call should force activation
                var _ = await grain.GetActivityId();

                // Expect at least one activation-related activity
                var activationActivities = Started.Where(a => a.Source.Name == ActivitySources.LifecycleActivitySourceName).ToList();
                Assert.True(activationActivities.Count > 0, "Expected activation tracing activity to be created, but none were observed.");

                // Verify all expected spans are present and properly parented under test-parent
                var testParentTraceId = parent.TraceId.ToString();

                // Find the placement span - should be parented to the grain call which is parented to test-parent
                var placementSpan = Started.FirstOrDefault(a => a.OperationName == ActivityNames.PlaceGrain);
                Assert.NotNull(placementSpan);
                Assert.Equal(testParentTraceId, placementSpan.TraceId.ToString());

                // Find the placement filter span - should share the same trace ID as test-parent
                var placementFilterSpan = Started.FirstOrDefault(a => a.OperationName == ActivityNames.FilterPlacementCandidates);
                Assert.NotNull(placementFilterSpan);
                Assert.Equal(testParentTraceId, placementFilterSpan.TraceId.ToString());
                Assert.Equal("TracingTestPlacementFilterStrategy", placementFilterSpan.Tags.FirstOrDefault(t => t.Key == "orleans.placement.filter.type").Value);

                // Find the activation span - should be parented to the grain call which is parented to test-parent
                var activationSpan = Started.FirstOrDefault(a => a.OperationName == ActivityNames.ActivateGrain);
                Assert.NotNull(activationSpan);
                Assert.Equal(testParentTraceId, activationSpan.TraceId.ToString());

                // Find the OnActivateAsync span - should be parented to the activation span
                var onActivateSpan = Started.FirstOrDefault(a => a.OperationName == ActivityNames.OnActivate);
                Assert.NotNull(onActivateSpan);
                Assert.Equal(testParentTraceId, onActivateSpan.TraceId.ToString());
                Assert.Equal(activationSpan.SpanId.ToString(), onActivateSpan.ParentSpanId.ToString());

                // Find the directory register span - should be parented to activation span
                var directoryRegisterSpan = Started.FirstOrDefault(a => a.OperationName == ActivityNames.RegisterDirectoryEntry);
                Assert.NotNull(directoryRegisterSpan);
                Assert.Equal(testParentTraceId, directoryRegisterSpan.TraceId.ToString());
                Assert.Equal(activationSpan.SpanId.ToString(), directoryRegisterSpan.ParentSpanId.ToString());
            }
            finally
            {
                parent.Stop();
                AssertNoApplicationSpansParentedByRuntimeSpans();
                PrintActivityDiagnostics();
            }
        }

        [Fact]
        [TestCategory("BVT")]
        public async Task PersistentStateReadSpanIsCreatedDuringActivation()
        {
            Started.Clear();

            using var parent = ActivitySources.ApplicationGrainSource.StartActivity("test-parent-storage");
            parent?.Start();
            try
            {
                var grain = _fixture.GrainFactory.GetGrain<IPersistentStateActivityGrain>(Random.Shared.Next());
                // First call should force activation which triggers state read
                var _ = await grain.GetActivityId();

                // Expect at least one activation-related activity
                var activationActivities = Started.Where(a => a.Source.Name == ActivitySources.LifecycleActivitySourceName).ToList();
                Assert.True(activationActivities.Count > 0, "Expected activation tracing activity to be created, but none were observed.");

                // Verify all expected spans are present and properly parented under test-parent
                var testParentTraceId = parent.TraceId.ToString();

                // Find the activation span - should be parented to the grain call which is parented to test-parent
                var activationSpan = Started.FirstOrDefault(a => a.OperationName == ActivityNames.ActivateGrain && a.Tags.First(kv => kv.Key == "orleans.grain.type").Value == "persistentstateactivity");
                Assert.NotNull(activationSpan);
                Assert.Equal(testParentTraceId, activationSpan.TraceId.ToString());

                // Find the storage read span - should share the same trace ID as test-parent
                var storageReadSpan = Started.FirstOrDefault(a => a.OperationName == ActivityNames.StorageRead);
                Assert.NotNull(storageReadSpan);
                Assert.Equal(testParentTraceId, storageReadSpan.TraceId.ToString());

                // Verify storage read span has expected tags
                Assert.Equal("MemoryGrainStorage", storageReadSpan.Tags.FirstOrDefault(t => t.Key == "orleans.storage.provider").Value);
                Assert.Equal("state", storageReadSpan.Tags.FirstOrDefault(t => t.Key == "orleans.storage.state.name").Value);
                Assert.Equal("PersistentStateActivityGrainState", storageReadSpan.Tags.FirstOrDefault(t => t.Key == "orleans.storage.state.type").Value);

                // Verify the grain ID tag is present
                var grainIdTag = storageReadSpan.Tags.FirstOrDefault(t => t.Key == "orleans.grain.id").Value;
                Assert.NotNull(grainIdTag);
            }
            finally
            {
                parent.Stop();
                AssertNoApplicationSpansParentedByRuntimeSpans();
                PrintActivityDiagnostics();
            }
        }

        /// <summary>
        /// Tests that dehydrate and rehydrate spans are created during grain migration.
        /// Verifies that the migration process creates proper tracing spans for both
        /// dehydration (on the source silo) and rehydration (on the target silo).
        /// </summary>
        [Fact]
        [TestCategory("BVT")]
        public async Task MigrationSpansAreCreatedDuringGrainMigration()
        {
            Started.Clear();

            using var parent = ActivitySources.ApplicationGrainSource.StartActivity("test-parent-migration");
            parent?.Start();
            try
            {
                // Create a grain and set some state
                var grain = _fixture.GrainFactory.GetGrain<IMigrationTracingTestGrain>(Random.Shared.Next());
                var expectedState = Random.Shared.Next();
                await grain.SetState(expectedState);
                var originalAddress = await grain.GetGrainAddress();
                var originalHost = originalAddress.SiloAddress;

                // Find a different silo to migrate to
                var targetHost = _fixture.HostedCluster.GetActiveSilos()
                    .Select(s => s.SiloAddress)
                    .First(address => address != originalHost);

                // Trigger migration with a placement hint to coerce the placement director to use the target silo
                RequestContext.Set(IPlacementDirector.PlacementHintKey, targetHost);
                await grain.Cast<IGrainManagementExtension>().MigrateOnIdle();

                // Verify the state was preserved (this also waits for migration to complete)
                var newState = await grain.GetState();
                Assert.Equal(expectedState, newState);

                // Give some time for all activities to complete
                await Task.Delay(500);

                var testParentTraceId = parent.TraceId.ToString();

                // Verify dehydrate span was created
                var dehydrateSpans = Started.Where(a => a.OperationName == ActivityNames.ActivationDehydrate).ToList();
                Assert.True(dehydrateSpans.Count > 0, "Expected at least one dehydrate span to be created during migration");

                var dehydrateSpan = dehydrateSpans.First();
                Assert.NotNull(dehydrateSpan.Tags.FirstOrDefault(t => t.Key == "orleans.grain.id").Value);
                Assert.NotNull(dehydrateSpan.Tags.FirstOrDefault(t => t.Key == "orleans.silo.id").Value);
                Assert.NotNull(dehydrateSpan.Tags.FirstOrDefault(t => t.Key == "orleans.activation.id").Value);
                // Verify target silo tag is present
                Assert.Equal(targetHost.ToString(), dehydrateSpan.Tags.FirstOrDefault(t => t.Key == "orleans.migration.target.silo").Value);
                // Verify dehydrate span is parented to the migration request trace
                Assert.Equal(testParentTraceId, dehydrateSpan.TraceId.ToString());

                // Verify rehydrate span was created on the target silo
                var rehydrateSpans = Started.Where(a => a.OperationName == ActivityNames.ActivationRehydrate).ToList();
                Assert.True(rehydrateSpans.Count > 0, "Expected at least one rehydrate span to be created during migration");

                var rehydrateSpan = rehydrateSpans.First();
                Assert.NotNull(rehydrateSpan.Tags.FirstOrDefault(t => t.Key == "orleans.grain.id").Value);
                Assert.NotNull(rehydrateSpan.Tags.FirstOrDefault(t => t.Key == "orleans.silo.id").Value);
                Assert.NotNull(rehydrateSpan.Tags.FirstOrDefault(t => t.Key == "orleans.activation.id").Value);
                // Verify the rehydrate span has the previous registration tag
                Assert.NotNull(rehydrateSpan.Tags.FirstOrDefault(t => t.Key == "orleans.rehydrate.previousRegistration").Value);
            }
            finally
            {
                parent?.Stop();
                AssertNoApplicationSpansParentedByRuntimeSpans();
                PrintActivityDiagnostics();
            }
        }

        /// <summary>
        /// Tests that dehydrate and rehydrate spans are created during migration of a grain with persistent state.
        /// Verifies that IPersistentState participates in migration and creates proper tracing spans.
        /// </summary>
        [Fact]
        [TestCategory("BVT")]
        public async Task MigrationSpansAreCreatedForGrainWithPersistentState()
        {
            Started.Clear();

            using var parent = ActivitySources.ApplicationGrainSource.StartActivity("test-parent-migration-persistent");
            parent?.Start();
            try
            {
                // Create a grain with persistent state and set some state
                var grain = _fixture.GrainFactory.GetGrain<IMigrationPersistentStateTracingTestGrain>(Random.Shared.Next());
                var expectedStateA = Random.Shared.Next();
                var expectedStateB = Random.Shared.Next();
                await grain.SetState(expectedStateA, expectedStateB);
                var originalAddress = await grain.GetGrainAddress();
                var originalHost = originalAddress.SiloAddress;

                // Find a different silo to migrate to
                var targetHost = _fixture.HostedCluster.GetActiveSilos()
                    .Select(s => s.SiloAddress)
                    .First(address => address != originalHost);

                // Trigger migration with a placement hint
                RequestContext.Set(IPlacementDirector.PlacementHintKey, targetHost);
                await grain.Cast<IGrainManagementExtension>().MigrateOnIdle();

                // Wait for migration to complete
                GrainAddress newAddress;
                do
                {
                    await Task.Delay(100);
                    newAddress = await grain.GetGrainAddress();
                } while (newAddress.ActivationId == originalAddress.ActivationId);

                // Verify the grain migrated to the target silo
                Assert.Equal(targetHost, newAddress.SiloAddress);

                // Verify the state was preserved
                var (actualA, actualB) = await grain.GetState();
                Assert.Equal(expectedStateA, actualA);
                Assert.Equal(expectedStateB, actualB);

                // Give some time for all activities to complete
                await Task.Delay(500);

                // Verify dehydrate span was NOT created (grain doesn't implement IGrainMigrationParticipant)
                var dehydrateSpans = Started.Where(a => a.OperationName == ActivityNames.ActivationDehydrate).ToList();
                Assert.True(dehydrateSpans.Count == 0, $"Expected no dehydrate spans for grain without IGrainMigrationParticipant, but found {dehydrateSpans.Count}");

                // Verify rehydrate span was NOT created
                var rehydrateSpans = Started.Where(a => a.OperationName == ActivityNames.ActivationRehydrate).ToList();
                Assert.True(rehydrateSpans.Count == 0, $"Expected no rehydrate spans for grain without IGrainMigrationParticipant, but found {rehydrateSpans.Count}");

                // Verify storage read span was NOT created during rehydration (state is transferred via migration context)
                // Note: Storage read should NOT happen during migration - the state is transferred in-memory
                var storageReadSpansAfterMigration = Started.Where(a => a.OperationName == ActivityNames.StorageRead).ToList();
                // During migration, storage should not be read because state is transferred via dehydration context
                // The storage read only happens on fresh activation, not on rehydration

                Assert.Equal(2, storageReadSpansAfterMigration.Count);
            }
            finally
            {
                parent.Stop();
                AssertNoApplicationSpansParentedByRuntimeSpans();
                PrintActivityDiagnostics();
            }
        }

        /// <summary>
        /// Tests that dehydrate and rehydrate spans are NOT created during migration of a grain
        /// that does not implement IGrainMigrationParticipant.
        /// </summary>
        [Fact]
        [TestCategory("BVT")]
        public async Task DehydrateAndRehydrateSpansAreNotCreatedForGrainWithoutMigrationParticipant()
        {
            Started.Clear();

            using var parent = ActivitySources.ApplicationGrainSource.StartActivity("test-parent-no-migration-participant");
            parent?.Start();
            try
            {
                // Create a grain that doesn't implement IGrainMigrationParticipant
                var grain = _fixture.GrainFactory.GetGrain<ISimpleMigrationTracingTestGrain>(Random.Shared.Next());
                var expectedState = Random.Shared.Next();
                await grain.SetState(expectedState);
                var originalAddress = await grain.GetGrainAddress();
                var originalHost = originalAddress.SiloAddress;

                // Find a different silo to migrate to
                var targetHost = _fixture.HostedCluster.GetActiveSilos()
                    .Select(s => s.SiloAddress)
                    .First(address => address != originalHost);

                // Trigger migration with a placement hint to coerce the placement director to use the target silo
                RequestContext.Set(IPlacementDirector.PlacementHintKey, targetHost);
                await grain.Cast<IGrainManagementExtension>().MigrateOnIdle();

                // Make a call to ensure grain is activated on target silo
                // Note: State won't be preserved since grain doesn't participate in migration
                _ = await grain.GetState();

                // Give some time for all activities to complete
                await Task.Delay(500);

                // Verify dehydrate span was NOT created (grain doesn't implement IGrainMigrationParticipant)
                var dehydrateSpans = Started.Where(a => a.OperationName == ActivityNames.ActivationDehydrate).ToList();
                Assert.True(dehydrateSpans.Count == 0, $"Expected no dehydrate spans for grain without IGrainMigrationParticipant, but found {dehydrateSpans.Count}");

                // Verify rehydrate span was NOT created
                var rehydrateSpans = Started.Where(a => a.OperationName == ActivityNames.ActivationRehydrate).ToList();
                Assert.True(rehydrateSpans.Count == 0, $"Expected no rehydrate spans for grain without IGrainMigrationParticipant, but found {rehydrateSpans.Count}");

                // Verify that activation span WAS created (the grain was still activated on the new silo)
                var activationSpans = Started.Where(a => a.OperationName == ActivityNames.ActivateGrain).ToList();
                Assert.True(activationSpans.Count > 0, "Expected at least one activation span for the migrated grain");
            }
            finally
            {
                parent?.Stop();
                AssertNoApplicationSpansParentedByRuntimeSpans();
                PrintActivityDiagnostics();
            }
        }

        /// <summary>
        /// Tests that appropriate tracing spans are created for IAsyncEnumerable grain calls with multiple elements.
        /// Verifies that:
        /// 1. A session span is created with the original method name (GetActivityDataStream)
        /// 2. StartEnumeration, MoveNext, and DisposeAsync spans are nested under the session span
        /// 3. All spans share the same trace context
        /// </summary>
        [Fact]
        [TestCategory("BVT")]
        public async Task AsyncEnumerableSpansAreCreatedForMultipleElements()
        {
            Started.Clear();

            using var parent = ActivitySources.ApplicationGrainSource.StartActivity("test-parent-async-enumerable");
            parent?.Start();
            try
            {
                var grain = _fixture.GrainFactory.GetGrain<IAsyncEnumerableActivityGrain>(Random.Shared.Next());
                const int elementCount = 5;

                var values = new List<ActivityData>();
                await foreach (var entry in grain.GetActivityDataStream(elementCount).WithBatchSize(1))
                {
                    values.Add(entry);
                }

                // Verify we received all elements
                Assert.Equal(elementCount, values.Count);

                // Verify all expected spans are present and properly parented under test-parent
                var testParentTraceId = parent.TraceId.ToString();
                var testParentSpanId = parent.SpanId.ToString();

                // Find all activities with the ApplicationGrainActivitySourceName
                var applicationSpans = Started.Where(a => a.Source.Name == ActivitySources.ApplicationGrainActivitySourceName).ToList();

                // Find the session span (the logical method call span)
                // This should have the method name from the grain interface (e.g., "IAsyncEnumerableActivityGrain/GetActivityDataStream")
                var sessionSpans = applicationSpans.Where(a => a.OperationName.Contains("GetActivityDataStream")).ToList();
                Assert.True(sessionSpans.Count >= 1, "Expected at least one session span with GetActivityDataStream operation name");

                var sessionSpan = sessionSpans.First();
                Assert.Equal(testParentTraceId, sessionSpan.TraceId.ToString());
                Assert.Equal(testParentSpanId, sessionSpan.ParentSpanId.ToString());

                // Verify the session span has the request ID tag
                var requestIdTag = sessionSpan.Tags.FirstOrDefault(t => t.Key == "orleans.async_enumerable.request_id").Value;
                Assert.NotNull(requestIdTag);

                var sessionSpanId = sessionSpan.SpanId.ToString();

                // Find all spans (including runtime spans) to verify parenting
                var allSpans = Started.ToList();

                // Find the StartEnumeration span - should be nested under the session span (in RuntimeActivitySourceName)
                // Filter to only client-side spans (those directly parented to the session span)
                var startEnumerationSpans = allSpans
                    .Where(a => a.OperationName.Contains("StartEnumeration") && a.ParentSpanId.ToString() == sessionSpanId)
                    .ToList();
                Assert.True(startEnumerationSpans.Count >= 1, "Expected at least one StartEnumeration span parented to session span");

                var startEnumerationSpan = startEnumerationSpans.First();
                Assert.Equal(testParentTraceId, startEnumerationSpan.TraceId.ToString());

                // Find MoveNext spans - should be nested under the session span (in RuntimeActivitySourceName)
                // Filter to only client-side spans (those directly parented to the session span)
                var moveNextSpans = allSpans
                    .Where(a => a.OperationName.Contains("MoveNext") && a.ParentSpanId.ToString() == sessionSpanId)
                    .ToList();
                Assert.True(moveNextSpans.Count >= 1, $"Expected at least one MoveNext span parented to session span, found {moveNextSpans.Count}");

                // All client-side MoveNext spans should share the same trace ID
                foreach (var moveNextSpan in moveNextSpans)
                {
                    Assert.Equal(testParentTraceId, moveNextSpan.TraceId.ToString());
                }

                // Find DisposeAsync span - should be nested under the session span (in RuntimeActivitySourceName)
                // Filter to only client-side spans (those directly parented to the session span)
                var disposeSpans = allSpans
                    .Where(a => a.OperationName.Contains("DisposeAsync") && a.ParentSpanId.ToString() == sessionSpanId)
                    .ToList();
                Assert.True(disposeSpans.Count >= 1, "Expected at least one DisposeAsync span parented to session span");

                var disposeSpan = disposeSpans.First();
                Assert.Equal(testParentTraceId, disposeSpan.TraceId.ToString());

                // Verify each ActivityData received has activity information
                // (verifying trace context was propagated into the grain during enumeration)
                foreach (var activityData in values)
                {
                    Assert.NotNull(activityData);
                    Assert.NotNull(activityData.Id);
                }
            }
            finally
            {
                parent?.Stop();
                AssertNoApplicationSpansParentedByRuntimeSpans();
                PrintActivityDiagnostics();
            }
        }

        /// <summary>
        /// Asserts that no spans from ApplicationGrainActivitySourceName have parents from RuntimeActivitySourceName.
        /// This ensures that if only ApplicationGrainActivitySourceName has been added (without RuntimeActivitySourceName),
        /// there won't be any hanging traces put at root because of missing RuntimeActivitySourceName spans
        /// that would otherwise propagate the trace context.
        /// </summary>
        private void AssertNoApplicationSpansParentedByRuntimeSpans()
        {
            var activities = Started.ToList();
            var activityById = activities
                .Where(a => a.Id is not null)
                .ToDictionary(a => a.Id!);

            var applicationSpans = activities
                .Where(a => a.Source.Name == ActivitySources.ApplicationGrainActivitySourceName)
                .ToList();

            var violations = new List<(Activity Child, Activity Parent)>();

            foreach (var appSpan in applicationSpans)
            {
                if (appSpan.ParentId is not null && activityById.TryGetValue(appSpan.ParentId, out var parentActivity))
                {
                    if (parentActivity.Source.Name == ActivitySources.RuntimeActivitySourceName)
                    {
                        violations.Add((appSpan, parentActivity));
                    }
                }
            }

            if (violations.Count > 0)
            {
                var sb = new StringBuilder();
                sb.AppendLine($"Found {violations.Count} ApplicationGrainActivitySourceName span(s) with RuntimeActivitySourceName parent(s):");
                foreach (var (child, violationParent) in violations)
                {
                    sb.AppendLine($"  - Application span '{child.OperationName}' (Id: {child.Id}) has Runtime parent '{violationParent.OperationName}' (Id: {violationParent.Id})");
                }
                Assert.Fail(sb.ToString());
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
            sb.AppendLine("╔══════════════════════════════════════════════════════════════════════════════╗");
            sb.AppendLine("║                         CAPTURED ACTIVITIES DIAGNOSTIC                       ║");
            sb.AppendLine("╠══════════════════════════════════════════════════════════════════════════════╣");
            sb.AppendLine($"║ Total Activities: {activities.Count,-59}║");
            sb.AppendLine("╚══════════════════════════════════════════════════════════════════════════════╝");
            sb.AppendLine();

            // Group by source
            var bySource = activities.GroupBy(a => a.Source.Name).OrderBy(g => g.Key);

            foreach (var sourceGroup in bySource)
            {
                sb.AppendLine($"┌─ Source: {sourceGroup.Key}");
                sb.AppendLine("│");

                var sourceActivities = sourceGroup.OrderBy(a => a.StartTimeUtc).ToList();
                for (int i = 0; i < sourceActivities.Count; i++)
                {
                    var activity = sourceActivities[i];
                    var isLast = i == sourceActivities.Count - 1;
                    var prefix = isLast ? "└──" : "├──";
                    var continuePrefix = isLast ? "   " : "│  ";

                    sb.AppendLine($"│ {prefix} [{activity.OperationName}]");
                    sb.AppendLine($"│ {continuePrefix}   ID: {activity.Id ?? "(null)"}");

                    if (activity.ParentId is not null)
                    {
                        sb.AppendLine($"│ {continuePrefix}   Parent: {activity.ParentId}");
                    }
                    else
                    {
                        sb.AppendLine($"│ {continuePrefix}   Parent: (root)");
                    }

                    sb.AppendLine($"│ {continuePrefix}   Duration: {activity.Duration.TotalMilliseconds:F2}ms");
                    sb.AppendLine($"│ {continuePrefix}   Status: {activity.Status}");

                    var tags = activity.Tags.ToList();
                    if (tags.Count > 0)
                    {
                        sb.AppendLine($"│ {continuePrefix}   Tags:");
                        foreach (var tag in tags)
                        {
                            sb.AppendLine($"│ {continuePrefix}     • {tag.Key}: {tag.Value}");
                        }
                    }

                    sb.AppendLine("│");
                }

                sb.AppendLine();
            }

            // Print hierarchy view
            sb.AppendLine("═══════════════════════════════════════════════════════════════════════════════");
            sb.AppendLine("                              ACTIVITY HIERARCHY                               ");
            sb.AppendLine("═══════════════════════════════════════════════════════════════════════════════");
            sb.AppendLine();

            var activityById = activities.Where(a => a.Id is not null).ToDictionary(a => a.Id!);
            var roots = activities.Where(a => a.ParentId is null || !activityById.ContainsKey(a.ParentId)).ToList();

            foreach (var root in roots.OrderBy(a => a.StartTimeUtc))
            {
                PrintActivityTree(sb, root, activityById, activities, "", true);
            }

            _output.WriteLine(sb.ToString());
        }

        private static void PrintActivityTree(
            StringBuilder sb,
            Activity activity,
            Dictionary<string, Activity> activityById,
            List<Activity> allActivities,
            string indent,
            bool isLast)
        {
            var marker = isLast ? "└── " : "├── ";
            var durationStr = activity.Duration.TotalMilliseconds > 0
                ? $" ({activity.Duration.TotalMilliseconds:F2}ms)"
                : "";

            sb.AppendLine($"{indent}{marker}[{activity.Source.Name}] {activity.OperationName}{durationStr}");

            var children = allActivities
                .Where(a => a.ParentId == activity.Id)
                .OrderBy(a => a.StartTimeUtc)
                .ToList();

            var childIndent = indent + (isLast ? "    " : "│   ");

            for (int i = 0; i < children.Count; i++)
            {
                PrintActivityTree(sb, children[i], activityById, allActivities, childIndent, i == children.Count - 1);
            }
        }
    }

    #region Test Placement Filter for Tracing

    /// <summary>
    /// Test placement filter attribute for tracing tests.
    /// </summary>
    public class TracingTestPlacementFilterAttribute() : PlacementFilterAttribute(new TracingTestPlacementFilterStrategy());

    /// <summary>
    /// Test placement filter strategy for tracing tests.
    /// </summary>
    public class TracingTestPlacementFilterStrategy() : PlacementFilterStrategy(order: 1)
    {
    }

    /// <summary>
    /// Test placement filter director that simply passes through all silos.
    /// </summary>
    public class TracingTestPlacementFilterDirector : IPlacementFilterDirector
    {
        public IEnumerable<SiloAddress> Filter(PlacementFilterStrategy filterStrategy, PlacementTarget target, IEnumerable<SiloAddress> silos)
        {
            return silos;
        }
    }

    /// <summary>
    /// Test grain interface with a placement filter for tracing tests.
    /// </summary>
    public interface IFilteredActivityGrain : IGrainWithIntegerKey
    {
        Task<ActivityData> GetActivityId();
    }

    /// <summary>
    /// Test grain implementation with a placement filter for tracing tests.
    /// </summary>
    [TracingTestPlacementFilter]
    public class FilteredActivityGrain : Grain, IFilteredActivityGrain
    {
        public Task<ActivityData> GetActivityId()
        {
            var activity = Activity.Current;
            if (activity is null)
            {
                return Task.FromResult(default(ActivityData));
            }

            var result = new ActivityData()
            {
                Id = activity.Id,
                TraceState = activity.TraceStateString,
                Baggage = activity.Baggage.ToList(),
            };

            return Task.FromResult(result);
        }
    }

    #endregion

    #region Test Grain with Persistent State for tracing

    /// <summary>
    /// Test grain interface with persistent state for tracing tests.
    /// </summary>
    public interface IPersistentStateActivityGrain : IGrainWithIntegerKey
    {
        Task<ActivityData> GetActivityId();
        Task<int> GetStateValue();
    }

    /// <summary>
    /// Test grain state for persistent state tracing tests.
    /// </summary>
    [GenerateSerializer]
    public class PersistentStateActivityGrainState
    {
        [Id(0)]
        public int Value { get; set; }
    }

    /// <summary>
    /// Test grain implementation with persistent state for tracing tests.
    /// </summary>
    [TracingTestPlacementFilter]
    public class PersistentStateActivityGrain : Grain, IPersistentStateActivityGrain
    {
        private readonly IPersistentState<PersistentStateActivityGrainState> _state;

        public PersistentStateActivityGrain(
            [PersistentState("state")] IPersistentState<PersistentStateActivityGrainState> state)
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

            var result = new ActivityData()
            {
                Id = activity.Id,
                TraceState = activity.TraceStateString,
                Baggage = activity.Baggage.ToList(),
            };

            return Task.FromResult(result);
        }

        public Task<int> GetStateValue()
        {
            return Task.FromResult(_state.State.Value);
        }
    }

    #endregion

    #region Test Grain for Migration Tracing

    /// <summary>
    /// Test grain interface for migration tracing tests.
    /// </summary>
    public interface IMigrationTracingTestGrain : IGrainWithIntegerKey
    {
        ValueTask<GrainAddress> GetGrainAddress();
        ValueTask SetState(int state);
        ValueTask<int> GetState();
    }

    /// <summary>
    /// Test grain state for migration tracing tests.
    /// </summary>
    [GenerateSerializer]
    public class MigrationTracingTestGrainState
    {
        [Id(0)]
        public int Value { get; set; }
    }

    /// <summary>
    /// Test grain implementation for migration tracing tests.
    /// Implements IGrainMigrationParticipant to participate in migration.
    /// Uses RandomPlacement to allow migration to different silos.
    /// </summary>
    [RandomPlacement]
    public class MigrationTracingTestGrain : Grain, IMigrationTracingTestGrain, IGrainMigrationParticipant
    {
        private int _state;

        public ValueTask<int> GetState() => new(_state);

        public ValueTask SetState(int state)
        {
            _state = state;
            return default;
        }

        public void OnDehydrate(IDehydrationContext migrationContext)
        {
            migrationContext.TryAddValue("state", _state);
        }

        public void OnRehydrate(IRehydrationContext migrationContext)
        {
            migrationContext.TryGetValue("state", out _state);
        }

        public ValueTask<GrainAddress> GetGrainAddress() => new(GrainContext.Address);
    }

    /// <summary>
    /// Test grain interface for migration tracing tests without IGrainMigrationParticipant.
    /// </summary>
    public interface ISimpleMigrationTracingTestGrain : IGrainWithIntegerKey
    {
        ValueTask<GrainAddress> GetGrainAddress();
        ValueTask SetState(int state);
        ValueTask<int> GetState();
    }

    /// <summary>
    /// Test grain implementation for migration tracing tests that does NOT implement IGrainMigrationParticipant.
    /// Uses RandomPlacement to allow migration to different silos.
    /// This grain will lose its state during migration since it doesn't participate in dehydration/rehydration.
    /// </summary>
    [RandomPlacement]
    public class SimpleMigrationTracingTestGrain : Grain, ISimpleMigrationTracingTestGrain
    {
        private int _state;

        public ValueTask<int> GetState() => new(_state);

        public ValueTask SetState(int state)
        {
            _state = state;
            return default;
        }

        public ValueTask<GrainAddress> GetGrainAddress() => new(GrainContext.Address);
    }

    /// <summary>
    /// Test grain interface with persistent state for migration tracing tests.
    /// </summary>
    public interface IMigrationPersistentStateTracingTestGrain : IGrainWithIntegerKey
    {
        ValueTask SetState(int a, int b);
        ValueTask<(int A, int B)> GetState();
        ValueTask<GrainAddress> GetGrainAddress();
    }

    /// <summary>
    /// Test grain implementation with IPersistentState for migration tracing tests.
    /// Uses RandomPlacement to allow migration to different silos.
    /// </summary>
    [RandomPlacement]
    public class MigrationPersistentStateTracingTestGrain : Grain, IMigrationPersistentStateTracingTestGrain
    {
        private readonly IPersistentState<MigrationTracingTestGrainState> _stateA;
        private readonly IPersistentState<MigrationTracingTestGrainState> _stateB;

        public MigrationPersistentStateTracingTestGrain(
            [PersistentState("a")] IPersistentState<MigrationTracingTestGrainState> stateA,
            [PersistentState("b")] IPersistentState<MigrationTracingTestGrainState> stateB)
        {
            _stateA = stateA;
            _stateB = stateB;
        }

        public ValueTask<(int A, int B)> GetState() => new((_stateA.State.Value, _stateB.State.Value));

        public ValueTask SetState(int a, int b)
        {
            _stateA.State.Value = a;
            _stateB.State.Value = b;
            return default;
        }

        public ValueTask<GrainAddress> GetGrainAddress() => new(GrainContext.Address);
    }

    #endregion
}
