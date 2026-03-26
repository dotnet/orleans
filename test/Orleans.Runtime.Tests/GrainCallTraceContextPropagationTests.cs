using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Orleans;
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

            var nestedGrain = GrainFactory.GetGrain<ITraceContextPropagationGrain>(this.GetPrimaryKeyLong() + 1);
            var nestedInfo = await nestedGrain.GetTraceContextInfo();

            return (localInfo, nestedInfo);
        }
    }

}
