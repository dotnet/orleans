using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Orleans.CodeGeneration;
using Orleans.Diagnostics;
using Orleans.Runtime;
using Orleans.Runtime.Diagnostics;
using Orleans.TestingHost.Diagnostics;
using TestExtensions;
using UnitTests.Grains;
using Xunit;

namespace UnitTests.ActivationsLifeCycleTests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
public sealed class ActivationStartupTests(ActivationStartupTestFixture fixture)
    : IClassFixture<ActivationStartupTestFixture>
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public void FailureBeforeStartAbortsAndUnregistersPreparedContext()
    {
        var (grainId, scenario) = fixture.CreateScenario(
            ActivationStartupCompletion.ImmediateSuccess,
            ActivationStartupDisposal.Synchronous);
        var failActivityCreation = new AsyncLocal<bool>();
        var expected = new InvalidOperationException("activity-start-fault");
        using var listener = new ActivityListener
        {
            ShouldListenTo = static source =>
                source.Name == ActivitySources.LifecycleActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                failActivityCreation.Value ? throw expected : ActivitySamplingResult.None,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) =>
                failActivityCreation.Value ? throw expected : ActivitySamplingResult.None,
        };
        ActivitySource.AddActivityListener(listener);

        try
        {
            failActivityCreation.Value = true;
            var actual = Assert.Throws<InvalidOperationException>(() => fixture.StartActivation(grainId));

            Assert.Same(expected, actual);
            Assert.Null(fixture.ActivationDirectory.FindTarget(grainId));
            Assert.Equal(0, scenario.CreateCount);
            Assert.Equal(0, scenario.ConstructorCount);
            Assert.Equal(0, scenario.OnActivateCount);
        }
        finally
        {
            failActivityCreation.Value = false;
            fixture.RemoveScenario(grainId);
        }
    }

    [Fact]
    public async Task AsyncActivation_OrdersLifecycleCallbacksAndCleanup()
    {
        var (grainId, scenario) = fixture.CreateScenario(
            ActivationStartupCompletion.AsynchronousSuccess,
            ActivationStartupDisposal.Asynchronous);
        using var lifecycleSubscription = fixture.ObserveLifecycle();
        var expected = new[]
        {
            "Created",
            "LifecycleStartLow",
            "LifecycleStartHigh",
            "OnActivateEntered",
            "OnActivateCompleted",
            "Activated",
            "RequestInvoked",
            "Deactivating",
            "OnDeactivateEntered",
            "OnDeactivateCompleted",
            "LifecycleStopHigh",
            "LifecycleStopLow",
            "ActivatorDisposeStarted",
            "ActivatorDisposeCompleted",
            "ScopeDisposed",
            "Deactivated",
        };
        var eventTasks = expected.Select(scenario.WaitForEvent).ToArray();
        ActivationData? context = null;

        try
        {
            context = fixture.StartActivation(grainId);
            await scenario.WaitForEvent("OnActivateEntered").WaitAsync(Timeout, TestContext.Current.CancellationToken);

            scenario.ReleaseActivation();
            await scenario.WaitForEvent("Activated").WaitAsync(Timeout, TestContext.Current.CancellationToken);

            var (message, _) = fixture.CreateRequest(
                context,
                scenario,
                payload: "lifecycle",
                requestContextValue: "lifecycle-request",
                recordResponse: false);
            context.ReceiveMessage(message);
            await scenario.WaitForEvent("RequestInvoked").WaitAsync(Timeout, TestContext.Current.CancellationToken);

            context.Deactivate(new(DeactivationReasonCode.ApplicationRequested, "Lifecycle test complete."));
            await scenario.WaitForEvent("ActivatorDisposeStarted").WaitAsync(Timeout, TestContext.Current.CancellationToken);
            Assert.Equal(0, scenario.DisposeCompletedCount);
            Assert.Equal(0, scenario.ScopeDisposeCount);

            scenario.ReleaseDisposal();
            await scenario.WaitForEvent("Deactivated").WaitAsync(Timeout, TestContext.Current.CancellationToken);
            await Task.WhenAll(eventTasks).WaitAsync(Timeout, TestContext.Current.CancellationToken);

            Assert.Equal(expected, scenario.Events.Select(static entry => entry.Name));
            Assert.All(expected, name => Assert.Equal(1, scenario.GetEventCount(name)));
            Assert.Equal(1, scenario.ConstructorCount);
            Assert.Equal(1, scenario.OnActivateCount);
            Assert.Equal(1, scenario.OnDeactivateCount);
            Assert.Equal(1, scenario.RequestInvocationCount);
            AssertIdentity(scenario, grainId);
            Assert.Null(fixture.ActivationDirectory.FindTarget(grainId));
        }
        finally
        {
            await CleanupAsync(context, scenario);
            fixture.RemoveScenario(grainId);
        }
    }

    [Fact]
    public async Task RequestAdmittedDuringAsyncActivation_CompletesAfterStartup()
    {
        var (grainId, scenario) = fixture.CreateScenario(
            ActivationStartupCompletion.AsynchronousSuccess,
            ActivationStartupDisposal.Synchronous);
        using var lifecycleSubscription = fixture.ObserveLifecycle();
        var entered = scenario.WaitForEvent("OnActivateEntered");
        var response = scenario.WaitForEvent("Response");
        ActivationData? context = null;

        try
        {
            context = fixture.StartActivation(grainId);
            await entered.WaitAsync(Timeout, TestContext.Current.CancellationToken);
            var (message, request) = fixture.CreateRequest(
                context,
                scenario,
                payload: "success",
                requestContextValue: "request-success",
                recordResponse: true);

            context.ReceiveMessage(message);
            scenario.Record("RequestAdmitted", context);

            Assert.Equal(0, scenario.RequestInvocationCount);
            Assert.Null(request.Result);

            scenario.ReleaseActivation();
            await response.WaitAsync(Timeout, TestContext.Current.CancellationToken);

            Assert.Equal("success:request-success", request.Result);
            Assert.Equal(1, scenario.RequestInvocationCount);
            Assert.Equal("request-success", scenario.RequestContextValue);
            AssertEventSubset(
                scenario,
                "RequestAdmitted",
                "OnActivateCompleted",
                "Activated",
                "RequestInvoked",
                "Response");
            AssertIdentity(scenario, grainId);
        }
        finally
        {
            await CleanupAsync(context, scenario);
            fixture.RemoveScenario(grainId);
        }
    }

    [Fact]
    public async Task RequestAdmittedDuringAsyncActivationFailure_IsRejectedWithoutInvocation()
    {
        var (grainId, scenario) = fixture.CreateScenario(
            ActivationStartupCompletion.AsynchronousFailure,
            ActivationStartupDisposal.Synchronous);
        using var lifecycleSubscription = fixture.ObserveLifecycle();
        using var collector = new DiagnosticEventCollector(DispatcherEvents.ListenerName);
        var entered = scenario.WaitForEvent("OnActivateEntered");
        var failed = scenario.WaitForEvent("OnActivateFailed");
        var deactivated = scenario.WaitForEvent("Deactivated");
        ActivationData? context = null;

        try
        {
            context = fixture.StartActivation(grainId);
            await entered.WaitAsync(Timeout, TestContext.Current.CancellationToken);
            var (message, request) = fixture.CreateRequest(
                context,
                scenario,
                payload: "must-not-run",
                requestContextValue: "request-failure",
                recordResponse: true);
            var rejected = collector.WaitForEventAsync(
                nameof(DispatcherEvents.Rejected),
                diagnosticEvent => diagnosticEvent.Payload is DispatcherEvents.Rejected rejection
                    && ReferenceEquals(rejection.Message, message),
                Timeout,
                TestContext.Current.CancellationToken);

            context.ReceiveMessage(message);
            scenario.Record("RequestAdmitted", context);
            Assert.Equal(0, scenario.RequestInvocationCount);

            scenario.ReleaseActivation();
            await failed.WaitAsync(Timeout, TestContext.Current.CancellationToken);
            var rejectionEvent = await rejected;
            await deactivated.WaitAsync(Timeout, TestContext.Current.CancellationToken);

            var rejection = Assert.IsType<DispatcherEvents.Rejected>(rejectionEvent.Payload);
            var exception = Assert.IsType<InvalidOperationException>(rejection.Exception);
            Assert.Equal("activate-fault", exception.Message);
            Assert.Equal(Message.RejectionTypes.Transient, rejection.RejectionType);
            Assert.Contains("Failed to activate grain", rejection.Reason);
            Assert.Same(message, rejection.Message);
            Assert.Null(request.Result);
            Assert.Equal(0, scenario.RequestInvocationCount);
            Assert.Equal(0, scenario.GetEventCount("Activated"));
            Assert.Equal(1, scenario.GetEventCount("Deactivating"));
            Assert.Equal(1, scenario.GetEventCount("Deactivated"));
            Assert.Equal(1, scenario.DisposeStartedCount);
            Assert.Equal(1, scenario.DisposeCompletedCount);
            Assert.Equal(1, scenario.ScopeDisposeCount);
            Assert.Null(fixture.ActivationDirectory.FindTarget(grainId));
            AssertIdentity(scenario, grainId);
        }
        finally
        {
            await CleanupAsync(context, scenario);
            fixture.RemoveScenario(grainId);
        }
    }

    [Fact]
    public async Task CancellationDuringAsyncActivation_AbortsAndCleansUpExactlyOnce()
    {
        var (grainId, scenario) = fixture.CreateScenario(
            ActivationStartupCompletion.Cancellation,
            ActivationStartupDisposal.Asynchronous);
        using var lifecycleSubscription = fixture.ObserveLifecycle();
        var entered = scenario.WaitForEvent("OnActivateEntered");
        var cancellationObserved = scenario.WaitForEvent("CancellationObserved");
        var disposeStarted = scenario.WaitForEvent("ActivatorDisposeStarted");
        var deactivated = scenario.WaitForEvent("Deactivated");
        ActivationData? context = null;

        try
        {
            context = fixture.StartActivation(grainId);
            await entered.WaitAsync(Timeout, TestContext.Current.CancellationToken);

            context.Deactivate(new(DeactivationReasonCode.RuntimeRequested, "Cancel startup."));
            await cancellationObserved.WaitAsync(Timeout, TestContext.Current.CancellationToken);
            await disposeStarted.WaitAsync(Timeout, TestContext.Current.CancellationToken);
            Assert.Equal(0, scenario.DisposeCompletedCount);
            Assert.Equal(0, scenario.ScopeDisposeCount);

            scenario.ReleaseDisposal();
            await deactivated.WaitAsync(Timeout, TestContext.Current.CancellationToken);

            Assert.Equal(0, scenario.GetEventCount("Activated"));
            Assert.Equal(0, scenario.RequestInvocationCount);
            Assert.Equal(0, scenario.OnDeactivateCount);
            Assert.Equal(1, scenario.GetEventCount("Deactivating"));
            Assert.Equal(1, scenario.GetEventCount("Deactivated"));
            Assert.Equal(1, scenario.DisposeStartedCount);
            Assert.Equal(1, scenario.DisposeCompletedCount);
            Assert.Equal(1, scenario.ScopeDisposeCount);
            Assert.Null(fixture.ActivationDirectory.FindTarget(grainId));
            AssertIdentity(scenario, grainId);
        }
        finally
        {
            await CleanupAsync(context, scenario);
            fixture.RemoveScenario(grainId);
        }
    }

    [Fact]
    public async Task SiloShutdownDuringAsyncActivation_CancelsAndCleansUpExactlyOnce()
    {
        var shutdownFixture = new ActivationStartupTestFixture();
        await shutdownFixture.InitializeAsync();
        var (grainId, scenario) = shutdownFixture.CreateScenario(
            ActivationStartupCompletion.Cancellation,
            ActivationStartupDisposal.Asynchronous);
        var shutdownHooks = shutdownFixture.Hooks;
        var activationDirectory = shutdownFixture.ActivationDirectory;
        var primarySilo = shutdownFixture.PrimarySilo;
        using var lifecycleSubscription = shutdownFixture.ObserveLifecycle();
        var entered = scenario.WaitForEvent("OnActivateEntered");
        var cancellationObserved = scenario.WaitForEvent("CancellationObserved");
        var disposeStarted = scenario.WaitForEvent("ActivatorDisposeStarted");
        var deactivated = scenario.WaitForEvent("Deactivated");
        using var collector = new DiagnosticEventCollector(DispatcherEvents.ListenerName);
        Task? stopTask = null;
        ActivationData? context = null;

        try
        {
            context = shutdownFixture.StartActivation(grainId);
            await entered.WaitAsync(Timeout, TestContext.Current.CancellationToken);
            var (message, request) = shutdownFixture.CreateRequest(
                context,
                scenario,
                payload: "shutdown-pending",
                requestContextValue: "shutdown-request",
                recordResponse: true,
                invokeMethodOptions: InvokeMethodOptions.None);
            var rejected = collector.WaitForEventAsync(
                nameof(DispatcherEvents.Rejected),
                diagnosticEvent => diagnosticEvent.Payload is DispatcherEvents.Rejected rejection
                    && ReferenceEquals(rejection.Message, message),
                Timeout,
                TestContext.Current.CancellationToken);

            context.ReceiveMessage(message);
            Assert.Equal(0, scenario.RequestInvocationCount);
            Assert.Null(request.Result);

            stopTask = shutdownFixture.HostedCluster.StopSiloAsync(primarySilo);
            await cancellationObserved.WaitAsync(Timeout, TestContext.Current.CancellationToken);
            await disposeStarted.WaitAsync(Timeout, TestContext.Current.CancellationToken);

            scenario.ReleaseDisposal();
            await deactivated.WaitAsync(Timeout, TestContext.Current.CancellationToken);
            await stopTask.WaitAsync(Timeout, TestContext.Current.CancellationToken);
            var rejectionEvent = await rejected;
            var rejection = Assert.IsType<DispatcherEvents.Rejected>(rejectionEvent.Payload);

            Assert.Same(message, rejection.Message);
            Assert.Equal(Message.RejectionTypes.Unrecoverable, rejection.RejectionType);
            var unavailable = Assert.IsType<SiloUnavailableException>(rejection.Exception);
            Assert.Equal(
                $"Silo '{primarySilo.SiloAddress}' is shutting down.",
                unavailable.Message);
            Assert.Null(rejection.Reason);
            Assert.Null(request.Result);
            Assert.Equal(0, scenario.GetEventCount("Activated"));
            Assert.Equal(0, scenario.RequestInvocationCount);
            Assert.Equal(1, scenario.GetEventCount("Deactivating"));
            Assert.Equal(1, scenario.GetEventCount("Deactivated"));
            Assert.Equal(1, scenario.DisposeStartedCount);
            Assert.Equal(1, scenario.DisposeCompletedCount);
            Assert.Equal(1, scenario.ScopeDisposeCount);
            Assert.Equal(ActivationState.Invalid, context.State);
            Assert.Null(activationDirectory.FindTarget(grainId));
            AssertIdentity(scenario, grainId);
        }
        finally
        {
            scenario.ReleaseActivation();
            scenario.ReleaseDisposal();
            if (stopTask is not null)
            {
                await stopTask.WaitAsync(Timeout, TestContext.Current.CancellationToken);
            }

            shutdownHooks.RemoveScenario(grainId);
            await shutdownFixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task ActivationStartup_DoesNotFlowExecutionContextOrRequestContext()
    {
        var (grainIdA, scenarioA) = fixture.CreateScenario(
            ActivationStartupCompletion.AsynchronousSuccess,
            ActivationStartupDisposal.Synchronous);
        var (grainIdB, scenarioB) = fixture.CreateScenario(
            ActivationStartupCompletion.AsynchronousSuccess,
            ActivationStartupDisposal.Synchronous);
        using var lifecycleSubscription = fixture.ObserveLifecycle();
        var enteredA = scenarioA.WaitForEvent("OnActivateEntered");
        var enteredB = scenarioB.WaitForEvent("OnActivateEntered");
        var responseA = scenarioA.WaitForEvent("Response");
        var responseB = scenarioB.WaitForEvent("Response");
        var originalAmbient = fixture.Hooks.AmbientValue;
        var originalRequestContext = RequestContext.Get(ActivationStartupTestHooks.RequestContextKey);
        ActivationData? contextA = null;
        ActivationData? contextB = null;

        try
        {
            fixture.Hooks.AmbientValue = "caller-ambient";
            RequestContext.Set(ActivationStartupTestHooks.RequestContextKey, "caller-request");

            contextA = fixture.StartActivation(grainIdA, "activate-a");
            contextB = fixture.StartActivation(grainIdB, "activate-b");
            await Task.WhenAll(enteredA, enteredB).WaitAsync(Timeout, TestContext.Current.CancellationToken);

            var (messageA, requestA) = fixture.CreateRequest(
                contextA,
                scenarioA,
                payload: "a",
                requestContextValue: "request-a",
                recordResponse: true);
            var (messageB, requestB) = fixture.CreateRequest(
                contextB,
                scenarioB,
                payload: "b",
                requestContextValue: "request-b",
                recordResponse: true);
            contextA.ReceiveMessage(messageA);
            scenarioA.Record("RequestAdmitted", contextA);
            contextB.ReceiveMessage(messageB);
            scenarioB.Record("RequestAdmitted", contextB);
            Assert.Equal(0, scenarioA.RequestInvocationCount);
            Assert.Equal(0, scenarioB.RequestInvocationCount);

            scenarioA.ReleaseActivation();
            scenarioB.ReleaseActivation();
            await Task.WhenAll(responseA, responseB).WaitAsync(Timeout, TestContext.Current.CancellationToken);

            Assert.Null(scenarioA.ConstructorAmbientValue);
            Assert.Null(scenarioB.ConstructorAmbientValue);
            Assert.Null(scenarioA.ConstructorRequestContextValue);
            Assert.Null(scenarioB.ConstructorRequestContextValue);
            Assert.Null(scenarioA.OnActivateAmbientValue);
            Assert.Null(scenarioB.OnActivateAmbientValue);
            Assert.Equal("activate-a", scenarioA.OnActivateRequestContextValue);
            Assert.Equal("activate-b", scenarioB.OnActivateRequestContextValue);
            Assert.Null(scenarioA.RequestAmbientValue);
            Assert.Null(scenarioB.RequestAmbientValue);
            Assert.Equal("request-a", scenarioA.RequestContextValue);
            Assert.Equal("request-b", scenarioB.RequestContextValue);
            Assert.Equal("a:request-a", requestA.Result);
            Assert.Equal("b:request-b", requestB.Result);
            Assert.Equal(1, scenarioA.ConstructorCount);
            Assert.Equal(1, scenarioB.ConstructorCount);
            Assert.Equal(1, scenarioA.OnActivateCount);
            Assert.Equal(1, scenarioB.OnActivateCount);
            Assert.Equal(1, scenarioA.RequestInvocationCount);
            Assert.Equal(1, scenarioB.RequestInvocationCount);
            Assert.Equal("caller-ambient", fixture.Hooks.AmbientValue);
            Assert.Equal("caller-request", RequestContext.Get(ActivationStartupTestHooks.RequestContextKey));
        }
        finally
        {
            fixture.Hooks.AmbientValue = originalAmbient;
            if (originalRequestContext is null)
            {
                RequestContext.Remove(ActivationStartupTestHooks.RequestContextKey);
            }
            else
            {
                RequestContext.Set(ActivationStartupTestHooks.RequestContextKey, originalRequestContext);
            }
            await CleanupAsync(contextA, scenarioA);
            await CleanupAsync(contextB, scenarioB);
            fixture.RemoveScenario(grainIdA);
            fixture.RemoveScenario(grainIdB);
        }
    }

    [Theory]
    [InlineData(ActivationStartupCompletion.ImmediateSuccess, ActivationStartupDisposal.Synchronous)]
    [InlineData(ActivationStartupCompletion.AsynchronousSuccess, ActivationStartupDisposal.Asynchronous)]
    [InlineData(ActivationStartupCompletion.ImmediateFailure, ActivationStartupDisposal.Synchronous)]
    [InlineData(ActivationStartupCompletion.AsynchronousFailure, ActivationStartupDisposal.Asynchronous)]
    [InlineData(ActivationStartupCompletion.Cancellation, ActivationStartupDisposal.Asynchronous)]
    public async Task ActivationStartup_CleanupOccursExactlyOnce(
        ActivationStartupCompletion completion,
        ActivationStartupDisposal disposal)
    {
        var (grainId, scenario) = fixture.CreateScenario(completion, disposal);
        using var lifecycleSubscription = fixture.ObserveLifecycle();
        var activated = scenario.WaitForEvent("Activated");
        var entered = scenario.WaitForEvent("OnActivateEntered");
        var failed = scenario.WaitForEvent("OnActivateFailed");
        var cancellationObserved = scenario.WaitForEvent("CancellationObserved");
        var disposeStarted = scenario.WaitForEvent("ActivatorDisposeStarted");
        var disposeCompleted = scenario.WaitForEvent("ActivatorDisposeCompleted");
        var scopeDisposed = scenario.WaitForEvent("ScopeDisposed");
        var deactivated = scenario.WaitForEvent("Deactivated");
        ActivationData? context = null;

        try
        {
            context = fixture.StartActivation(grainId);
            switch (completion)
            {
                case ActivationStartupCompletion.ImmediateSuccess:
                    await activated.WaitAsync(Timeout, TestContext.Current.CancellationToken);
                    context.Deactivate(new(DeactivationReasonCode.ApplicationRequested, "Matrix success complete."));
                    break;
                case ActivationStartupCompletion.AsynchronousSuccess:
                    await entered.WaitAsync(Timeout, TestContext.Current.CancellationToken);
                    scenario.ReleaseActivation();
                    await activated.WaitAsync(Timeout, TestContext.Current.CancellationToken);
                    context.Deactivate(new(DeactivationReasonCode.ApplicationRequested, "Matrix success complete."));
                    break;
                case ActivationStartupCompletion.ImmediateFailure:
                    await failed.WaitAsync(Timeout, TestContext.Current.CancellationToken);
                    break;
                case ActivationStartupCompletion.AsynchronousFailure:
                    await entered.WaitAsync(Timeout, TestContext.Current.CancellationToken);
                    scenario.ReleaseActivation();
                    await failed.WaitAsync(Timeout, TestContext.Current.CancellationToken);
                    break;
                case ActivationStartupCompletion.Cancellation:
                    await entered.WaitAsync(Timeout, TestContext.Current.CancellationToken);
                    context.Deactivate(new(DeactivationReasonCode.RuntimeRequested, "Matrix cancellation."));
                    await cancellationObserved.WaitAsync(Timeout, TestContext.Current.CancellationToken);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(completion));
            }

            await disposeStarted.WaitAsync(Timeout, TestContext.Current.CancellationToken);
            if (disposal is ActivationStartupDisposal.Asynchronous)
            {
                Assert.Equal(0, scenario.DisposeCompletedCount);
                Assert.Equal(0, scenario.ScopeDisposeCount);
                scenario.ReleaseDisposal();
            }

            await deactivated.WaitAsync(Timeout, TestContext.Current.CancellationToken);
            await Task.WhenAll(disposeCompleted, scopeDisposed).WaitAsync(Timeout, TestContext.Current.CancellationToken);

            Assert.Equal(1, scenario.CreateCount);
            Assert.Equal(1, scenario.ConstructorCount);
            Assert.Equal(1, scenario.OnActivateCount);
            Assert.Equal(1, scenario.DisposeStartedCount);
            Assert.Equal(1, scenario.DisposeCompletedCount);
            Assert.Equal(1, scenario.ScopeDisposeCount);
            Assert.Equal(
                completion is ActivationStartupCompletion.ImmediateSuccess
                    or ActivationStartupCompletion.AsynchronousSuccess ? 1 : 0,
                scenario.OnDeactivateCount);
            Assert.Equal(
                completion is ActivationStartupCompletion.ImmediateSuccess
                    or ActivationStartupCompletion.AsynchronousSuccess ? 1 : 0,
                scenario.GetEventCount("Activated"));
            Assert.Equal(1, scenario.GetEventCount("Deactivated"));
            Assert.Null(fixture.ActivationDirectory.FindTarget(grainId));

            var events = scenario.Events;
            Assert.True(IndexOf(events, "ActivatorDisposeStarted") < IndexOf(events, "ActivatorDisposeCompleted"));
            Assert.True(IndexOf(events, "ActivatorDisposeCompleted") < IndexOf(events, "ScopeDisposed"));
            Assert.True(IndexOf(events, "ScopeDisposed") < IndexOf(events, "Deactivated"));
            Assert.Equal("Deactivated", events[^1].Name);
            AssertIdentity(scenario, grainId);

            var eventSnapshot = scenario.Events;
            await context.DisposeAsync();
            await context.DisposeAsync();
            Assert.Equal(eventSnapshot, scenario.Events);
            Assert.Equal(1, scenario.DisposeCompletedCount);
            Assert.Equal(1, scenario.ScopeDisposeCount);
        }
        finally
        {
            await CleanupAsync(context, scenario);
            fixture.RemoveScenario(grainId);
        }
    }

    private static async Task CleanupAsync(ActivationData? context, ActivationStartupScenario scenario)
    {
        scenario.ReleaseActivation();
        scenario.ReleaseDisposal();
        if (context is null)
        {
            return;
        }

        var deactivated = scenario.WaitForEvent("Deactivated");
        context.Deactivate(new(DeactivationReasonCode.ApplicationRequested, "Test cleanup."));
        await deactivated.WaitAsync(Timeout, TestContext.Current.CancellationToken);
    }

    private static void AssertEventSubset(ActivationStartupScenario scenario, params string[] expected)
    {
        var expectedSet = expected.ToHashSet();
        Assert.Equal(expected, scenario.Events.Where(entry => expectedSet.Contains(entry.Name)).Select(static entry => entry.Name));
        Assert.All(expected, name => Assert.Equal(1, scenario.GetEventCount(name)));
    }

    private static void AssertIdentity(ActivationStartupScenario scenario, GrainId grainId)
    {
        var events = scenario.Events;
        Assert.NotEmpty(events);
        Assert.All(events, entry => Assert.Equal(grainId, entry.GrainId));
        Assert.Single(events.Select(static entry => entry.ActivationId).Distinct());
    }

    private static int IndexOf(IReadOnlyList<ActivationStartupEvent> events, string name)
    {
        for (var i = 0; i < events.Count; i++)
        {
            if (events[i].Name == name)
            {
                return i;
            }
        }

        return -1;
    }
}
