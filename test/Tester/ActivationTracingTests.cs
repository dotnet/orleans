using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Core.Internal;
using Orleans.Diagnostics;
using Orleans.Placement;
using Orleans.Runtime.Placement;
using Orleans.Storage;
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
#pragma warning disable ORLEANSEXP003 // Type is for evaluation purposes only and is subject to change or removal in future updates.
                public void Configure(ISiloBuilder hostBuilder)
                {
                    hostBuilder
                        .AddActivityPropagation()
                        .AddDistributedGrainDirectory()
                        .AddMemoryGrainStorageAsDefault()
                        .AddMemoryGrainStorage("PubSubStore")
                        .AddIncomingGrainCallFilter<DeactivateAfterDisposeAsyncFilter>();
                    hostBuilder.Services.AddPlacementFilter<TracingTestPlacementFilterStrategy, TracingTestPlacementFilterDirector>(ServiceLifetime.Singleton);
                    hostBuilder.Services.AddPlacementFilter<SecondTracingTestPlacementFilterStrategy, SecondTracingTestPlacementFilterDirector>(ServiceLifetime.Singleton);
                }
#pragma warning restore ORLEANSEXP003
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
        public async Task ActivationSpanIncludesMultipleFilters()
        {
            Started.Clear();

            using var parent = ActivitySources.ApplicationGrainSource.StartActivity("test-parent-multi-filter");
            parent?.Start();
            try
            {
                var grain = _fixture.GrainFactory.GetGrain<IMultiFilteredActivityGrain>(Random.Shared.Next());
                // First call should force activation
                _ = await grain.GetActivityId();

                // Verify all expected spans are present and properly parented under test-parent
                var testParentTraceId = parent.TraceId.ToString();
                var testParentSpanId = parent.SpanId.ToString();

                // Find the placement span
                var placementSpan = Started.FirstOrDefault(a => a.OperationName == ActivityNames.PlaceGrain);
                Assert.NotNull(placementSpan);
                Assert.Equal(testParentTraceId, placementSpan.TraceId.ToString());

                // Find ALL placement filter spans - should be 2 (one for each filter)
                var placementFilterSpans = Started
                    .Where(a => a.OperationName == ActivityNames.FilterPlacementCandidates)
                    .OrderBy(a => a.StartTimeUtc)
                    .ToList();
                Assert.Equal(2, placementFilterSpans.Count);

                // Both filter spans should share the same trace ID as test-parent
                foreach (var filterSpan in placementFilterSpans)
                {
                    Assert.Equal(testParentTraceId, filterSpan.TraceId.ToString());
                    // Each filter span should be parented directly to the PlaceGrain span
                    Assert.Equal(placementSpan.SpanId.ToString(), filterSpan.ParentSpanId.ToString());
                }

                // Verify that both filters were executed
                var filterTypes = placementFilterSpans
                    .Select(span => span.Tags.FirstOrDefault(t => t.Key == "orleans.placement.filter.type").Value)
                    .ToHashSet();
                Assert.Contains("TracingTestPlacementFilterStrategy", filterTypes);
                Assert.Contains("SecondTracingTestPlacementFilterStrategy", filterTypes);
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
                _ = await grain.GetActivityId();

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
        /// Tests that FilterPlacementCandidates spans are properly parented under a PlaceGrain span
        /// when migration triggers placement via PlaceGrainAsync.
        /// This covers the code path where PlaceGrainAsync (not the PlacementWorker message path)
        /// calls a placement director which calls GetCompatibleSilos with filters.
        /// </summary>
        [Fact]
        [TestCategory("BVT")]
        public async Task MigrationPlacementFilterSpanIsParentedUnderPlaceGrainSpan()
        {
            Started.Clear();

            using var parent = ActivitySources.ApplicationGrainSource.StartActivity("test-parent-migration-filter");
            parent?.Start();
            try
            {
                // Create a grain that has both a placement filter and migration support
                var grain = _fixture.GrainFactory.GetGrain<IMigrationFilterTracingTestGrain>(Random.Shared.Next());
                var expectedState = Random.Shared.Next();
                await grain.SetState(expectedState);
                var originalAddress = await grain.GetGrainAddress();
                var originalHost = originalAddress.SiloAddress;

                // Find a different silo to migrate to
                var targetHost = _fixture.HostedCluster.GetActiveSilos()
                    .Select(s => s.SiloAddress)
                    .First(address => address != originalHost);

                // Clear activities to focus on migration placement
                Started.Clear();

                // Trigger migration with a placement hint
                RequestContext.Set(IPlacementDirector.PlacementHintKey, targetHost);
                await grain.Cast<IGrainManagementExtension>().MigrateOnIdle();

                // Verify the state was preserved (this also waits for migration to complete)
                var newState = await grain.GetState();
                Assert.Equal(expectedState, newState);

                // Give some time for all activities to complete
                await Task.Delay(500);

                var testParentTraceId = parent.TraceId.ToString();

                // Find the PlaceGrain span created during migration's PlaceGrainAsync call
                var placementSpans = Started.Where(a => a.OperationName == ActivityNames.PlaceGrain).ToList();
                Assert.True(placementSpans.Count > 0, "Expected at least one PlaceGrain span during migration");

                var placementSpan = placementSpans.First();
                Assert.Equal(testParentTraceId, placementSpan.TraceId.ToString());

                // Find the FilterPlacementCandidates span - should share the same trace ID
                var filterSpans = Started.Where(a => a.OperationName == ActivityNames.FilterPlacementCandidates).ToList();
                Assert.True(filterSpans.Count > 0, "Expected at least one FilterPlacementCandidates span during migration with filter");

                var filterSpan = filterSpans.First();
                Assert.Equal(testParentTraceId, filterSpan.TraceId.ToString());
                Assert.Equal("TracingTestPlacementFilterStrategy", filterSpan.Tags.FirstOrDefault(t => t.Key == "orleans.placement.filter.type").Value);

                // The filter span should be a child of the PlaceGrain span
                Assert.Equal(placementSpan.SpanId.ToString(), filterSpan.ParentSpanId.ToString());
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
        /// Tests that OnDeactivateAsync span is created when a grain is deactivated via DeactivateOnIdle.
        /// Verifies that the span has proper tags including grain ID, type, silo ID, and deactivation reason.
        /// </summary>
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

                // First call should force activation
                _ = await grain.GetActivityId();

                // Trigger deactivation - capture the trace ID before clearing
                var testParentTraceId = parent.TraceId.ToString();

                // Clear activities to focus on deactivation
                Started.Clear();

                // Trigger deactivation
                await grain.TriggerDeactivation();

                // Wait for deactivation to complete - make a call to ensure grain is reactivated (which confirms deactivation happened)
                await Task.Delay(500);

                // Make another call to force a new activation (confirming the previous one was deactivated)
                _ = await grain.GetActivityId();

                // Find the OnDeactivate span
                var onDeactivateSpans = Started.Where(a => a.OperationName == ActivityNames.OnDeactivate).ToList();
                Assert.True(onDeactivateSpans.Count > 0, "Expected at least one OnDeactivate span to be created during deactivation");

                var onDeactivateSpan = onDeactivateSpans.First();

                // Verify the span has expected tags
                Assert.NotNull(onDeactivateSpan.Tags.FirstOrDefault(t => t.Key == "orleans.grain.id").Value);
                Assert.NotNull(onDeactivateSpan.Tags.FirstOrDefault(t => t.Key == "orleans.grain.type").Value);
                Assert.NotNull(onDeactivateSpan.Tags.FirstOrDefault(t => t.Key == "orleans.silo.id").Value);
                Assert.NotNull(onDeactivateSpan.Tags.FirstOrDefault(t => t.Key == "orleans.activation.id").Value);

                // Verify deactivation reason tag
                var deactivationReasonTag = onDeactivateSpan.Tags.FirstOrDefault(t => t.Key == "orleans.deactivation.reason").Value;
                Assert.NotNull(deactivationReasonTag);
                Assert.Contains("ApplicationRequested", deactivationReasonTag);

                // Verify the OnDeactivate span shares the same trace ID as the parent activity
                // This confirms the activity context was propagated from the TriggerDeactivation call
                Assert.Equal(testParentTraceId, onDeactivateSpan.TraceId.ToString());
            }
            finally
            {
                parent?.Stop();
                AssertNoApplicationSpansParentedByRuntimeSpans();
                PrintActivityDiagnostics();
            }
        }

        /// <summary>
        /// Tests that OnDeactivateAsync span captures state writes performed during deactivation.
        /// Verifies that storage operations during OnDeactivateAsync are properly traced.
        /// </summary>
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

                // First call should force activation
                _ = await grain.GetActivityId();

                // Clear activities to focus on deactivation
                Started.Clear();

                // Trigger deactivation
                await grain.TriggerDeactivation();

                // Wait for deactivation to complete
                await Task.Delay(500);

                // Make another call to force a new activation (confirming the previous one was deactivated)
                var wasDeactivated = await grain.WasDeactivated();
                Assert.True(wasDeactivated, "Expected grain to have been deactivated");

                // Find the OnDeactivate span
                var onDeactivateSpans = Started.Where(a => a.OperationName == ActivityNames.OnDeactivate).ToList();
                Assert.True(onDeactivateSpans.Count > 0, "Expected at least one OnDeactivate span to be created during deactivation");

                // Find storage write span - should have been created during OnDeactivateAsync
                var storageWriteSpans = Started.Where(a => a.OperationName == ActivityNames.StorageWrite).ToList();
                Assert.True(storageWriteSpans.Count > 0, "Expected at least one storage write span to be created during OnDeactivateAsync");

                var storageWriteSpan = storageWriteSpans.First();
                Assert.Equal("MemoryGrainStorage", storageWriteSpan.Tags.FirstOrDefault(t => t.Key == "orleans.storage.provider").Value);
            }
            finally
            {
                parent?.Stop();
                AssertNoApplicationSpansParentedByRuntimeSpans();
                PrintActivityDiagnostics();
            }
        }

        /// <summary>
        /// Tests that OnDeactivateAsync span captures exceptions thrown during deactivation.
        /// Verifies that the span's error status is set and the exception event is recorded.
        /// </summary>
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

                // First call should force activation
                _ = await grain.GetActivityId();

                // Clear activities to focus on deactivation
                Started.Clear();

                // Trigger deactivation (grain throws exception in OnDeactivateAsync)
                await grain.TriggerDeactivation();

                // Wait for deactivation to complete
                await Task.Delay(500);

                // Make another call to force a new activation
                _ = await grain.GetActivityId();

                // Find the OnDeactivate span
                var onDeactivateSpans = Started.Where(a => a.OperationName == ActivityNames.OnDeactivate).ToList();
                Assert.True(onDeactivateSpans.Count > 0, "Expected at least one OnDeactivate span to be created during deactivation");

                var onDeactivateSpan = onDeactivateSpans.First();

                // Verify the span captured the error
                Assert.Equal(ActivityStatusCode.Error, onDeactivateSpan.Status);

                // Verify the span captured the error
                Assert.Equal("on-deactivate-failed", onDeactivateSpan.StatusDescription);
            }
            finally
            {
                parent?.Stop();
                AssertNoApplicationSpansParentedByRuntimeSpans();
                PrintActivityDiagnostics();
            }
        }

        /// <summary>
        /// Tests that OnDeactivateAsync span is created during migration and precedes the dehydration span.
        /// Verifies the correct ordering: OnDeactivateAsync -> Dehydrate during migration.
        /// </summary>
        [Fact]
        [TestCategory("BVT")]
        public async Task OnDeactivateSpanPrecedesDehydrateDuringMigration()
        {
            Started.Clear();

            using var parent = ActivitySources.ApplicationGrainSource.StartActivity("test-parent-deactivate-migrate");
            parent?.Start();
            try
            {
                // Create a grain and set some state
                var grain = _fixture.GrainFactory.GetGrain<IDeactivationMigrationTracingTestGrain>(Random.Shared.Next());
                var expectedState = Random.Shared.Next();
                await grain.SetState(expectedState);
                var originalAddress = await grain.GetGrainAddress();
                var originalHost = originalAddress.SiloAddress;

                // Find a different silo to migrate to
                var targetHost = _fixture.HostedCluster.GetActiveSilos()
                    .Select(s => s.SiloAddress)
                    .First(address => address != originalHost);

                // Clear activities to focus on deactivation/migration
                Started.Clear();

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

                // Verify the state was preserved
                var newState = await grain.GetState();
                Assert.Equal(expectedState, newState);

                // Give some time for all activities to complete
                await Task.Delay(500);

                var testParentTraceId = parent.TraceId.ToString();

                // Find the OnDeactivate span
                var onDeactivateSpans = Started.Where(a => a.OperationName == ActivityNames.OnDeactivate).ToList();
                Assert.True(onDeactivateSpans.Count > 0, "Expected at least one OnDeactivate span during migration");

                // Find the dehydrate span
                var dehydrateSpans = Started.Where(a => a.OperationName == ActivityNames.ActivationDehydrate).ToList();
                Assert.True(dehydrateSpans.Count > 0, "Expected at least one dehydrate span during migration");

                // Verify OnDeactivate started before Dehydrate (as per the FinishDeactivating flow)
                var onDeactivateSpan = onDeactivateSpans.First();
                var dehydrateSpan = dehydrateSpans.First();

                Assert.True(onDeactivateSpan.StartTimeUtc <= dehydrateSpan.StartTimeUtc,
                    "OnDeactivateAsync should start before or at the same time as Dehydrate");

                // Verify both spans have proper tags
                Assert.NotNull(onDeactivateSpan.Tags.FirstOrDefault(t => t.Key == "orleans.grain.id").Value);
                Assert.Contains("Migrating", onDeactivateSpan.Tags.FirstOrDefault(t => t.Key == "orleans.deactivation.reason").Value);

                Assert.NotNull(dehydrateSpan.Tags.FirstOrDefault(t => t.Key == "orleans.grain.id").Value);
                Assert.Equal(targetHost.ToString(), dehydrateSpan.Tags.FirstOrDefault(t => t.Key == "orleans.migration.target.silo").Value);
            }
            finally
            {
                parent?.Stop();
                AssertNoApplicationSpansParentedByRuntimeSpans();
                PrintActivityDiagnostics();
            }
        }

        /// <summary>
        /// Tests that OnDeactivateAsync span is NOT created for grains that don't implement IGrainBase.
        /// Verifies that only grains with OnDeactivateAsync implementation get the span.
        /// </summary>
        [Fact]
        [TestCategory("BVT")]
        public async Task OnDeactivateSpanIsNotCreatedForNonGrainBaseGrain()
        {
            Started.Clear();

            using var parent = ActivitySources.ApplicationGrainSource.StartActivity("test-parent-no-deactivate");
            parent?.Start();
            try
            {
                // Use a simple grain that doesn't override OnDeactivateAsync (IActivityGrain/ActivityGrain)
                var grain = _fixture.GrainFactory.GetGrain<IActivityGrain>(Random.Shared.Next());

                // First call should force activation
                _ = await grain.GetActivityId();

                // Clear activities to focus on deactivation
                Started.Clear();

                // Trigger deactivation via IGrainManagementExtension
                await grain.Cast<IGrainManagementExtension>().DeactivateOnIdle();

                // Wait for deactivation to complete
                await Task.Delay(500);

                // Make another call to force a new activation
                _ = await grain.GetActivityId();

                // For grains that don't inherit from Grain (which implements IGrainBase), 
                // OnDeactivateAsync won't be called, so no span should be created
                // Note: ActivityGrain doesn't inherit from Grain, it implements IActivityGrain directly
                var onDeactivateSpans = Started.Where(a => a.OperationName == ActivityNames.OnDeactivate).ToList();

                // ActivityGrain doesn't implement IGrainBase, so no OnDeactivate span should be created
                Assert.True(onDeactivateSpans.Count == 0,
                    $"Expected no OnDeactivate spans for grain not implementing IGrainBase, but found {onDeactivateSpans.Count}");
            }
            finally
            {
                parent?.Stop();
                AssertNoApplicationSpansParentedByRuntimeSpans();
                PrintActivityDiagnostics();
            }
        }

        /// <summary>
        /// Tests that OnDeactivateAsync span properly inherits the trace context from the triggering call.
        /// Verifies that when deactivation is triggered, the OnDeactivate span has the same TraceId as the request.
        /// </summary>
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

                // First call should force activation
                _ = await grain.GetActivityId();

                var testParentTraceId = parent.TraceId.ToString();

                // Trigger deactivation - this call's activity context should be propagated to OnDeactivate
                await grain.TriggerDeactivation();

                // Wait for deactivation to complete
                await Task.Delay(500);

                // Make another call to force a new activation
                _ = await grain.GetActivityId();

                // Find the OnDeactivate span
                var onDeactivateSpans = Started.Where(a => a.OperationName == ActivityNames.OnDeactivate).ToList();
                Assert.True(onDeactivateSpans.Count > 0, "Expected at least one OnDeactivate span");

                var onDeactivateSpan = onDeactivateSpans.First();

                // Verify the OnDeactivate span shares the same trace ID as the parent activity
                // This confirms trace context propagation works correctly
                Assert.Equal(testParentTraceId, onDeactivateSpan.TraceId.ToString());
            }
            finally
            {
                parent?.Stop();
                AssertNoApplicationSpansParentedByRuntimeSpans();
                PrintActivityDiagnostics();
            }
        }

        /// <summary>
        /// Tests that OnDeactivateAsync span is created during IAsyncEnumerable method execution when the grain calls DeactivateOnIdle.
        /// Verifies that the OnDeactivate span is properly parented to the method call (session) span.
        /// </summary>
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
                var testParentTraceId = parent.TraceId.ToString();
                const int elementCount = 3;

                var values = new List<int>();
                await foreach (var value in grain.GetValuesAndDeactivate(elementCount).WithBatchSize(1))
                {
                    values.Add(value);
                }

                // Verify we received all elements
                Assert.Equal(elementCount, values.Count);

                // Wait for deactivation to complete
                await Task.Delay(1000);

                // Make another call to force a new activation (confirming the previous one was deactivated)
                _ = await grain.GetActivityId();

                // Find the session span (the logical method call span)
                var sessionSpans = Started
                    .Where(a => a.Source.Name == ActivitySources.ApplicationGrainActivitySourceName
                               && a.OperationName.Contains("GetValuesAndDeactivate"))
                    .ToList();
                Assert.True(sessionSpans.Count >= 1, "Expected at least one session span with GetValuesAndDeactivate operation name");

                var sessionSpan = sessionSpans.First();
                Assert.Equal(testParentTraceId, sessionSpan.TraceId.ToString());
                var sessionSpanId = sessionSpan.SpanId.ToString();

                // Find the OnDeactivate span
                var onDeactivateSpans = Started.Where(a => a.OperationName == ActivityNames.OnDeactivate).ToList();
                Assert.True(onDeactivateSpans.Count > 0, "Expected at least one OnDeactivate span to be created during enumeration");

                var onDeactivateSpan = onDeactivateSpans.First();

                // Verify the OnDeactivate span shares the same trace ID as the parent activity
                Assert.Equal(testParentTraceId, onDeactivateSpan.TraceId.ToString());

                // Verify the OnDeactivate span is parented to the session span
                // Note: The OnDeactivate might be a descendant (not direct child) of the session span,
                // but it should be in the same trace
                Assert.Equal(sessionSpan.TraceId, onDeactivateSpan.TraceId);

                // Verify deactivation reason tag
                var deactivationReasonTag = onDeactivateSpan.Tags.FirstOrDefault(t => t.Key == "orleans.deactivation.reason").Value;
                Assert.NotNull(deactivationReasonTag);
                Assert.Contains("ApplicationRequested", deactivationReasonTag);
            }
            finally
            {
                parent?.Stop();
                AssertNoApplicationSpansParentedByRuntimeSpans();
                PrintActivityDiagnostics();
            }
        }

        /// <summary>
        /// Tests that OnDeactivateAsync span captures proper deactivation reason for different deactivation scenarios.
        /// Verifies that the deactivation reason tag reflects the actual reason for deactivation.
        /// </summary>
        [Fact]
        [TestCategory("BVT")]
        public async Task OnDeactivateSpanHasCorrectReasonTagForMigration()
        {
            Started.Clear();

            using var parent = ActivitySources.ApplicationGrainSource.StartActivity("test-parent-reason-migration");
            parent?.Start();
            try
            {
                // Create a grain and set some state
                var grain = _fixture.GrainFactory.GetGrain<IDeactivationMigrationTracingTestGrain>(Random.Shared.Next());
                var testParentTraceId = parent.TraceId.ToString();
                await grain.SetState(42);
                var originalAddress = await grain.GetGrainAddress();
                var originalHost = originalAddress.SiloAddress;

                // Find a different silo to migrate to
                var targetHost = _fixture.HostedCluster.GetActiveSilos()
                    .Select(s => s.SiloAddress)
                    .First(address => address != originalHost);

                // Clear activities to focus on deactivation/migration
                Started.Clear();

                // Trigger migration
                RequestContext.Set(IPlacementDirector.PlacementHintKey, targetHost);
                await grain.Cast<IGrainManagementExtension>().MigrateOnIdle();

                // Wait for migration to complete
                GrainAddress newAddress;
                do
                {
                    await Task.Delay(100);
                    newAddress = await grain.GetGrainAddress();
                } while (newAddress.ActivationId == originalAddress.ActivationId);

                // Give some time for all activities to complete
                await Task.Delay(500);

                // Find the OnDeactivate span
                var onDeactivateSpans = Started.Where(a => a.OperationName == ActivityNames.OnDeactivate).ToList();
                Assert.True(onDeactivateSpans.Count > 0, "Expected at least one OnDeactivate span during migration");

                var onDeactivateSpan = onDeactivateSpans.First();

                // Verify the deactivation reason tag indicates migration
                var deactivationReasonTag = onDeactivateSpan.Tags.FirstOrDefault(t => t.Key == "orleans.deactivation.reason").Value;
                Assert.NotNull(deactivationReasonTag);
                Assert.Contains("Migrating", deactivationReasonTag);

                // Verify the OnDeactivate span shares the same trace ID as the parent activity
                Assert.Equal(testParentTraceId, onDeactivateSpan.TraceId.ToString());
            }
            finally
            {
                parent?.Stop();
                AssertNoApplicationSpansParentedByRuntimeSpans();
                PrintActivityDiagnostics();
            }
        }

        /// <summary>
        /// Tests that OnDeactivateAsync span is created when a grain throws InconsistentStateException
        /// and gets deactivated with ApplicationError reason.
        /// </summary>
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

                // First call should force activation
                _ = await grain.GetActivityId();

                // This call will throw InconsistentStateException and trigger deactivation
                try
                {
                    await grain.ThrowInconsistentStateException();
                }
                catch (InconsistentStateException)
                {
                    // Expected
                }

                // Wait for deactivation to complete
                await Task.Delay(500);

                // Make another call to force a new activation (confirming the previous one was deactivated)
                _ = await grain.GetActivityId();

                // Find the OnDeactivate span
                var onDeactivateSpans = Started.Where(a => a.OperationName == ActivityNames.OnDeactivate).ToList();
                Assert.True(onDeactivateSpans.Count > 0, "Expected at least one OnDeactivate span to be created during deactivation");

                var onDeactivateSpan = onDeactivateSpans.First();

                // Verify the span has expected tags
                Assert.NotNull(onDeactivateSpan.Tags.FirstOrDefault(t => t.Key == "orleans.grain.id").Value);
                Assert.NotNull(onDeactivateSpan.Tags.FirstOrDefault(t => t.Key == "orleans.grain.type").Value);

                // Verify deactivation reason tag indicates ApplicationError
                var deactivationReasonTag = onDeactivateSpan.Tags.FirstOrDefault(t => t.Key == "orleans.deactivation.reason").Value;
                Assert.NotNull(deactivationReasonTag);
                Assert.Contains("ApplicationError", deactivationReasonTag);

                // Verify the OnDeactivate span has a valid trace ID
                // Note: The trace ID may or may not match our parent activity depending on timing,
                // but it should be valid and propagated from somewhere in the call chain
                Assert.NotEqual(default(ActivityTraceId).ToString(), onDeactivateSpan.TraceId.ToString());
            }
            finally
            {
                parent?.Stop();
                AssertNoApplicationSpansParentedByRuntimeSpans();
                PrintActivityDiagnostics();
            }
        }

        /// <summary>
        /// Tests that OnDeactivateAsync span is NOT created when a grain fails during activation (PreviousState != Valid).
        /// The OnDeactivate span should only be created when the grain was previously in Valid state.
        /// </summary>
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

                // Clear activities to focus on activation/deactivation
                Started.Clear();

                // First call should trigger activation which will fail
                try
                {
                    await grain.GetActivityId();
                }
                catch
                {
                    // Expected - activation fails
                }

                // Wait for any deactivation to complete
                await Task.Delay(5000);

                // Find the OnDeactivate span - should NOT exist because the grain was never in Valid state
                var onDeactivateSpans = Started.Where(a => a.OperationName == ActivityNames.OnDeactivate).ToList();
                Assert.True(onDeactivateSpans.Count == 0,
                    $"Expected no OnDeactivate spans for grain that failed during activation, but found {onDeactivateSpans.Count}");

                // Verify the activation span was created
                var activationSpans = Started.Where(a => a.OperationName == ActivityNames.ActivateGrain).ToList();
                Assert.True(activationSpans.Count > 0, "Expected at least one activation span");
            }
            finally
            {
                parent?.Stop();
                AssertNoApplicationSpansParentedByRuntimeSpans();
                PrintActivityDiagnostics();
            }
        }

        /// <summary>
        /// Tests that OnDeactivateAsync span is created when a grain deactivates itself using GrainContext.Deactivate.
        /// This tests the programmatic deactivation path within the grain.
        /// </summary>
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
                var testParentTraceId = parent.TraceId.ToString();

                // First call should force activation
                _ = await grain.GetActivityId();

                // Clear activities to focus on deactivation
                Started.Clear();

                // Trigger deactivation using GrainContext.Deactivate with custom reason
                await grain.DeactivateWithCustomReason("Custom deactivation reason for testing");

                // Wait for deactivation to complete
                await Task.Delay(500);

                // Make another call to force a new activation (confirming the previous one was deactivated)
                _ = await grain.GetActivityId();

                // Find the OnDeactivate span
                var onDeactivateSpans = Started.Where(a => a.OperationName == ActivityNames.OnDeactivate).ToList();
                Assert.True(onDeactivateSpans.Count > 0, "Expected at least one OnDeactivate span to be created during deactivation");

                var onDeactivateSpan = onDeactivateSpans.First();

                // Verify the span has expected tags
                Assert.NotNull(onDeactivateSpan.Tags.FirstOrDefault(t => t.Key == "orleans.grain.id").Value);
                Assert.NotNull(onDeactivateSpan.Tags.FirstOrDefault(t => t.Key == "orleans.grain.type").Value);

                // Verify deactivation reason tag indicates ApplicationRequested with custom message
                var deactivationReasonTag = onDeactivateSpan.Tags.FirstOrDefault(t => t.Key == "orleans.deactivation.reason").Value;
                Assert.NotNull(deactivationReasonTag);
                Assert.Contains("ApplicationRequested", deactivationReasonTag);
                Assert.Contains("Custom deactivation reason for testing", deactivationReasonTag);

                // Verify the OnDeactivate span shares the same trace ID as the parent activity
                Assert.Equal(testParentTraceId, onDeactivateSpan.TraceId.ToString());
            }
            finally
            {
                parent?.Stop();
                AssertNoApplicationSpansParentedByRuntimeSpans();
                PrintActivityDiagnostics();
            }
        }

        /// <summary>
        /// Tests that the OnDeactivate span properly captures the activity context when deactivation
        /// is triggered externally via IGrainManagementExtension.DeactivateOnIdle.
        /// </summary>
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
                var testParentTraceId = parent.TraceId.ToString();

                // First call should force activation
                _ = await grain.GetActivityId();

                // Clear activities to focus on deactivation
                Started.Clear();

                // Trigger deactivation externally via IGrainManagementExtension
                await grain.Cast<IGrainManagementExtension>().DeactivateOnIdle();

                // Wait for deactivation to complete
                await Task.Delay(500);

                // Make another call to force a new activation (confirming the previous one was deactivated)
                _ = await grain.GetActivityId();

                // Find the OnDeactivate span
                var onDeactivateSpans = Started.Where(a => a.OperationName == ActivityNames.OnDeactivate).ToList();
                Assert.True(onDeactivateSpans.Count > 0, "Expected at least one OnDeactivate span to be created during deactivation");

                var onDeactivateSpan = onDeactivateSpans.First();

                // Verify the OnDeactivate span shares the same trace ID as the parent activity
                // This confirms the activity context was propagated from the DeactivateOnIdle call
                Assert.Equal(testParentTraceId, onDeactivateSpan.TraceId.ToString());

                // Verify deactivation reason tag
                var deactivationReasonTag = onDeactivateSpan.Tags.FirstOrDefault(t => t.Key == "orleans.deactivation.reason").Value;
                Assert.NotNull(deactivationReasonTag);
                Assert.Contains("ApplicationRequested", deactivationReasonTag);
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

    #region Test Grains for Deactivation Tracing

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
            // Simple deactivation logic to ensure OnDeactivateAsync is called
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
            // Perform work during deactivation - write state
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
            // Simple deactivation logic
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
            // Simple deactivation logic
            return Task.CompletedTask;
        }
    }

    #endregion

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
    /// Second test placement filter attribute for tracing tests with multiple filters.
    /// </summary>
    public class SecondTracingTestPlacementFilterAttribute() : PlacementFilterAttribute(new SecondTracingTestPlacementFilterStrategy());

    /// <summary>
    /// Second test placement filter strategy for tracing tests with multiple filters.
    /// </summary>
    public class SecondTracingTestPlacementFilterStrategy() : PlacementFilterStrategy(order: 2)
    {
    }

    /// <summary>
    /// Second test placement filter director that simply passes through all silos.
    /// </summary>
    public class SecondTracingTestPlacementFilterDirector : IPlacementFilterDirector
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

    /// <summary>
    /// Test grain interface with multiple placement filters for tracing tests.
    /// </summary>
    public interface IMultiFilteredActivityGrain : IGrainWithIntegerKey
    {
        Task<ActivityData> GetActivityId();
    }

    /// <summary>
    /// Test grain implementation with multiple placement filters for tracing tests.
    /// </summary>
    [TracingTestPlacementFilter]
    [SecondTracingTestPlacementFilter]
    public class MultiFilteredActivityGrain : Grain, IMultiFilteredActivityGrain
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

    /// <summary>
    /// Test grain interface for migration tracing tests with a placement filter.
    /// </summary>
    public interface IMigrationFilterTracingTestGrain : IGrainWithIntegerKey
    {
        ValueTask<GrainAddress> GetGrainAddress();
        ValueTask SetState(int state);
        ValueTask<int> GetState();
    }

    /// <summary>
    /// Test grain implementation for migration tracing tests with a placement filter.
    /// Combines IGrainMigrationParticipant and a placement filter to verify that
    /// FilterPlacementCandidates spans are properly parented under PlaceGrain during migration.
    /// </summary>
    [RandomPlacement]
    [TracingTestPlacementFilter]
    public class MigrationFilterTracingTestGrain : Grain, IMigrationFilterTracingTestGrain, IGrainMigrationParticipant
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

    #region Test Grain for IAsyncEnumerable with Deactivation

    /// <summary>
    /// Test grain interface for IAsyncEnumerable deactivation tracing tests.
    /// </summary>
    public interface IAsyncEnumerableDeactivationGrain : IGrainWithIntegerKey
    {
        IAsyncEnumerable<int> GetValuesAndDeactivate(int count);
        Task<ActivityData> GetActivityId();
    }

    /// <summary>
    /// Grain call filter that triggers deactivation after DisposeAsync is called on an async enumerable.
    /// This ensures deactivation happens after the enumeration is fully complete.
    /// </summary>
    public class DeactivateAfterDisposeAsyncFilter : IIncomingGrainCallFilter
    {
        public async Task Invoke(IIncomingGrainCallContext context)
        {
            await context.Invoke();

            // Check if this is the DisposeAsync call for async enumerable
            if (context.InterfaceMethod?.Name == "DisposeAsync" &&
                context.InterfaceMethod.DeclaringType?.FullName == "Orleans.Runtime.IAsyncEnumerableGrainExtension")
            {
                // Trigger deactivation on the grain
                if (context.Grain is Grain grain)
                {
                    grain.DeactivateOnIdle();
                }
            }
        }
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
                await Task.Delay(10); // Small delay to simulate work
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
            // Simple deactivation logic to ensure OnDeactivateAsync is called
            return Task.CompletedTask;
        }
    }

    #endregion

    #region Trace Context Propagation Tests

    /// <summary>
    /// Test grain interface for verifying trace context propagation from client to grain.
    /// Returns detailed trace information to verify the server received the correct trace context.
    /// </summary>
    public interface ITraceContextPropagationGrain : IGrainWithIntegerKey
    {
        /// <summary>
        /// Returns detailed trace information from the server-side Activity.Current.
        /// This allows the test to verify that trace context was properly propagated.
        /// </summary>
        Task<TraceContextInfo> GetTraceContextInfo();

        /// <summary>
        /// Makes a call to another grain and returns both the local and nested trace context.
        /// Used to verify trace context propagation across grain-to-grain calls.
        /// </summary>
        Task<(TraceContextInfo Local, TraceContextInfo Nested)> GetNestedTraceContextInfo();
    }

    /// <summary>
    /// Detailed trace context information returned from grain calls.
    /// </summary>
    [GenerateSerializer]
    public class TraceContextInfo
    {
        [Id(0)]
        public string ActivityId { get; set; }

        [Id(1)]
        public string TraceId { get; set; }

        [Id(2)]
        public string SpanId { get; set; }

        [Id(3)]
        public string ParentSpanId { get; set; }

        [Id(4)]
        public string ParentId { get; set; }

        [Id(5)]
        public string OperationName { get; set; }

        [Id(6)]
        public string TraceParentFromRequestContext { get; set; }

        [Id(7)]
        public bool HasActivity { get; set; }

        [Id(8)]
        public ActivityKind Kind { get; set; }

        [Id(9)]
        public bool IsRemote { get; set; }
    }

    /// <summary>
    /// Test grain implementation for verifying trace context propagation.
    /// </summary>
    public class TraceContextPropagationGrain : Grain, ITraceContextPropagationGrain
    {
        public Task<TraceContextInfo> GetTraceContextInfo()
        {
            var activity = Activity.Current;
            var traceParent = RequestContext.Get("traceparent") as string;

            return Task.FromResult(new TraceContextInfo
            {
                HasActivity = activity is not null,
                ActivityId = activity?.Id,
                TraceId = activity?.TraceId.ToString(),
                SpanId = activity?.SpanId.ToString(),
                ParentSpanId = activity?.ParentSpanId.ToString(),
                ParentId = activity?.ParentId,
                OperationName = activity?.OperationName,
                Kind = activity?.Kind ?? ActivityKind.Internal,
                IsRemote = activity?.HasRemoteParent ?? false,
                TraceParentFromRequestContext = traceParent
            });
        }

        public async Task<(TraceContextInfo Local, TraceContextInfo Nested)> GetNestedTraceContextInfo()
        {
            var localInfo = await GetTraceContextInfo();

            // Make a nested call to another grain
            var nestedGrain = GrainFactory.GetGrain<ITraceContextPropagationGrain>(this.GetPrimaryKeyLong() + 1);
            var nestedInfo = await nestedGrain.GetTraceContextInfo();

            return (localInfo, nestedInfo);
        }
    }

    #endregion

    /// <summary>
    /// Tests specifically for verifying trace context propagation between client and grain server.
    /// These tests expose issues where the server-side span starts a new trace instead of continuing the client's trace.
    /// </summary>
    [Collection("ActivationTracing")]
    public class GrainCallTraceContextPropagationTests : OrleansTestingBase, IClassFixture<ActivationTracingTests.Fixture>
    {
        private static readonly ConcurrentBag<Activity> Started = new();

        static GrainCallTraceContextPropagationTests()
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

        public GrainCallTraceContextPropagationTests(ActivationTracingTests.Fixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture;
            _output = output;
        }

        /// <summary>
        /// CRITICAL TEST: Verifies that the server-side grain call activity has the same TraceId as the client.
        /// This test fails if trace context propagation is broken - the server will start a new trace instead
        /// of continuing the client's trace.
        /// </summary>
        [Fact]
        [TestCategory("BVT")]
        public async Task ServerSideGrainCallSharesSameTraceIdAsClient()
        {
            Started.Clear();

            // Start a parent activity on the client side
            using var clientActivity = ActivitySources.ApplicationGrainSource.StartActivity("client-parent-activity");
            clientActivity?.Start();

            Assert.NotNull(clientActivity);
            var clientTraceId = clientActivity.TraceId.ToString();
            var clientSpanId = clientActivity.SpanId.ToString();

            _output.WriteLine($"Client TraceId: {clientTraceId}");
            _output.WriteLine($"Client SpanId: {clientSpanId}");

            try
            {
                var grain = _fixture.GrainFactory.GetGrain<ITraceContextPropagationGrain>(Random.Shared.Next());

                // This call should propagate the trace context to the server
                var serverTraceInfo = await grain.GetTraceContextInfo();

                _output.WriteLine($"Server HasActivity: {serverTraceInfo.HasActivity}");
                _output.WriteLine($"Server TraceId: {serverTraceInfo.TraceId}");
                _output.WriteLine($"Server SpanId: {serverTraceInfo.SpanId}");
                _output.WriteLine($"Server ParentSpanId: {serverTraceInfo.ParentSpanId}");
                _output.WriteLine($"Server ParentId: {serverTraceInfo.ParentId}");
                _output.WriteLine($"Server OperationName: {serverTraceInfo.OperationName}");
                _output.WriteLine($"Server Kind: {serverTraceInfo.Kind}");
                _output.WriteLine($"Server IsRemote: {serverTraceInfo.IsRemote}");
                _output.WriteLine($"Server TraceParentFromRequestContext: {serverTraceInfo.TraceParentFromRequestContext}");

                // CRITICAL ASSERTION: Server must have an activity
                Assert.True(serverTraceInfo.HasActivity, "Server-side grain call should have an Activity.Current");

                // CRITICAL ASSERTION: Server TraceId must match client TraceId
                // If this fails, trace context propagation is broken!
                Assert.Equal(clientTraceId, serverTraceInfo.TraceId);
            }
            finally
            {
                clientActivity?.Stop();
                PrintActivityDiagnostics();
            }
        }

        /// <summary>
        /// Verifies that the server-side activity is a Server kind and has a remote parent.
        /// This confirms proper W3C trace context handling.
        /// </summary>
        [Fact]
        [TestCategory("BVT")]
        public async Task ServerSideActivityHasCorrectKindAndRemoteParent()
        {
            Started.Clear();

            using var clientActivity = ActivitySources.ApplicationGrainSource.StartActivity("client-activity-kind-test");
            clientActivity?.Start();

            Assert.NotNull(clientActivity);

            try
            {
                var grain = _fixture.GrainFactory.GetGrain<ITraceContextPropagationGrain>(Random.Shared.Next());
                var serverTraceInfo = await grain.GetTraceContextInfo();

                _output.WriteLine($"Server Kind: {serverTraceInfo.Kind}");
                _output.WriteLine($"Server IsRemote: {serverTraceInfo.IsRemote}");

                Assert.True(serverTraceInfo.HasActivity, "Server-side grain call should have an Activity.Current");

                // Server-side activity should be of kind Server
                Assert.Equal(ActivityKind.Server, serverTraceInfo.Kind);

                // Server-side activity should have a remote parent (propagated from client)
                Assert.True(serverTraceInfo.IsRemote, "Server-side activity should have HasRemoteParent=true");
            }
            finally
            {
                clientActivity?.Stop();
                PrintActivityDiagnostics();
            }
        }

        /// <summary>
        /// Verifies trace context propagation across nested grain-to-grain calls.
        /// All calls in the chain should share the same TraceId.
        /// </summary>
        [Fact]
        [TestCategory("BVT")]
        public async Task NestedGrainCallsShareSameTraceId()
        {
            Started.Clear();

            using var clientActivity = ActivitySources.ApplicationGrainSource.StartActivity("client-nested-calls-test");
            clientActivity?.Start();

            Assert.NotNull(clientActivity);
            var clientTraceId = clientActivity.TraceId.ToString();

            _output.WriteLine($"Client TraceId: {clientTraceId}");

            try
            {
                var grain = _fixture.GrainFactory.GetGrain<ITraceContextPropagationGrain>(Random.Shared.Next());
                var (localInfo, nestedInfo) = await grain.GetNestedTraceContextInfo();

                _output.WriteLine($"First Grain TraceId: {localInfo.TraceId}");
                _output.WriteLine($"Nested Grain TraceId: {nestedInfo.TraceId}");

                // Both grains should have activities
                Assert.True(localInfo.HasActivity, "First grain should have an Activity.Current");
                Assert.True(nestedInfo.HasActivity, "Nested grain should have an Activity.Current");

                // CRITICAL: All calls should share the same TraceId
                Assert.Equal(clientTraceId, localInfo.TraceId);
                Assert.Equal(clientTraceId, nestedInfo.TraceId);
            }
            finally
            {
                clientActivity?.Stop();
                PrintActivityDiagnostics();
            }
        }

        /// <summary>
        /// Verifies that traceparent header is properly set in RequestContext when making grain calls.
        /// This tests the outgoing filter's injection of trace context.
        /// </summary>
        [Fact]
        [TestCategory("BVT")]
        public async Task TraceParentIsSetInRequestContext()
        {
            Started.Clear();

            using var clientActivity = ActivitySources.ApplicationGrainSource.StartActivity("client-traceparent-test");
            clientActivity?.Start();

            Assert.NotNull(clientActivity);
            var clientTraceId = clientActivity.TraceId.ToString();

            _output.WriteLine($"Client TraceId: {clientTraceId}");

            try
            {
                var grain = _fixture.GrainFactory.GetGrain<ITraceContextPropagationGrain>(Random.Shared.Next());
                var serverTraceInfo = await grain.GetTraceContextInfo();

                _output.WriteLine($"Server TraceParentFromRequestContext: {serverTraceInfo.TraceParentFromRequestContext}");

                // traceparent header should be present in RequestContext
                Assert.NotNull(serverTraceInfo.TraceParentFromRequestContext);
                Assert.NotEmpty(serverTraceInfo.TraceParentFromRequestContext);

                // traceparent should contain the client's TraceId
                Assert.Contains(clientTraceId, serverTraceInfo.TraceParentFromRequestContext);
            }
            finally
            {
                clientActivity?.Stop();
                PrintActivityDiagnostics();
            }
        }

        /// <summary>
        /// Verifies that the client-side outgoing span and server-side incoming span are properly linked.
        /// The server span's parent should be the client's outgoing span.
        /// </summary>
        [Fact]
        [TestCategory("BVT")]
        public async Task ClientAndServerSpansAreProperlyLinked()
        {
            Started.Clear();

            using var clientParentActivity = ActivitySources.ApplicationGrainSource.StartActivity("client-linking-test");
            clientParentActivity?.Start();

            Assert.NotNull(clientParentActivity);
            var clientTraceId = clientParentActivity.TraceId.ToString();

            _output.WriteLine($"Client Parent TraceId: {clientTraceId}");
            _output.WriteLine($"Client Parent SpanId: {clientParentActivity.SpanId}");

            try
            {
                var grain = _fixture.GrainFactory.GetGrain<ITraceContextPropagationGrain>(Random.Shared.Next());
                _ = await grain.GetTraceContextInfo();

                // Find the client-side outgoing span (should be a child of our test activity)
                var clientOutgoingSpan = Started
                    .Where(a => a.Source.Name == ActivitySources.ApplicationGrainActivitySourceName
                               && a.Kind == ActivityKind.Client
                               && a.OperationName.Contains("GetTraceContextInfo"))
                    .FirstOrDefault();

                // Find the server-side incoming span
                var serverIncomingSpan = Started
                    .Where(a => a.Source.Name == ActivitySources.ApplicationGrainActivitySourceName
                               && a.Kind == ActivityKind.Server
                               && a.OperationName.Contains("GetTraceContextInfo"))
                    .FirstOrDefault();

                _output.WriteLine($"Client Outgoing Span: {clientOutgoingSpan?.Id ?? "(not found)"}");
                _output.WriteLine($"Server Incoming Span: {serverIncomingSpan?.Id ?? "(not found)"}");

                Assert.NotNull(clientOutgoingSpan);
                Assert.NotNull(serverIncomingSpan);

                // Both should share the same TraceId
                Assert.Equal(clientTraceId, clientOutgoingSpan.TraceId.ToString());
                Assert.Equal(clientTraceId, serverIncomingSpan.TraceId.ToString());

                // Client outgoing span should be parented to our test activity
                Assert.Equal(clientParentActivity.SpanId.ToString(), clientOutgoingSpan.ParentSpanId.ToString());

                // Server span's parent should be the client outgoing span
                Assert.Equal(clientOutgoingSpan.SpanId.ToString(), serverIncomingSpan.ParentSpanId.ToString());
            }
            finally
            {
                clientParentActivity?.Stop();
                PrintActivityDiagnostics();
            }
        }

        /// <summary>
        /// Verifies that trace context is properly propagated even when the client has no active activity.
        /// The server should still create its own trace in this case.
        /// </summary>
        [Fact]
        [TestCategory("BVT")]
        public async Task ServerCreatesOwnTraceWhenClientHasNoActivity()
        {
            Started.Clear();

            // Ensure no activity is current on the client
            var previousActivity = Activity.Current;
            Activity.Current = null;

            try
            {
                var grain = _fixture.GrainFactory.GetGrain<ITraceContextPropagationGrain>(Random.Shared.Next());
                var serverTraceInfo = await grain.GetTraceContextInfo();

                _output.WriteLine($"Server HasActivity: {serverTraceInfo.HasActivity}");
                _output.WriteLine($"Server TraceId: {serverTraceInfo.TraceId}");

                // Server should still create an activity (starting a new trace)
                Assert.True(serverTraceInfo.HasActivity, "Server should create an activity even when client has none");
                Assert.NotNull(serverTraceInfo.TraceId);
                Assert.NotEmpty(serverTraceInfo.TraceId);
            }
            finally
            {
                Activity.Current = previousActivity;
                PrintActivityDiagnostics();
            }
        }

        /// <summary>
        /// Verifies that multiple concurrent grain calls from the same client activity
        /// all share the same TraceId.
        /// </summary>
        [Fact]
        [TestCategory("BVT")]
        public async Task ConcurrentGrainCallsShareSameTraceId()
        {
            Started.Clear();

            using var clientActivity = ActivitySources.ApplicationGrainSource.StartActivity("client-concurrent-test");
            clientActivity?.Start();

            Assert.NotNull(clientActivity);
            var clientTraceId = clientActivity.TraceId.ToString();

            _output.WriteLine($"Client TraceId: {clientTraceId}");

            try
            {
                var tasks = Enumerable.Range(0, 5)
                    .Select(i => _fixture.GrainFactory.GetGrain<ITraceContextPropagationGrain>(Random.Shared.Next()).GetTraceContextInfo())
                    .ToList();

                var results = await Task.WhenAll(tasks);

                foreach (var (result, index) in results.Select((r, i) => (r, i)))
                {
                    _output.WriteLine($"Grain {index} TraceId: {result.TraceId}");

                    Assert.True(result.HasActivity, $"Grain {index} should have an Activity.Current");
                    Assert.Equal(clientTraceId, result.TraceId);
                }
            }
            finally
            {
                clientActivity?.Stop();
                PrintActivityDiagnostics();
            }
        }

        /// <summary>
        /// EDGE CASE: Tests trace context propagation when the traceparent header contains an unexpected format.
        /// The server should handle malformed headers gracefully and still create an activity.
        /// </summary>
        [Fact]
        [TestCategory("BVT")]
        public async Task ServerHandlesMalformedTraceParentGracefully()
        {
            Started.Clear();

            // Manually set an invalid traceparent in RequestContext
            RequestContext.Set("traceparent", "invalid-traceparent-value");

            try
            {
                var grain = _fixture.GrainFactory.GetGrain<ITraceContextPropagationGrain>(Random.Shared.Next());
                var serverTraceInfo = await grain.GetTraceContextInfo();

                _output.WriteLine($"Server HasActivity: {serverTraceInfo.HasActivity}");
                _output.WriteLine($"Server TraceId: {serverTraceInfo.TraceId}");
                _output.WriteLine($"Server TraceParentFromRequestContext: {serverTraceInfo.TraceParentFromRequestContext}");

                // Server should still have an activity (creating a new trace)
                Assert.True(serverTraceInfo.HasActivity, "Server should create an activity even with malformed traceparent");
                Assert.NotNull(serverTraceInfo.TraceId);
            }
            finally
            {
                RequestContext.Clear();
                PrintActivityDiagnostics();
            }
        }

        /// <summary>
        /// Tests that the traceparent in RequestContext reflects the client's outgoing span,
        /// not the original parent activity. This verifies proper span creation on the client side.
        /// </summary>
        [Fact]
        [TestCategory("BVT")]
        public async Task TraceParentReflectsClientOutgoingSpan()
        {
            Started.Clear();

            using var clientParentActivity = ActivitySources.ApplicationGrainSource.StartActivity("client-traceparent-reflection-test");
            clientParentActivity?.Start();

            Assert.NotNull(clientParentActivity);
            var clientTraceId = clientParentActivity.TraceId.ToString();
            var clientParentSpanId = clientParentActivity.SpanId.ToString();

            _output.WriteLine($"Client Parent TraceId: {clientTraceId}");
            _output.WriteLine($"Client Parent SpanId: {clientParentSpanId}");

            try
            {
                var grain = _fixture.GrainFactory.GetGrain<ITraceContextPropagationGrain>(Random.Shared.Next());
                var serverTraceInfo = await grain.GetTraceContextInfo();

                _output.WriteLine($"Server TraceParentFromRequestContext: {serverTraceInfo.TraceParentFromRequestContext}");
                _output.WriteLine($"Server ParentSpanId: {serverTraceInfo.ParentSpanId}");

                Assert.NotNull(serverTraceInfo.TraceParentFromRequestContext);

                // The traceparent should contain the TraceId
                Assert.Contains(clientTraceId, serverTraceInfo.TraceParentFromRequestContext);

                // The server's parent span ID should NOT be the original client parent span ID
                // It should be the span ID of the client's outgoing call span
                // (This is because the client creates a new span for the outgoing call)
                Assert.NotEqual(clientParentSpanId, serverTraceInfo.ParentSpanId);

                // Find the client outgoing span
                var clientOutgoingSpan = Started
                    .FirstOrDefault(a => a.Source.Name == ActivitySources.ApplicationGrainActivitySourceName
                                        && a.Kind == ActivityKind.Client
                                        && a.OperationName.Contains("GetTraceContextInfo"));

                Assert.NotNull(clientOutgoingSpan);

                // The server's parent span ID should match the client outgoing span ID
                Assert.Equal(clientOutgoingSpan.SpanId.ToString(), serverTraceInfo.ParentSpanId);

                // The traceparent should contain the client outgoing span ID
                Assert.Contains(clientOutgoingSpan.SpanId.ToString(), serverTraceInfo.TraceParentFromRequestContext);
            }
            finally
            {
                clientParentActivity?.Stop();
                PrintActivityDiagnostics();
            }
        }

        /// <summary>
        /// Tests trace context propagation when making calls from within a grain's OnActivateAsync.
        /// This is a common edge case where activation might not have a proper trace context.
        /// </summary>
        [Fact]
        [TestCategory("BVT")]
        public async Task TraceContextIsPropagatedDuringActivation()
        {
            Started.Clear();

            using var clientActivity = ActivitySources.ApplicationGrainSource.StartActivity("client-activation-context-test");
            clientActivity?.Start();

            Assert.NotNull(clientActivity);
            var clientTraceId = clientActivity.TraceId.ToString();

            _output.WriteLine($"Client TraceId: {clientTraceId}");

            try
            {
                // Make a call that triggers activation
                var grain = _fixture.GrainFactory.GetGrain<ITraceContextPropagationGrain>(Random.Shared.Next());
                var serverTraceInfo = await grain.GetTraceContextInfo();

                _output.WriteLine($"Server TraceId (during activation): {serverTraceInfo.TraceId}");

                // Verify the trace ID matches
                Assert.Equal(clientTraceId, serverTraceInfo.TraceId);

                // Find the activation span
                var activationSpan = Started
                    .FirstOrDefault(a => a.OperationName == ActivityNames.ActivateGrain);

                if (activationSpan is not null)
                {
                    _output.WriteLine($"Activation Span TraceId: {activationSpan.TraceId}");
                    // The activation span should also share the same trace ID
                    Assert.Equal(clientTraceId, activationSpan.TraceId.ToString());
                }
            }
            finally
            {
                clientActivity?.Stop();
                PrintActivityDiagnostics();
            }
        }

        /// <summary>
        /// Tests that tracestate header is properly propagated along with traceparent.
        /// </summary>
        [Fact]
        [TestCategory("BVT")]
        public async Task TraceStateIsPropagatedWithTraceParent()
        {
            Started.Clear();

            using var clientActivity = ActivitySources.ApplicationGrainSource.StartActivity("client-tracestate-test");
            clientActivity?.Start();

            Assert.NotNull(clientActivity);

            // Set a tracestate on the client activity
            clientActivity.TraceStateString = "vendor1=value1,vendor2=value2";

            try
            {
                var grain = _fixture.GrainFactory.GetGrain<ITraceContextPropagationGrain>(Random.Shared.Next());
                var serverTraceInfo = await grain.GetTraceContextInfo();

                // Find the server span
                var serverSpan = Started
                    .FirstOrDefault(a => a.Source.Name == ActivitySources.ApplicationGrainActivitySourceName
                                        && a.Kind == ActivityKind.Server
                                        && a.OperationName.Contains("GetTraceContextInfo"));

                _output.WriteLine($"Server Span TraceState: {serverSpan?.TraceStateString ?? "(null)"}");

                // The tracestate should be propagated to the server
                // Note: This test may need adjustment based on how Orleans handles tracestate
                if (serverSpan is not null && !string.IsNullOrEmpty(serverSpan.TraceStateString))
                {
                    Assert.Contains("vendor1", serverSpan.TraceStateString);
                }
            }
            finally
            {
                clientActivity?.Stop();
                PrintActivityDiagnostics();
            }
        }

        /// <summary>
        /// CRITICAL TEST: Verifies that when a grain call triggers activation, the activation span
        /// shares the same TraceId as the grain call and is properly linked in the trace.
        /// 
        /// This test reproduces the production issue where the activation span starts a new trace
        /// instead of being part of the incoming grain call's trace.
        /// 
        /// Expected trace structure:
        ///   client-parent-activity
        ///     └── ITraceContextPropagationGrain/GetTraceContextInfo (Client, outgoing)
        ///           └── ITraceContextPropagationGrain/GetTraceContextInfo (Server, incoming) 
        ///                 └── activate grain (should be linked to this trace!)
        ///                       ├── register directory entry
        ///                       └── execute OnActivateAsync
        /// 
        /// Bug scenario (what we're testing for):
        ///   activate grain (NEW TRACE - disconnected from client!)  <-- THIS IS THE BUG
        ///     ├── register directory entry
        ///     └── execute OnActivateAsync
        /// </summary>
        [Fact]
        [TestCategory("BVT")]
        public async Task ActivationSpanSharesTraceIdWithTriggeringGrainCall()
        {
            Started.Clear();

            using var clientActivity = ActivitySources.ApplicationGrainSource.StartActivity("client-activation-trace-test");
            clientActivity?.Start();

            Assert.NotNull(clientActivity);
            var clientTraceId = clientActivity.TraceId.ToString();

            _output.WriteLine($"Client TraceId: {clientTraceId}");

            try
            {
                // Use a unique grain ID to ensure we trigger a new activation
                var uniqueGrainId = Random.Shared.Next();
                var grain = _fixture.GrainFactory.GetGrain<ITraceContextPropagationGrain>(uniqueGrainId);

                // This call will trigger activation since it's a new grain
                var serverTraceInfo = await grain.GetTraceContextInfo();

                _output.WriteLine($"Server TraceId: {serverTraceInfo.TraceId}");

                // Find the activation span
                var activationSpan = Started
                    .FirstOrDefault(a => a.OperationName == ActivityNames.ActivateGrain);

                _output.WriteLine($"Activation Span found: {activationSpan is not null}");
                if (activationSpan is not null)
                {
                    _output.WriteLine($"Activation Span TraceId: {activationSpan.TraceId}");
                    _output.WriteLine($"Activation Span ParentSpanId: {activationSpan.ParentSpanId}");
                    _output.WriteLine($"Activation Span ParentId: {activationSpan.ParentId}");
                }

                // CRITICAL ASSERTION: Activation span must exist
                Assert.NotNull(activationSpan);

                // CRITICAL ASSERTION: Activation span must share the same TraceId as the client
                // If this fails, the activation is starting a new trace instead of being part
                // of the incoming grain call's trace!
                Assert.Equal(clientTraceId, activationSpan.TraceId.ToString());

                // CRITICAL ASSERTION: Activation span should have a parent (not be a root span)
                // In the bug scenario, the activation span has no parent and starts a new trace
                Assert.False(
                    string.IsNullOrEmpty(activationSpan.ParentId),
                    "Activation span should have a parent! If this fails, the activation is starting a new trace.");

                // Verify the grain call span also shares the same trace ID
                Assert.Equal(clientTraceId, serverTraceInfo.TraceId);
            }
            finally
            {
                clientActivity?.Stop();
                PrintActivityDiagnostics();
            }
        }

        /// <summary>
        /// Verifies that when activation is triggered by a grain call, the entire trace hierarchy is correct:
        /// 1. Client outgoing span
        /// 2. Server incoming grain call span (parented to client outgoing)
        /// 3. Activation span (should share trace context with the grain call)
        /// 4. OnActivateAsync span (parented to activation span)
        /// 
        /// This test checks the full hierarchy to ensure no span is disconnected.
        /// </summary>
        [Fact]
        [TestCategory("BVT")]
        public async Task FullTraceHierarchyIsCorrectWhenActivationTriggeredByGrainCall()
        {
            Started.Clear();

            using var clientActivity = ActivitySources.ApplicationGrainSource.StartActivity("client-hierarchy-test");
            clientActivity?.Start();

            Assert.NotNull(clientActivity);
            var clientTraceId = clientActivity.TraceId.ToString();
            var clientSpanId = clientActivity.SpanId.ToString();

            _output.WriteLine($"=== TEST START ===");
            _output.WriteLine($"Client Activity TraceId: {clientTraceId}");
            _output.WriteLine($"Client Activity SpanId: {clientSpanId}");

            try
            {
                // Use a grain type that has OnActivateAsync to ensure we get all spans
                var grain = _fixture.GrainFactory.GetGrain<IFilteredActivityGrain>(Random.Shared.Next());
                _ = await grain.GetActivityId();

                _output.WriteLine($"\n=== SPAN ANALYSIS ===");

                // 1. Find client outgoing span
                var clientOutgoingSpan = Started
                    .FirstOrDefault(a => a.Source.Name == ActivitySources.ApplicationGrainActivitySourceName
                                        && a.Kind == ActivityKind.Client
                                        && a.OperationName.Contains("GetActivityId"));

                _output.WriteLine($"\n1. Client Outgoing Span:");
                if (clientOutgoingSpan is not null)
                {
                    _output.WriteLine($"   TraceId: {clientOutgoingSpan.TraceId}");
                    _output.WriteLine($"   SpanId: {clientOutgoingSpan.SpanId}");
                    _output.WriteLine($"   ParentSpanId: {clientOutgoingSpan.ParentSpanId}");
                    _output.WriteLine($"   ParentId: {clientOutgoingSpan.ParentId}");
                }
                else
                {
                    _output.WriteLine($"   NOT FOUND!");
                }

                // 2. Find server incoming span
                var serverIncomingSpan = Started
                    .FirstOrDefault(a => a.Source.Name == ActivitySources.ApplicationGrainActivitySourceName
                                        && a.Kind == ActivityKind.Server
                                        && a.OperationName.Contains("GetActivityId"));

                _output.WriteLine($"\n2. Server Incoming Span:");
                if (serverIncomingSpan is not null)
                {
                    _output.WriteLine($"   TraceId: {serverIncomingSpan.TraceId}");
                    _output.WriteLine($"   SpanId: {serverIncomingSpan.SpanId}");
                    _output.WriteLine($"   ParentSpanId: {serverIncomingSpan.ParentSpanId}");
                    _output.WriteLine($"   ParentId: {serverIncomingSpan.ParentId}");
                }
                else
                {
                    _output.WriteLine($"   NOT FOUND!");
                }

                // 3. Find activation span
                var activationSpan = Started
                    .FirstOrDefault(a => a.OperationName == ActivityNames.ActivateGrain);

                _output.WriteLine($"\n3. Activation Span:");
                if (activationSpan is not null)
                {
                    _output.WriteLine($"   TraceId: {activationSpan.TraceId}");
                    _output.WriteLine($"   SpanId: {activationSpan.SpanId}");
                    _output.WriteLine($"   ParentSpanId: {activationSpan.ParentSpanId}");
                    _output.WriteLine($"   ParentId: {activationSpan.ParentId ?? "(null - ROOT SPAN!)"}");
                }
                else
                {
                    _output.WriteLine($"   NOT FOUND!");
                }

                // 4. Find OnActivateAsync span
                var onActivateSpan = Started
                    .FirstOrDefault(a => a.OperationName == ActivityNames.OnActivate);

                _output.WriteLine($"\n4. OnActivateAsync Span:");
                if (onActivateSpan is not null)
                {
                    _output.WriteLine($"   TraceId: {onActivateSpan.TraceId}");
                    _output.WriteLine($"   SpanId: {onActivateSpan.SpanId}");
                    _output.WriteLine($"   ParentSpanId: {onActivateSpan.ParentSpanId}");
                    _output.WriteLine($"   ParentId: {onActivateSpan.ParentId}");
                }
                else
                {
                    _output.WriteLine($"   NOT FOUND (grain may not implement IGrainBase)");
                }

                // ASSERTIONS
                _output.WriteLine($"\n=== ASSERTIONS ===");

                // All spans should share the same TraceId
                Assert.NotNull(clientOutgoingSpan);
                Assert.NotNull(serverIncomingSpan);
                Assert.NotNull(activationSpan);

                _output.WriteLine($"Checking all spans share TraceId {clientTraceId}...");

                Assert.Equal(clientTraceId, clientOutgoingSpan.TraceId.ToString());
                _output.WriteLine($"  ✓ Client outgoing span has correct TraceId");

                Assert.Equal(clientTraceId, serverIncomingSpan.TraceId.ToString());
                _output.WriteLine($"  ✓ Server incoming span has correct TraceId");

                // THIS IS THE CRITICAL CHECK - activation span must share the trace ID!
                Assert.Equal(clientTraceId, activationSpan.TraceId.ToString());
                _output.WriteLine($"  ✓ Activation span has correct TraceId");

                // Client outgoing span should be parented to our test activity
                Assert.Equal(clientSpanId, clientOutgoingSpan.ParentSpanId.ToString());
                _output.WriteLine($"  ✓ Client outgoing span is parented to test activity");

                // Server incoming span should be parented to client outgoing span
                Assert.Equal(clientOutgoingSpan.SpanId.ToString(), serverIncomingSpan.ParentSpanId.ToString());
                _output.WriteLine($"  ✓ Server incoming span is parented to client outgoing span");

                // Activation span should have a parent (not be a root span)
                Assert.False(
                    string.IsNullOrEmpty(activationSpan.ParentId),
                    "Activation span should not be a root span! This indicates broken trace context propagation.");
                _output.WriteLine($"  ✓ Activation span has a parent (not a root span)");

                if (onActivateSpan is not null)
                {
                    Assert.Equal(clientTraceId, onActivateSpan.TraceId.ToString());
                    _output.WriteLine($"  ✓ OnActivateAsync span has correct TraceId");

                    Assert.Equal(activationSpan.SpanId.ToString(), onActivateSpan.ParentSpanId.ToString());
                    _output.WriteLine($"  ✓ OnActivateAsync span is parented to activation span");
                }

                _output.WriteLine($"\n=== ALL CHECKS PASSED ===");
            }
            finally
            {
                clientActivity?.Stop();
                PrintActivityDiagnostics();
            }
        }

        /// <summary>
        /// Diagnostic test that checks if the traceparent is properly present in the message's
        /// RequestContextData when it reaches the server. This helps diagnose production issues
        /// where trace context might not be propagating.
        /// </summary>
        [Fact]
        [TestCategory("BVT")]
        public async Task DiagnoseTraceContextInRequestContextData()
        {
            Started.Clear();

            using var clientActivity = ActivitySources.ApplicationGrainSource.StartActivity("client-diagnostic-test");
            clientActivity?.Start();

            Assert.NotNull(clientActivity);
            var clientTraceId = clientActivity.TraceId.ToString();

            _output.WriteLine($"=== DIAGNOSTIC TEST ===");
            _output.WriteLine($"Client Activity TraceId: {clientTraceId}");
            _output.WriteLine($"Client Activity SpanId: {clientActivity.SpanId}");

            try
            {
                var grain = _fixture.GrainFactory.GetGrain<ITraceContextPropagationGrain>(Random.Shared.Next());
                var serverTraceInfo = await grain.GetTraceContextInfo();

                _output.WriteLine($"\n=== Server-side RequestContext Analysis ===");
                _output.WriteLine($"TraceParent from RequestContext: {serverTraceInfo.TraceParentFromRequestContext ?? "(NULL or MISSING!)"}");
                _output.WriteLine($"Server Activity TraceId: {serverTraceInfo.TraceId ?? "(NULL!)"}");
                _output.WriteLine($"Server Activity ParentSpanId: {serverTraceInfo.ParentSpanId ?? "(NULL!)"}");
                _output.WriteLine($"Server Activity HasRemoteParent: {serverTraceInfo.IsRemote}");

                // DIAGNOSTIC ASSERTIONS
                if (string.IsNullOrEmpty(serverTraceInfo.TraceParentFromRequestContext))
                {
                    _output.WriteLine("\n⚠️ WARNING: traceparent is NOT present in RequestContext!");
                    _output.WriteLine("This would cause activation spans to start a new trace.");
                    _output.WriteLine("Check that AddActivityPropagation() is called on both client and silo.");
                }
                else
                {
                    _output.WriteLine($"\n✓ traceparent IS present in RequestContext");

                    // Parse and validate the traceparent format
                    var parts = serverTraceInfo.TraceParentFromRequestContext.Split('-');
                    if (parts.Length >= 3)
                    {
                        var version = parts[0];
                        var traceId = parts[1];
                        var parentId = parts[2];

                        _output.WriteLine($"  Version: {version}");
                        _output.WriteLine($"  TraceId: {traceId}");
                        _output.WriteLine($"  ParentSpanId: {parentId}");

                        Assert.Equal(clientTraceId, traceId);
                        _output.WriteLine($"  ✓ TraceId matches client's TraceId");
                    }
                }

                // The traceparent should be present
                Assert.NotNull(serverTraceInfo.TraceParentFromRequestContext);
                Assert.NotEmpty(serverTraceInfo.TraceParentFromRequestContext);
                Assert.Contains(clientTraceId, serverTraceInfo.TraceParentFromRequestContext);
            }
            finally
            {
                clientActivity?.Stop();
                PrintActivityDiagnostics();
            }
        }

        /// <summary>
        /// CRITICAL TEST: Simulates a scenario where the calling code has no active Activity.
        /// In this case, the ActivityPropagationOutgoingGrainCallFilter's StartActivity will return null
        /// (because there's no parent activity and potentially no listener), and no traceparent will be injected.
        /// 
        /// This test verifies that when a grain call is made without an active Activity,
        /// the activation span will NOT have a parent and will start a new trace.
        /// 
        /// This reproduces the production issue where:
        /// - `activate grain` span has empty parentSpanId
        /// - The trace appears disconnected from the originating call
        /// </summary>
        [Fact]
        [TestCategory("BVT")]
        public async Task ActivationStartsNewTraceWhenCallerHasNoActivity()
        {
            Started.Clear();

            // Ensure no Activity is current
            var previousActivity = Activity.Current;
            Activity.Current = null;

            try
            {
                _output.WriteLine("=== TEST: Calling grain with NO Activity.Current ===");
                _output.WriteLine($"Activity.Current before call: {Activity.Current?.Id ?? "(null)"}");

                var grain = _fixture.GrainFactory.GetGrain<ITraceContextPropagationGrain>(Random.Shared.Next());
                var serverTraceInfo = await grain.GetTraceContextInfo();

                _output.WriteLine($"\n=== Server Response ===");
                _output.WriteLine($"Server TraceParent from RequestContext: {serverTraceInfo.TraceParentFromRequestContext ?? "(NULL - this is the bug!)"}");
                _output.WriteLine($"Server HasActivity: {serverTraceInfo.HasActivity}");
                _output.WriteLine($"Server TraceId: {serverTraceInfo.TraceId}");

                // Find the activation span
                var activationSpan = Started
                    .FirstOrDefault(a => a.OperationName == ActivityNames.ActivateGrain);

                _output.WriteLine($"\n=== Activation Span Analysis ===");
                if (activationSpan is not null)
                {
                    _output.WriteLine($"Activation Span TraceId: {activationSpan.TraceId}");
                    _output.WriteLine($"Activation Span ParentId: {activationSpan.ParentId ?? "(NULL - ROOT SPAN!)"}");
                    _output.WriteLine($"Activation Span ParentSpanId: {activationSpan.ParentSpanId}");

                    // When there's no caller activity, the activation span should have no parent
                    // This is the scenario that matches the production trace
                    if (string.IsNullOrEmpty(activationSpan.ParentId))
                    {
                        _output.WriteLine("\n⚠️ EXPECTED BEHAVIOR: Activation span is a ROOT span (no parent)");
                        _output.WriteLine("This matches the production issue where activate grain has empty parentSpanId");
                    }
                }
                else
                {
                    _output.WriteLine("Activation span NOT FOUND");
                }

                // Document the expected behavior: without a caller activity, traceparent won't be in RequestContext
                // and the activation will start a new trace
                if (string.IsNullOrEmpty(serverTraceInfo.TraceParentFromRequestContext))
                {
                    _output.WriteLine("\n✓ CONFIRMED: traceparent is NOT in RequestContext when caller has no Activity");
                    _output.WriteLine("This causes the activation span to start a new trace (no parent)");
                }
            }
            finally
            {
                Activity.Current = previousActivity;
                PrintActivityDiagnostics();
            }
        }

        /// <summary>
        /// Tests what happens when a grain call is made from within another grain that has no activity context.
        /// This simulates scenarios like:
        /// - Grain timers triggering calls
        /// - Reminder callbacks making grain calls
        /// - Stream subscription handlers
        /// - Background processing in grains
        /// </summary>
        [Fact]
        [TestCategory("BVT")]
        public async Task GrainToGrainCallWithoutActivityContext()
        {
            Started.Clear();

            // First, activate a grain that will make a nested call
            // We start with an activity to ensure the first grain is activated with proper context
            using var clientActivity = ActivitySources.ApplicationGrainSource.StartActivity("setup-activity");
            clientActivity?.Start();

            var callerGrain = _fixture.GrainFactory.GetGrain<ITraceContextPropagationGrain>(Random.Shared.Next());
            
            // First call to ensure the grain is activated
            _ = await callerGrain.GetTraceContextInfo();
            
            clientActivity?.Stop();
            Activity.Current = null;
            Started.Clear();

            _output.WriteLine("=== TEST: Grain-to-grain call with no ambient activity ===");

            try
            {
                // Now make another call - the grain is already activated
                // But if this call triggers a nested call within the grain, 
                // and there's no Activity.Current, the nested call won't have trace context
                var result = await callerGrain.GetNestedTraceContextInfo();

                _output.WriteLine($"Caller grain TraceId: {result.Local.TraceId}");
                _output.WriteLine($"Nested grain TraceId: {result.Nested.TraceId}");

                // Both should have activities (server creates one even without traceparent)
                Assert.True(result.Local.HasActivity);
                Assert.True(result.Nested.HasActivity);

                // But they should share the same trace because grain-to-grain calls preserve context
                Assert.Equal(result.Local.TraceId, result.Nested.TraceId);
            }
            finally
            {
                PrintActivityDiagnostics();
            }
        }

        /// <summary>
        /// CRITICAL TEST: This test reproduces the exact production issue where:
        /// - OpenTelemetry is configured to listen to Microsoft.Orleans.Lifecycle (for activation spans)
        /// - But NOT listening to Microsoft.Orleans.Application (for grain call spans)
        /// 
        /// When this happens:
        /// 1. Client makes a grain call
        /// 2. ActivityPropagationOutgoingGrainCallFilter tries to start an activity on ApplicationGrainSource
        /// 3. StartActivity returns NULL because there's no listener for that source
        /// 4. No traceparent is injected into RequestContext
        /// 5. Server receives message with no traceparent
        /// 6. Catalog.GetOrCreateActivation starts activation span with no parent
        /// 7. The activation span becomes a ROOT span (disconnected from the original trace)
        /// 
        /// Expected trace in production (BUG):
        ///   activate grain (NO PARENT - starts new trace!)
        ///     ├── register directory entry
        ///     └── execute OnActivateAsync
        /// 
        /// This test verifies that if Application source isn't being sampled,
        /// the activation span will have no parent.
        /// </summary>
        [Fact]
        [TestCategory("BVT")]
        public async Task ActivationSpanHasNoParentWhenApplicationSourceNotSampled()
        {
            // This test documents the expected behavior when only Lifecycle source is sampled
            // In our test fixture, we DO sample Application source, so this test verifies correct behavior
            // In production, if Application source is NOT sampled, the activation will be a root span
            
            Started.Clear();

            using var clientActivity = ActivitySources.ApplicationGrainSource.StartActivity("client-sampling-test");
            clientActivity?.Start();

            _output.WriteLine("=== TEST: Verifying sampling behavior ===");
            _output.WriteLine($"Client Activity created: {clientActivity is not null}");
            _output.WriteLine($"Client Activity ID: {clientActivity?.Id}");

            if (clientActivity is null)
            {
                _output.WriteLine("\n⚠️ CLIENT ACTIVITY IS NULL!");
                _output.WriteLine("This means Microsoft.Orleans.Application source is NOT being sampled.");
                _output.WriteLine("The ActivityPropagationOutgoingGrainCallFilter will NOT inject traceparent.");
                _output.WriteLine("This causes activation spans to start new traces (no parent).");
            }

            try
            {
                var grain = _fixture.GrainFactory.GetGrain<ITraceContextPropagationGrain>(Random.Shared.Next());
                var serverTraceInfo = await grain.GetTraceContextInfo();

                _output.WriteLine($"\n=== Results ===");
                _output.WriteLine($"Server has traceparent in RequestContext: {!string.IsNullOrEmpty(serverTraceInfo.TraceParentFromRequestContext)}");
                _output.WriteLine($"Server TraceParent: {serverTraceInfo.TraceParentFromRequestContext ?? "(NULL)"}");

                // Find spans by source
                var applicationSpans = Started.Where(a => a.Source.Name == ActivitySources.ApplicationGrainActivitySourceName).ToList();
                var lifecycleSpans = Started.Where(a => a.Source.Name == ActivitySources.LifecycleActivitySourceName).ToList();

                _output.WriteLine($"\n=== Spans by Source ===");
                _output.WriteLine($"Microsoft.Orleans.Application spans: {applicationSpans.Count}");
                foreach (var span in applicationSpans)
                {
                    _output.WriteLine($"  - {span.OperationName} (Kind: {span.Kind})");
                }
                _output.WriteLine($"Microsoft.Orleans.Lifecycle spans: {lifecycleSpans.Count}");
                foreach (var span in lifecycleSpans)
                {
                    _output.WriteLine($"  - {span.OperationName} (ParentId: {span.ParentId ?? "NULL - ROOT"})");
                }

                // Find the client outgoing span
                var clientOutgoingSpan = Started
                    .FirstOrDefault(a => a.Source.Name == ActivitySources.ApplicationGrainActivitySourceName
                                        && a.Kind == ActivityKind.Client);

                _output.WriteLine($"\n=== Key Finding ===");
                if (clientOutgoingSpan is null && applicationSpans.Count == 0)
                {
                    _output.WriteLine("⚠️ No Application source spans found!");
                    _output.WriteLine("If this happens in production (no listener for Application source),");
                    _output.WriteLine("the activation span will have no parent.");
                }

                // In our test environment, Application source IS sampled, so we should have spans
                Assert.NotNull(clientActivity);
                Assert.True(applicationSpans.Count > 0, 
                    "Application source spans should exist. In production, verify Microsoft.Orleans.Application is in your TracerProvider sources.");
            }
            finally
            {
                clientActivity?.Stop();
                PrintActivityDiagnostics();
            }
        }

        /// <summary>
        /// Diagnostic test that checks if all Orleans ActivitySources are being properly sampled.
        /// Run this to verify your OpenTelemetry configuration includes all necessary sources.
        /// </summary>
        [Fact]
        [TestCategory("BVT")]
        public async Task DiagnoseActivitySourceSampling()
        {
            Started.Clear();

            _output.WriteLine("=== Checking ActivitySource Sampling ===\n");

            // Test each Orleans ActivitySource
            var sourcesToTest = new[]
            {
                (ActivitySources.ApplicationGrainSource, "Microsoft.Orleans.Application"),
                (ActivitySources.RuntimeGrainSource, "Microsoft.Orleans.Runtime"),
                (ActivitySources.LifecycleGrainSource, "Microsoft.Orleans.Lifecycle"),
                (ActivitySources.StorageGrainSource, "Microsoft.Orleans.Storage"),
            };

            foreach (var (source, name) in sourcesToTest)
            {
                var testActivity = source.StartActivity($"test-{name}", ActivityKind.Internal);
                var isSampled = testActivity is not null;
                _output.WriteLine($"{name}: {(isSampled ? "✓ SAMPLED" : "✗ NOT SAMPLED")}");
                testActivity?.Stop();
            }

            _output.WriteLine("\n=== Implications ===");
            _output.WriteLine("For proper trace propagation, you need to sample:");
            _output.WriteLine("  - Microsoft.Orleans.Application (for grain call spans)");
            _output.WriteLine("  - Microsoft.Orleans.Lifecycle (for activation/deactivation spans)");
            _output.WriteLine("\nIf Application is NOT sampled but Lifecycle IS:");
            _output.WriteLine("  - Grain call spans won't be created");
            _output.WriteLine("  - traceparent won't be injected into messages");
            _output.WriteLine("  - Activation spans will start new traces (no parent)");

            // Make an actual grain call to verify end-to-end
            _output.WriteLine("\n=== End-to-End Test ===");
            using var clientActivity = ActivitySources.ApplicationGrainSource.StartActivity("e2e-test");
            if (clientActivity is null)
            {
                _output.WriteLine("⚠️ Could not create client activity - Application source not sampled!");
            }
            else
            {
                clientActivity.Start();
                var grain = _fixture.GrainFactory.GetGrain<ITraceContextPropagationGrain>(Random.Shared.Next());
                var info = await grain.GetTraceContextInfo();

                _output.WriteLine($"Server received traceparent: {!string.IsNullOrEmpty(info.TraceParentFromRequestContext)}");
                
                var activationSpan = Started.FirstOrDefault(a => a.OperationName == ActivityNames.ActivateGrain);
                if (activationSpan is not null)
                {
                    _output.WriteLine($"Activation span has parent: {!string.IsNullOrEmpty(activationSpan.ParentId)}");
                    if (string.IsNullOrEmpty(activationSpan.ParentId))
                    {
                        _output.WriteLine("⚠️ BUG: Activation span is a root span!");
                    }
                }

                clientActivity.Stop();
            }

            PrintActivityDiagnostics();
        }

        /// <summary>
        /// CRITICAL TEST: Simulates a call from an ASP.NET HTTP request handler.
        /// In production, grain calls often originate from HTTP endpoints where:
        /// 1. ASP.NET creates an HTTP activity (e.g., "GET /api/users/{id}")
        /// 2. The controller calls a grain
        /// 3. Orleans should propagate the HTTP trace context to the grain
        /// 
        /// This test verifies that if Activity.Current exists (from HTTP/ASP.NET),
        /// Orleans properly propagates it to the grain call and activation spans.
        /// </summary>
        [Fact]
        [TestCategory("BVT")]
        public async Task TraceContextPropagatedFromHttpActivityToGrainCall()
        {
            Started.Clear();

            // Simulate an ASP.NET HTTP activity (different source than Orleans)
            using var httpActivitySource = new ActivitySource("Microsoft.AspNetCore", "1.0.0");
            var httpListener = new ActivityListener
            {
                ShouldListenTo = src => src.Name == "Microsoft.AspNetCore",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            };
            ActivitySource.AddActivityListener(httpListener);

            using var httpActivity = httpActivitySource.StartActivity("GET /api/users/{id}", ActivityKind.Server);
            httpActivity?.Start();

            Assert.NotNull(httpActivity);
            var httpTraceId = httpActivity.TraceId.ToString();

            _output.WriteLine("=== TEST: HTTP to Grain call trace propagation ===");
            _output.WriteLine($"HTTP Activity TraceId: {httpTraceId}");
            _output.WriteLine($"HTTP Activity SpanId: {httpActivity.SpanId}");
            _output.WriteLine($"Activity.Current: {Activity.Current?.Id}");

            try
            {
                var grain = _fixture.GrainFactory.GetGrain<ITraceContextPropagationGrain>(Random.Shared.Next());
                var serverTraceInfo = await grain.GetTraceContextInfo();

                _output.WriteLine($"\n=== Server Response ===");
                _output.WriteLine($"Server TraceId: {serverTraceInfo.TraceId}");
                _output.WriteLine($"Server TraceParent: {serverTraceInfo.TraceParentFromRequestContext ?? "(NULL)"}");
                _output.WriteLine($"Server HasActivity: {serverTraceInfo.HasActivity}");

                // Find the activation span
                var activationSpan = Started
                    .FirstOrDefault(a => a.OperationName == ActivityNames.ActivateGrain);

                _output.WriteLine($"\n=== Activation Span ===");
                if (activationSpan is not null)
                {
                    _output.WriteLine($"TraceId: {activationSpan.TraceId}");
                    _output.WriteLine($"ParentId: {activationSpan.ParentId ?? "(NULL - ROOT!)"}");
                }

                // Server should have the same TraceId as the HTTP activity
                Assert.Equal(httpTraceId, serverTraceInfo.TraceId);

                // Activation span should also share the same TraceId
                if (activationSpan is not null)
                {
                    Assert.Equal(httpTraceId, activationSpan.TraceId.ToString());
                    Assert.False(string.IsNullOrEmpty(activationSpan.ParentId), 
                        "Activation span should have a parent when called from HTTP activity");
                }
            }
            finally
            {
                httpActivity?.Stop();
                PrintActivityDiagnostics();
            }
        }

        /// <summary>
        /// DIAGNOSTIC TEST: Forces grain activation on a specific (different) silo to test cross-silo
        /// trace context propagation. This simulates production scenarios where the placement
        /// director places the grain on a different silo than the one receiving the initial call.
        /// 
        /// In production with AddDistributedGrainDirectory():
        /// 1. Client sends call to Silo A
        /// 2. Placement decides grain should be on Silo B
        /// 3. Message is forwarded to Silo B
        /// 4. Silo B creates the activation
        /// 
        /// The question: Is trace context preserved through this forwarding?
        /// </summary>
        [Fact]
        [TestCategory("BVT")]
        public async Task TraceContextPreservedWhenGrainPlacedOnDifferentSilo()
        {
            Started.Clear();

            using var clientActivity = ActivitySources.ApplicationGrainSource.StartActivity("cross-silo-test");
            clientActivity?.Start();

            Assert.NotNull(clientActivity);
            var clientTraceId = clientActivity.TraceId.ToString();

            _output.WriteLine("=== TEST: Cross-silo grain activation ===");
            _output.WriteLine($"Client TraceId: {clientTraceId}");

            // Get the silos
            var silos = _fixture.HostedCluster.GetActiveSilos().ToList();
            _output.WriteLine($"Active silos: {silos.Count}");
            foreach (var silo in silos)
            {
                _output.WriteLine($"  - {silo.SiloAddress}");
            }

            if (silos.Count < 2)
            {
                _output.WriteLine("⚠️ Need at least 2 silos for this test");
                return;
            }

            try
            {
                // Use placement hint to force grain onto a specific silo
                var targetSilo = silos[1].SiloAddress; // Use second silo
                RequestContext.Set(IPlacementDirector.PlacementHintKey, targetSilo);
                _output.WriteLine($"Placement hint set to: {targetSilo}");

                // This grain uses RandomPlacement, so the hint should work
                var grain = _fixture.GrainFactory.GetGrain<ITraceContextPropagationGrain>(Random.Shared.Next());
                var serverTraceInfo = await grain.GetTraceContextInfo();

                _output.WriteLine($"\n=== Results ===");
                _output.WriteLine($"Server TraceId: {serverTraceInfo.TraceId}");
                _output.WriteLine($"Server TraceParent: {serverTraceInfo.TraceParentFromRequestContext ?? "(NULL!)"}");
                _output.WriteLine($"Server HasActivity: {serverTraceInfo.HasActivity}");

                // Find the activation span
                var activationSpan = Started.FirstOrDefault(a => a.OperationName == ActivityNames.ActivateGrain);
                if (activationSpan is not null)
                {
                    _output.WriteLine($"\nActivation Span:");
                    _output.WriteLine($"  TraceId: {activationSpan.TraceId}");
                    _output.WriteLine($"  ParentId: {activationSpan.ParentId ?? "(NULL - ROOT!)"}");
                    _output.WriteLine($"  HasRemoteParent: {activationSpan.HasRemoteParent}");
                }

                // CRITICAL: Even with cross-silo placement, trace should be preserved
                Assert.Equal(clientTraceId, serverTraceInfo.TraceId);
                
                if (activationSpan is not null)
                {
                    Assert.Equal(clientTraceId, activationSpan.TraceId.ToString());
                    Assert.False(string.IsNullOrEmpty(activationSpan.ParentId),
                        "Activation span should have a parent even with cross-silo placement");
                }
            }
            finally
            {
                RequestContext.Clear();
                clientActivity?.Stop();
                PrintActivityDiagnostics();
            }
        }

        /// <summary>
        /// DIAGNOSTIC TEST: Tests trace context when a grain-to-grain call crosses silo boundaries.
        /// This is common in production where Grain A on Silo 1 calls Grain B on Silo 2.
        /// </summary>
        [Fact]
        [TestCategory("BVT")]
        public async Task TraceContextPreservedInCrossSiloGrainToGrainCall()
        {
            Started.Clear();

            using var clientActivity = ActivitySources.ApplicationGrainSource.StartActivity("cross-silo-g2g-test");
            clientActivity?.Start();

            Assert.NotNull(clientActivity);
            var clientTraceId = clientActivity.TraceId.ToString();

            var silos = _fixture.HostedCluster.GetActiveSilos().ToList();
            if (silos.Count < 2)
            {
                _output.WriteLine("⚠️ Need at least 2 silos for this test");
                return;
            }

            _output.WriteLine("=== TEST: Cross-silo grain-to-grain call ===");
            _output.WriteLine($"Client TraceId: {clientTraceId}");

            try
            {
                // Place first grain on silo 1
                RequestContext.Set(IPlacementDirector.PlacementHintKey, silos[0].SiloAddress);
                var grain1 = _fixture.GrainFactory.GetGrain<ITraceContextPropagationGrain>(Random.Shared.Next());
                _ = await grain1.GetTraceContextInfo(); // Activate on silo 1
                RequestContext.Clear();

                // Now make a nested call - the nested grain should go to silo 2
                RequestContext.Set(IPlacementDirector.PlacementHintKey, silos[1].SiloAddress);
                var (local, nested) = await grain1.GetNestedTraceContextInfo();
                RequestContext.Clear();

                _output.WriteLine($"\n=== Results ===");
                _output.WriteLine($"Local grain TraceId: {local.TraceId}");
                _output.WriteLine($"Nested grain TraceId: {nested.TraceId}");
                _output.WriteLine($"Nested grain HasRemoteParent: {nested.IsRemote}");

                // Both should share same trace ID
                Assert.Equal(clientTraceId, local.TraceId);
                Assert.Equal(clientTraceId, nested.TraceId);
            }
            finally
            {
                RequestContext.Clear();
                clientActivity?.Stop();
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
                sb.AppendLine($"  SpanId: {activity.SpanId}");
                sb.AppendLine($"  ParentSpanId: {activity.ParentSpanId}");
                sb.AppendLine($"  ParentId: {activity.ParentId}");
                sb.AppendLine($"  Kind: {activity.Kind}");
                sb.AppendLine($"  HasRemoteParent: {activity.HasRemoteParent}");
                sb.AppendLine();
            }

            _output.WriteLine(sb.ToString());
        }
    }
}

