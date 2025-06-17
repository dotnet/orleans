using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Orleans.Runtime;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.General
{
    /// <summary>
    /// Tests for distributed tracing and activity propagation across Orleans grain calls.
    /// 
    /// Orleans supports distributed tracing through .NET's Activity API, which is compatible with OpenTelemetry.
    /// These tests verify that:
    /// - Activity context (trace ID, span ID, trace state) is properly propagated from client to grain
    /// - Both W3C and Hierarchical activity ID formats are supported
    /// - Baggage items (key-value pairs) are correctly transmitted across grain boundaries
    /// - Activity propagation works correctly both from external clients and between grains
    /// 
    /// The ActivityPropagationGrainCallFilter is responsible for creating child activities for grain calls
    /// and ensuring proper context propagation throughout the distributed system.
    /// </summary>
    public class ActivityPropagationTests : OrleansTestingBase, IClassFixture<ActivityPropagationTests.Fixture>
    {
        private static readonly ActivityListener Listener;

        static ActivityPropagationTests()
        {
            // Configure an ActivityListener to monitor grain call activities
            // This listener specifically targets activities created by Orleans for grain calls
            Listener = new()
            {
                ShouldListenTo = p => p.Name == ActivityPropagationGrainCallFilter.ApplicationGrainActivitySourceName,
                Sample = Sample,
                SampleUsingParentId = SampleUsingParentId,
            };

            static ActivitySamplingResult Sample(ref ActivityCreationOptions<ActivityContext> options)
            {
                // Accessing TraceId during sampling is important to ensure the Activity system
                // properly initializes the trace context. This reproduces a specific scenario
                // where SetParentId might not work correctly if TraceId isn't accessed.
                var _ = options.TraceId; 
                return ActivitySamplingResult.PropagationData;
            };

            static ActivitySamplingResult SampleUsingParentId(ref ActivityCreationOptions<string> options)
            {
                //Trace id has to be accessed in sample to reproduce the scenario when SetParentId does not work
                var _ = options.TraceId;
                return ActivitySamplingResult.PropagationData;
            };
        }

        /// <summary>
        /// Test fixture that configures an Orleans cluster with activity propagation enabled.
        /// Both the silo and client are configured to support distributed tracing.
        /// </summary>
        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.ConfigureHostConfiguration(TestDefaultConfiguration.ConfigureHostConfiguration);
                builder.AddSiloBuilderConfigurator<SiloInvokerTestSiloBuilderConfigurator>();
                builder.AddClientBuilderConfigurator<ClientConfigurator>();
            }

            private class SiloInvokerTestSiloBuilderConfigurator : ISiloConfigurator
            {
                public void Configure(ISiloBuilder hostBuilder) =>
                    hostBuilder
                        // Enable activity propagation on the silo to support distributed tracing
                        .AddActivityPropagation()
                        // Configure memory storage providers for testing
                        .AddMemoryGrainStorageAsDefault()
                        .AddMemoryGrainStorage("PubSubStore");
            }

            private class ClientConfigurator : IClientBuilderConfigurator
            {
                public void Configure(IConfiguration configuration, IClientBuilder clientBuilder) =>
                    clientBuilder
                        // Enable activity propagation on the client to participate in distributed traces
                        .AddActivityPropagation();
            }
        }

        private readonly ActivityIdFormat defaultIdFormat;
        private readonly Fixture fixture;

        public ActivityPropagationTests(Fixture fixture)
        {
            defaultIdFormat = Activity.DefaultIdFormat;
            this.fixture = fixture;
            ActivitySource.AddActivityListener(Listener);
        }

        /// <summary>
        /// Tests that grain calls create new activities when no parent activity exists.
        /// Verifies both W3C (standard format) and Hierarchical (legacy .NET format) activity ID formats.
        /// When no parent activity is present, Orleans creates a new root activity for the grain call.
        /// </summary>
        [Theory]
        [InlineData(ActivityIdFormat.W3C)]
        [InlineData(ActivityIdFormat.Hierarchical)]
        [TestCategory("BVT")]
        public async Task WithoutParentActivity(ActivityIdFormat idFormat)
        {
            Activity.DefaultIdFormat = idFormat;

            await Test(fixture.GrainFactory);
            await Test(fixture.Client);

            static async Task Test(IGrainFactory grainFactory)
            {
                var grain = grainFactory.GetGrain<IActivityGrain>(Random.Shared.Next());

                var result = await grain.GetActivityId();

                Assert.NotNull(result);
                Assert.NotEmpty(result.Id);
                Assert.Null(result.TraceState);
            }
        }

        /// <summary>
        /// Tests activity propagation with W3C format when a parent activity exists.
        /// Verifies that:
        /// - The trace ID from the parent activity is preserved in the grain
        /// - Trace state (vendor-specific tracing data) is correctly propagated
        /// - Baggage items (contextual key-value pairs) are transmitted to the grain
        /// This ensures distributed traces can span across client-to-grain boundaries.
        /// </summary>
        [Fact]
        [TestCategory("BVT")]
        public async Task WithParentActivity_W3C()
        {
            Activity.DefaultIdFormat = ActivityIdFormat.W3C;

            var activity = new Activity("SomeName");
            activity.TraceStateString = "traceState";
            activity.AddBaggage("foo", "bar");
            activity.Start();

            try
            {
                await Test(fixture.GrainFactory);
                await Test(fixture.Client);
            }
            finally
            {
                activity.Stop();
            }

            async Task Test(IGrainFactory grainFactory)
            {
                var grain = grainFactory.GetGrain<IActivityGrain>(Random.Shared.Next());

                var result = await grain.GetActivityId();

                Assert.NotNull(result);
                Assert.NotNull(result.Id);
                Assert.Contains(activity.TraceId.ToHexString(), result.Id); // ensure, that trace id is persisted.
                Assert.Equal(activity.TraceStateString, result.TraceState);
                Assert.Equal(activity.Baggage, result.Baggage);
            }
        }

        /// <summary>
        /// Tests activity propagation with Hierarchical format (legacy .NET format).
        /// Verifies that:
        /// - The grain's activity ID is a child of the parent activity (starts with parent ID)
        /// - Baggage items are correctly propagated
        /// Note: Hierarchical format doesn't support trace state like W3C format does.
        /// </summary>
        [Fact]
        [TestCategory("BVT")]
        public async Task WithParentActivity_Hierarchical()
        {
            Activity.DefaultIdFormat = ActivityIdFormat.Hierarchical;

            var activity = new Activity("SomeName");
            activity.AddBaggage("foo", "bar");
            activity.Start();

            try
            {
                await Test(fixture.GrainFactory);
                await Test(fixture.Client);
            }
            finally
            {
                activity.Stop();
            }

            async Task Test(IGrainFactory grainFactory)
            {
                var grain = grainFactory.GetGrain<IActivityGrain>(Random.Shared.Next());

                var result = await grain.GetActivityId();

                Assert.NotNull(result);
                Assert.NotNull(result.Id);
                Assert.StartsWith(activity.Id, result.Id);
                Assert.Equal(activity.Baggage, result.Baggage);
            }
        }
    }
}
