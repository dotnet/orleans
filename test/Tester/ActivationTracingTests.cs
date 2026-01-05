using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Placement;
using Orleans.Runtime;
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
    public class ActivationTracingTests : OrleansTestingBase, IClassFixture<ActivationTracingTests.Fixture>
    {
        private static string ActivationSourceName = ActivitySources.RuntimeActivitySourceName;
        private static readonly ConcurrentBag<Activity> Started = new();

        static ActivationTracingTests()
        {
            var listener = new ActivityListener
            {
                ShouldListenTo = src => true /*src.Name == ActivationSourceName*/,
                Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllData,
                SampleUsingParentId = (ref ActivityCreationOptions<string> options) => ActivitySamplingResult.AllData,
                ActivityStarted = activity => Started.Add(activity),
            };
            ActivitySource.AddActivityListener(listener);
        }

        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
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

            using var parent = new Activity("test-parent");
            parent.Start();
            try
            {
                var grain = _fixture.GrainFactory.GetGrain<IFilteredActivityGrain>(Random.Shared.Next());
                // First call should force activation
                var _ = await grain.GetActivityId();

                // Expect at least one activation-related activity
                var activationActivities = Started.Where(a => a.Source.Name == ActivationSourceName).ToList();
                Assert.True(activationActivities.Count > 0, "Expected activation tracing activity to be created, but none were observed.");

                // Verify all expected spans are present and properly parented under test-parent
                var testParentTraceId = parent.TraceId.ToString();

                // Find the placement span - should be parented to the grain call which is parented to test-parent
                var placementSpan = Started.FirstOrDefault(a => a.OperationName == "orleans.placement");
                Assert.NotNull(placementSpan);
                Assert.Equal(testParentTraceId, placementSpan.TraceId.ToString());

                // Find the placement filter span - should share the same trace ID as test-parent
                var placementFilterSpan = Started.FirstOrDefault(a => a.OperationName == "orleans.placement.filter");
                Assert.NotNull(placementFilterSpan);
                Assert.Equal(testParentTraceId, placementFilterSpan.TraceId.ToString());
                Assert.Equal("TracingTestPlacementFilterStrategy", placementFilterSpan.Tags.FirstOrDefault(t => t.Key == "orleans.placement.filter.type").Value);

                // Find the activation span - should be parented to the grain call which is parented to test-parent
                var activationSpan = Started.FirstOrDefault(a => a.OperationName == "orleans.activation");
                Assert.NotNull(activationSpan);
                Assert.Equal(testParentTraceId, activationSpan.TraceId.ToString());

                // Find the OnActivateAsync span - should be parented to the activation span
                var onActivateSpan = Started.FirstOrDefault(a => a.OperationName == "orleans.activation.on-activate");
                Assert.NotNull(onActivateSpan);
                Assert.Equal(testParentTraceId, onActivateSpan.TraceId.ToString());
                Assert.Equal(activationSpan.SpanId.ToString(), onActivateSpan.ParentSpanId.ToString());

                // Find the directory register span - should be parented to activation span
                var directoryRegisterSpan = Started.FirstOrDefault(a => a.OperationName == "orleans.directory.register");
                Assert.NotNull(directoryRegisterSpan);
                Assert.Equal(testParentTraceId, directoryRegisterSpan.TraceId.ToString());
                Assert.Equal(activationSpan.SpanId.ToString(), directoryRegisterSpan.ParentSpanId.ToString());

                // Find the directory lookup spans - should share the same trace ID as test-parent
                var lookupSpans = Started.Where(a =>
                    a.OperationName == "IGrainDirectoryPartition/LookupAsync" &&
                    a.Source.Name == ActivitySources.RuntimeActivitySourceName).ToList();
                Assert.True(lookupSpans.Count > 0, "Expected at least one directory lookup span");
                foreach (var lookupSpan in lookupSpans)
                {
                    Assert.Equal(testParentTraceId, lookupSpan.TraceId.ToString());
                }

                // Find the directory register RPC spans - should share the same trace ID as test-parent
                var registerSpans = Started.Where(a =>
                    a.OperationName == "IGrainDirectoryPartition/RegisterAsync" &&
                    a.Source.Name == ActivitySources.RuntimeActivitySourceName).ToList();
                Assert.True(registerSpans.Count > 0, "Expected at least one directory register RPC span");
                foreach (var registerSpan in registerSpans)
                {
                    Assert.Equal(testParentTraceId, registerSpan.TraceId.ToString());
                }
            }
            finally
            {
                parent.Stop();
                PrintActivityDiagnostics();
            }
        }

        [Fact]
        [TestCategory("BVT")]
        public async Task PersistentStateReadSpanIsCreatedDuringActivation()
        {
            Started.Clear();

            using var parent = new Activity("test-parent-storage");
            parent.Start();
            try
            {
                var grain = _fixture.GrainFactory.GetGrain<IPersistentStateActivityGrain>(Random.Shared.Next());
                // First call should force activation which triggers state read
                var _ = await grain.GetActivityId();

                // Expect at least one activation-related activity
                var activationActivities = Started.Where(a => a.Source.Name == ActivationSourceName).ToList();
                Assert.True(activationActivities.Count > 0, "Expected activation tracing activity to be created, but none were observed.");

                // Verify all expected spans are present and properly parented under test-parent
                var testParentTraceId = parent.TraceId.ToString();

                // Find the activation span - should be parented to the grain call which is parented to test-parent
                var activationSpan = Started.FirstOrDefault(a => a.OperationName == "orleans.activation" && a.Tags.First(kv => kv.Key == "orleans.grain.type").Value == "persistentstateactivity");
                Assert.NotNull(activationSpan);
                Assert.Equal(testParentTraceId, activationSpan.TraceId.ToString());

                // Find the storage read span - should share the same trace ID as test-parent
                var storageReadSpan = Started.FirstOrDefault(a => a.OperationName == "orleans.storage.read");
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

    #region Test Grain with Persistent State for Tracing

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
}
