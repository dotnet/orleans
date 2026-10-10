using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Concurrency;
using Orleans.GrainDirectory;
using Orleans.Hosting;
using Orleans.Messaging;
using Orleans.Metadata;
using Orleans.Placement;
using Orleans.Placement.Repartitioning;
using Orleans.Runtime;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Messaging;
using Orleans.Runtime.Placement;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace UnitTests.ActivationsLifeCycleTests;

[TestSuite("BVT"), TestProvider("None"), TestArea("Runtime")]
[TestCategory("BVT"), TestCategory("ActivationShutdown")]
public sealed partial class SafeSiloRetirementTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;
    private const string OperationKey = "safe-retirement-operation";
    private const string OperationValue = "original-invocation";
    private const int Argument = 173;
    private const int Result = 174;

    [Fact]
    public Task QueuedRequest_SurvivesOriginalSiloDeparture_WithExactlyOneExecution()
        => VerifyRetirementAsync(arriveDuringUnregister: false);

    [Fact]
    public Task LateArrival_DuringRegistrationRetirement_SurvivesOriginalSiloDeparture()
        => VerifyRetirementAsync(arriveDuringUnregister: true);

    [Fact]
    public Task OutsideClient_ThroughGateway_SurvivesRetirement_WithOriginalIdentityAndOneExecution()
        => VerifyRetirementAsync(arriveDuringUnregister: false, callerKind: CallerKind.Outside);

    [Fact]
    public Task GrainCaller_SurvivesRetirement_WithOriginalIdentityAndOneExecution()
        => VerifyRetirementAsync(arriveDuringUnregister: false, callerKind: CallerKind.Grain);

    [Fact]
    public Task DelayedUnregisterAndCacheInvalidation_PreserveReplacementRegistration()
        => VerifyRetirementAsync(arriveDuringUnregister: false, verifyDelayedCleanup: true);

    [Fact]
    public async Task OutsideClient_LearnsHostingHintAndKeepsStickyGatewayAfterReceiverForwarding()
    {
        await using var fixture = await Fixture.CreateAsync(callerKind: CallerKind.Outside);
        var control = fixture.Control;
        var initial = Assert.Single(control.InitialRouteUpdates);
        Assert.Equal(0, initial.Generation);
        Assert.Equal(fixture.A.SiloAddress, initial.ForwardedTo);
        fixture.StartRetirement();
        var activation = await control.DeactivationEntered.Task.WaitAsync(Timeout, TestCancellation);
        var invocation = fixture.InvokeOutsideAsync("learned-route", TestCancellation);
        var first = await control.GatewayRequest("learned-route").Task.WaitAsync(Timeout, TestCancellation);
        Assert.Equal(fixture.A.SiloAddress, first.TargetSilo);
        Assert.Equal(fixture.ExpectedSendingGrain, first.SendingGrain);
        var received = await control.ReceiverRequest(fixture.A.SiloAddress, "learned-route").Task.WaitAsync(Timeout, TestCancellation);
        Assert.Equal(first.Id, received.Id);
        Assert.Equal(1, activation.WaitingCount);
        control.ReleaseDeactivation.TrySetResult();
        await control.UnregisterEntered.Task.WaitAsync(Timeout, TestCancellation);
        control.ReleaseUnregister.TrySetResult();
        await control.ReplacementActivationEntered.Task.WaitAsync(Timeout, TestCancellation);
        var forwarded = await control.RequestOnC.Task.WaitAsync(Timeout, TestCancellation);
        Assert.Equal(first.Id, forwarded.Id);
        Assert.Equal(1, forwarded.ForwardCount);
        await activation.Deactivated.WaitAsync(Timeout, TestCancellation);
        await fixture.CatalogRetirement.WaitAsync(Timeout, TestCancellation);
        await fixture.Retirement!.WaitAsync(Timeout, TestCancellation);
        fixture.BreakOutstandingToA();
        Assert.False(invocation.IsCompleted);
        var acceptedRoute = await control.OutsideRouteOnC.Task.WaitAsync(Timeout, TestCancellation);
        Assert.Equal(first.Id, acceptedRoute.Id);
        Assert.Equal(1, acceptedRoute.Generation);
        control.ReleaseReplacementActivation.TrySetResult();
        Assert.Equal(Result, await invocation.WaitAsync(Timeout, TestCancellation));
        var subsequent = fixture.InvokeOutsideAsync("cached-route", TestCancellation);
        var second = await control.GatewayRequest("cached-route").Task.WaitAsync(Timeout, TestCancellation);
        Assert.Equal(fixture.C.SiloAddress, second.TargetSilo);
        Assert.Equal(fixture.ExpectedSendingGrain, second.SendingGrain);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(Result, await subsequent.WaitAsync(Timeout, TestCancellation));
        Assert.Equal(2, control.Executions.Count);
        Assert.All(control.Executions, e => Assert.Equal(fixture.C.SiloAddress, e.Silo));
        Assert.Equal(0, fixture.RunningTargetRequests);
    }

    [Fact]
    public async Task OutsideClient_DelayedCorrelatedRoutesAndInvalidationsPreserveKnownReplacement()
    {
        await using var fixture = await Fixture.CreateAsync(
            callerKind: CallerKind.Outside, holdCancellationDelivery: true);
        var control = fixture.Control;
        var activation = fixture.OriginalActivation;
        activation.Deactivate(new(DeactivationReasonCode.ShuttingDown, "Hold delayed outside calls."), TestCancellation);
        await control.DeactivationEntered.Task.WaitAsync(Timeout, TestCancellation);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestCancellation);
        var delayed = fixture.InvokeOutsideAsync("delayed-route", cancellation.Token);
        var delayedWire = await control.GatewayRequest("delayed-route").Task.WaitAsync(Timeout, TestCancellation);
        var current = fixture.InvokeOutsideAsync("replacement-route", cancellation.Token);
        var currentWire = await control.GatewayRequest("replacement-route").Task.WaitAsync(Timeout, TestCancellation);
        Assert.Equal(fixture.A.SiloAddress, delayedWire.TargetSilo);
        Assert.Equal(fixture.A.SiloAddress, currentWire.TargetSilo);
        var client = fixture.OutsideCaller;
        // Use the real client's response pipeline and real correlated pending calls, not
        // reflection over its cache or a new production hook. The packets model delayed
        // network delivery while A's waiting requests remain below admission.
        client.ReceiveResponse(Route(currentWire, fixture.C.SiloAddress, generation: 1));
        client.ReceiveResponse(Route(delayedWire, fixture.B.SiloAddress, generation: 1));
        client.ReceiveResponse(new Message
        {
            Direction = Message.Directions.Response,
            Result = Message.ResponseTypes.Status,
            Id = currentWire.Id,
            SendingGrain = control.GrainId,
            TargetGrain = fixture.ExpectedSendingGrain,
            SendingSilo = fixture.A.SiloAddress,
            ForwardCount = 1,
            BodyObject = new StatusResponse(false, false, []) { ForwardedTo = fixture.B.SiloAddress },
            CacheInvalidationHeader = [new GrainAddressCacheUpdate(
                new GrainAddress { GrainId = control.GrainId, SiloAddress = fixture.A.SiloAddress },
                new GrainAddress { GrainId = control.GrainId, SiloAddress = fixture.B.SiloAddress })],
        });
        client.ReceiveResponse(new Message
        {
            Direction = Message.Directions.Response,
            Result = Message.ResponseTypes.Rejection,
            Id = delayedWire.Id,
            SendingGrain = control.GrainId,
            TargetGrain = fixture.ExpectedSendingGrain,
            SendingSilo = fixture.A.SiloAddress,
            BodyObject = new RejectionResponse
            {
                RejectionType = Message.RejectionTypes.Transient,
                RejectionInfo = "delayed rejection from original hosting silo",
            },
        });
        var rejected = await Assert.ThrowsAsync<OrleansMessageRejectionException>(() => delayed.WaitAsync(Timeout, TestCancellation));
        Assert.Contains("delayed rejection from original hosting silo", rejected.Message);
        // Neither an uncorrelated notice nor a notice for the completed delayed call
        // is allowed to overwrite the known replacement used by the next real request.
        client.ReceiveResponse(Route(delayedWire with { Id = default }, fixture.B.SiloAddress, generation: 255));
        client.ReceiveResponse(Route(delayedWire, fixture.B.SiloAddress, generation: 2));
        var probe = fixture.InvokeOutsideAsync("preserved-route", cancellation.Token);
        var probeWire = await control.GatewayRequest("preserved-route").Task.WaitAsync(Timeout, TestCancellation);
        Assert.Equal(fixture.C.SiloAddress, probeWire.TargetSilo);
        Assert.Equal(fixture.ExpectedSendingGrain, probeWire.SendingGrain);
        // The injected C hint does not change the actual host: the gateway's live
        // placement cache still knows A and delivers directly, without a C->A hop.
        var probeOnA = await control.ReceiverRequest(fixture.A.SiloAddress, "preserved-route").Task.WaitAsync(Timeout, TestCancellation);
        Assert.Equal(probeWire.Id, probeOnA.Id);
        Assert.Equal(0, probeOnA.ForwardCount);
        Assert.Empty(control.Executions);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => current.WaitAsync(Timeout, TestCancellation));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.WaitAsync(Timeout, TestCancellation));
        foreach (var request in new[] { delayedWire, currentWire, probeWire })
        {
            var operation = request.Operation![(OperationValue.Length + 1)..];
            await control.ReceiverRequest(fixture.A.SiloAddress, operation).Task.WaitAsync(Timeout, TestCancellation);
        }
        // Remote cancellation was deliberately held. Remove this fixture's abandoned
        // waiting work before cleanup can release the deactivation/registration barriers.
        var abandoned = activation.DequeueAllWaitingRequests();
        var originalRequests = abandoned.Where(message => message.RequestContextData?.ContainsKey(OperationKey) == true).ToArray();
        Assert.Equal(3, originalRequests.Length);
        Assert.Equal(new[] { delayedWire.Id, currentWire.Id, probeWire.Id }.OrderBy(id => id.ToString()),
            originalRequests.Select(message => message.Id).OrderBy(id => id.ToString()));
        foreach (var message in abandoned) message.Dispose();
        Assert.Equal(0, activation.WaitingCount);
        Assert.Equal(0, fixture.RunningTargetRequests);
        Assert.Empty(control.Executions);

        Message Route(ReceivedRequest request, SiloAddress destination, int generation) => new()
        {
            Direction = Message.Directions.Response,
            Result = Message.ResponseTypes.Status,
            Id = request.Id,
            SendingGrain = control.GrainId,
            TargetGrain = request.SendingGrain,
            SendingSilo = fixture.A.SiloAddress,
            ForwardCount = generation,
            BodyObject = new StatusResponse(false, false, []) { ForwardedTo = destination },
        };
    }

    private static async Task VerifyRetirementAsync(
        bool arriveDuringUnregister,
        CallerKind callerKind = CallerKind.Hosted,
        bool verifyDelayedCleanup = false)
    {
        await using var fixture = await Fixture.CreateAsync(callerKind: callerKind);
        var control = fixture.Control;
        fixture.StartRetirement();
        var retiring = await control.DeactivationEntered.Task.WaitAsync(Timeout, TestCancellation);
        Assert.Equal(DeactivationReasonCode.ShuttingDown, control.DeactivationReason);

        if (arriveDuringUnregister)
        {
            control.ReleaseDeactivation.TrySetResult();
            await control.UnregisterEntered.Task.WaitAsync(Timeout, TestCancellation);
        }

        // B has cached A. The hint only takes effect after that stale address is invalidated.
        // It is deliberately part of the ORIGINAL invocation, not a replacement application call.
        var originalTask = fixture.InvokeAsync(TestCancellation);
        var original = await control.RequestOnA.Task.WaitAsync(Timeout, TestCancellation);
        var targetInvocation = callerKind == CallerKind.Grain
            ? await control.GrainCallerInvocationEntered.Task.WaitAsync(Timeout, TestCancellation)
            : originalTask;
        Assert.NotEqual(default, original.Id);
        Assert.Equal(fixture.A.SiloAddress, original.TargetSilo);
        Assert.Equal(fixture.B.SiloAddress, original.SendingSilo);
        Assert.Equal(fixture.ExpectedSendingGrain, original.SendingGrain);
        Assert.Equal(((GrainReference)fixture.Grain).GrainId, original.TargetGrain);
        Assert.Equal(OperationValue, original.Operation);
        Assert.Equal(fixture.C.SiloAddress, original.PlacementHint);
        Assert.Equal(0, original.ForwardCount);
        Assert.Equal(1, retiring.WaitingCount);
        Assert.Empty(control.Executions);
        Assert.Empty(control.Admissions);
        Assert.False(originalTask.IsCompleted);

        control.ReleaseDeactivation.TrySetResult();
        var unregistered = await control.UnregisterEntered.Task.WaitAsync(Timeout, TestCancellation);
        Assert.Equal(retiring.Address, unregistered);
        Assert.Equal(retiring.Address, control.Directory.Registration);
        Assert.False(retiring.Deactivated.IsCompleted);
        Assert.False(fixture.CatalogRetirement.IsCompleted);
        Assert.False(control.ReplacementActivationEntered.Task.IsCompleted);
        Assert.Empty(control.Executions);

        // Registration retirement, not application hook completion, authorizes disposition.
        control.ReleaseUnregister.TrySetResult();
        ActivationData replacement;
        try
        {
            replacement = await control.ReplacementActivationEntered.Task.WaitAsync(Timeout, TestCancellation);
        }
        catch (TimeoutException exception)
        {
            var inCache = fixture.B.ServiceProvider.GetRequiredService<GrainLocator>().TryLookupInCache(control.GrainId, out var cached);
            throw new TimeoutException(
                $"C was not activated. Caller={callerKind}; original task={originalTask.Status}; B cache={(inCache ? cached : null)}; "
                + $"A={fixture.A.SiloAddress}; B={fixture.B.SiloAddress}; C={fixture.C.SiloAddress}; "
                + $"directory={control.Directory.Registration}; routes=[{string.Join(", ", control.RouteUpdates)}]; "
                + $"execution silos=[{string.Join(", ", control.Executions.Select(execution => execution.Silo))}].",
                exception);
        }

        var forwarded = await control.RequestOnC.Task.WaitAsync(Timeout, TestCancellation);
        Assert.Equal(fixture.C.SiloAddress, replacement.Address.SiloAddress);
        Assert.Equal(original.Id, forwarded.Id);
        Assert.Equal(original.SendingGrain, forwarded.SendingGrain);
        Assert.Equal(original.SendingSilo, forwarded.SendingSilo);
        Assert.Equal(original.TargetGrain, forwarded.TargetGrain);
        Assert.Equal(OperationValue, forwarded.Operation);
        Assert.Equal(fixture.C.SiloAddress, forwarded.PlacementHint);
        Assert.Equal(fixture.C.SiloAddress, forwarded.TargetSilo);
        Assert.Equal(1, forwarded.ForwardCount);
        Assert.NotNull(original.Remaining);
        Assert.NotNull(forwarded.Remaining);
        Assert.True(forwarded.Remaining > TimeSpan.Zero);
        Assert.True(forwarded.Remaining <= original.Remaining);
        Assert.Equal(replacement.Address, control.Directory.Registration);
        Assert.Empty(control.Executions);
        Assert.Empty(control.Admissions);

        if (callerKind == CallerKind.Outside)
        {
            var notice = await control.RouteUpdateObserved.Task.WaitAsync(Timeout, TestCancellation);
            Assert.Equal(original.Id, notice.Id);
            Assert.Equal(fixture.C.SiloAddress, notice.ForwardedTo);
            Assert.Equal(1, notice.Generation);
        }

        if (verifyDelayedCleanup)
        {
            var locator = fixture.B.ServiceProvider.GetRequiredService<GrainLocator>();
            // Retire the known A hint before learning C. Locator results can carry
            // a silo-only placement hint or the directory's full activation address.
            locator.InvalidateCache(retiring.Address);
            Assert.True(replacement.Address.Matches(await locator.Lookup(control.GrainId)));
            Assert.True(locator.TryLookupInCache(control.GrainId, out var cachedBefore));
            Assert.Equal(fixture.C.SiloAddress, cachedBefore!.SiloAddress);
            await locator.Unregister(retiring.Address, UnregistrationCause.Force).WaitAsync(Timeout, TestCancellation);
            Assert.Equal(replacement.Address, control.Directory.Registration);
            Assert.True(locator.TryLookupInCache(control.GrainId, out var cachedAfter));
            Assert.Equal(cachedBefore, cachedAfter);
            Assert.Equal(fixture.C.SiloAddress, cachedAfter!.SiloAddress);
        }

        // Wait until A really stops, but keep C below invocation admission.
        await fixture.CatalogRetirement.WaitAsync(Timeout, TestCancellation);
        await fixture.Retirement!.WaitAsync(Timeout, TestCancellation);
        fixture.BreakOutstandingToA();
        Assert.False(originalTask.IsCompleted);
        Assert.False(targetInvocation.IsCompleted);
        Assert.Equal(1, fixture.RunningTargetRequests);

        control.ReleaseReplacementActivation.TrySetResult();
        Assert.Equal(Result, await targetInvocation.WaitAsync(Timeout, TestCancellation));
        Assert.Equal(Result, await originalTask.WaitAsync(Timeout, TestCancellation));
        Assert.Equal(0, fixture.RunningTargetRequests);
        var execution = Assert.Single(control.Executions);
        Assert.Equal(fixture.C.SiloAddress, execution.Silo);
        Assert.Equal(Argument, execution.Argument);
        Assert.Equal(OperationValue, execution.Operation);
        Assert.Equal(fixture.C.SiloAddress, Assert.Single(control.Admissions));
        Assert.Equal(0, retiring.WaitingCount);

        var disposition = Assert.Single(control.RouteUpdates);
        Assert.Equal(original.Id, disposition.Id);
        Assert.Equal(1, disposition.Generation);
        Assert.Equal(fixture.C.SiloAddress, disposition.ForwardedTo);
        Assert.Equal(fixture.A.SiloAddress, disposition.SendingSilo);

        Assert.True(retiring.Deactivated.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanceledOrForcedShutdownDeactivation_WithQueuedRequest_ProducesNoPositiveProof(bool forceDisposal)
    {
        await using var fixture = await Fixture.CreateAsync();
        var control = fixture.Control;
        using var retirementCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestCancellation);
        var activation = Assert.IsType<ActivationData>(
            fixture.A.ServiceProvider.GetRequiredService<ActivationDirectory>().FindTarget(control.GrainId));
        activation.Deactivate(
            new(DeactivationReasonCode.ShuttingDown, "Canceled shutdown deactivation boundary."),
            retirementCancellation.Token);
        var retiring = await control.DeactivationEntered.Task.WaitAsync(Timeout, TestCancellation);
        var originalTask = fixture.InvokeAsync(TestCancellation);
        var original = await control.RequestOnA.Task.WaitAsync(Timeout, TestCancellation);
        Assert.Equal(activation, retiring);
        Assert.Equal(1, retiring.WaitingCount);
        Assert.False(retiring.Deactivated.IsCompleted);
        Assert.False(fixture.CatalogRetirement.IsCompleted);
        Assert.Empty(control.Executions);
        Assert.Empty(control.Admissions);

        if (forceDisposal)
        {
            // Invoke the actual forced activation cleanup boundary, not a reflection-set flag.
            // The held hook's command is canceled by disposal before registration retirement.
            await retiring.DisposeAsync().AsTask().WaitAsync(Timeout, TestCancellation);
        }
        else
        {
            retirementCancellation.Cancel();
        }

        await fixture.AssertCatalogRetirementFailedAsync();
        var exception = await Assert.ThrowsAsync<OrleansMessageRejectionException>(
            () => originalTask.WaitAsync(Timeout, TestCancellation));
        Assert.Contains("Canceled shutdown deactivation boundary.", exception.Message);
        Assert.True(retiring.Deactivated.IsCompletedSuccessfully);
        Assert.Equal(retiring.Address, control.Directory.Registration);
        Assert.False(control.UnregisterEntered.Task.IsCompleted);
        Assert.Empty(control.RouteUpdates);
        Assert.False(control.ReplacementActivationEntered.Task.IsCompleted);
        Assert.False(control.RequestOnC.Task.IsCompleted);
        Assert.Empty(control.Executions);
        Assert.Empty(control.Admissions);
        Assert.Equal(0, retiring.WaitingCount);
        Assert.Equal(0, fixture.RunningTargetRequests);
        Assert.NotEqual(default, original.Id);
    }

    [Fact]
    public async Task Cancellation_WhileRegistrationRetirementIsHeld_DoesNotReplayButReceiverMayForward()
    {
        // Deliberately hold remote cancellation: local callback completion cannot revoke
        // work already owned by A. A may forward it, but B must not replay the payload.
        await using var fixture = await Fixture.CreateAsync(holdCancellationDelivery: true);
        var control = fixture.Control;
        fixture.StartRetirement();
        var retiring = await control.DeactivationEntered.Task.WaitAsync(Timeout, TestCancellation);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestCancellation);
        var originalTask = fixture.InvokeAsync(cancellation.Token);
        var original = await control.RequestOnA.Task.WaitAsync(Timeout, TestCancellation);
        control.ReleaseDeactivation.TrySetResult();
        await control.UnregisterEntered.Task.WaitAsync(Timeout, TestCancellation);
        Assert.Equal(1, retiring.WaitingCount);
        Assert.Empty(control.Executions);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => originalTask.WaitAsync(Timeout, TestCancellation));
        Assert.Equal(0, fixture.Caller.GetRunningRequestsCount(fixture.InterfaceType));
        control.CancellationManager!.Received(1).SignalCancellation(
            fixture.A.SiloAddress, original.TargetGrain, original.SendingGrain, original.Id);

        control.ReleaseUnregister.TrySetResult();
        var disposition = await control.RouteUpdateObserved.Task.WaitAsync(Timeout, TestCancellation);
        var replacement = await control.ReplacementActivationEntered.Task.WaitAsync(Timeout, TestCancellation);
        var forwarded = await control.RequestOnC.Task.WaitAsync(Timeout, TestCancellation);
        Assert.Equal(original.Id, forwarded.Id);
        Assert.Equal(1, forwarded.ForwardCount);
        // Cooperatively cancel at the new receiver before admitting this held invocation.
        await ((IGrainCallCancellationExtension)replacement).CancelRequestAsync(
            original.SendingGrain, original.Id, TestCancellation).AsTask().WaitAsync(Timeout, TestCancellation);
        await fixture.CatalogRetirement.WaitAsync(Timeout, TestCancellation);
        await fixture.Retirement!.WaitAsync(Timeout, TestCancellation);
        Assert.Equal(original.Id, disposition.Id);
        Assert.Equal(fixture.C.SiloAddress, disposition.ForwardedTo);
        Assert.Equal(1, disposition.Generation);
        Assert.Empty(control.Executions);
        Assert.Empty(control.Admissions);
        Assert.Equal(replacement.Address, control.Directory.Registration);
        Assert.Equal(0, replacement.WaitingCount);
        Assert.Equal(0, retiring.WaitingCount);
        fixture.Caller.BreakOutstandingMessagesToSilo(fixture.A.SiloAddress);
        Assert.Equal(0, fixture.Caller.GetRunningRequestsCount(fixture.InterfaceType));
        Assert.True(retiring.Deactivated.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task FailedUnregister_ProducesFailedDrainAndTerminalRejection_NotForwarding()
    {
        await using var fixture = await Fixture.CreateAsync();
        var control = fixture.Control;
        control.FailUnregister = true;
        fixture.StartRetirement();
        var retiring = await control.DeactivationEntered.Task.WaitAsync(Timeout, TestCancellation);
        var originalTask = fixture.InvokeAsync(TestCancellation);
        await control.RequestOnA.Task.WaitAsync(Timeout, TestCancellation);
        control.ReleaseDeactivation.TrySetResult();
        var registration = await control.UnregisterEntered.Task.WaitAsync(Timeout, TestCancellation);
        Assert.Equal(1, retiring.WaitingCount);
        Assert.False(retiring.Deactivated.IsCompleted);
        Assert.False(fixture.CatalogRetirement.IsCompleted);

        control.ReleaseUnregister.TrySetResult();
        await fixture.AssertCatalogRetirementFailedAsync();
        var exception = await Assert.ThrowsAsync<OrleansMessageRejectionException>(() => originalTask.WaitAsync(Timeout, TestCancellation));
        Assert.Contains("This process is terminating.", exception.Message);
        await fixture.Retirement!.WaitAsync(Timeout, TestCancellation);
        Assert.Equal(registration, control.Directory.Registration);
        Assert.Empty(control.RouteUpdates);
        Assert.Empty(control.Executions);
        Assert.Empty(control.Admissions);
        Assert.False(control.ReplacementActivationEntered.Task.IsCompleted);
        Assert.False(control.RequestOnC.Task.IsCompleted);
        Assert.Equal(0, retiring.WaitingCount);
        Assert.Equal(0, fixture.Caller.GetRunningRequestsCount(fixture.InterfaceType));
        Assert.True(retiring.Deactivated.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateArrival_AfterCatalogRemoval_UsesInvalidActivationForwardingAndExecutesOnce(bool deliverThroughCapturedActivation)
    {
        await using var fixture = await Fixture.CreateAsync();
        var control = fixture.Control;
        control.HoldDisposal = true;
        fixture.StartRetirement();
        var retiring = await control.DeactivationEntered.Task.WaitAsync(Timeout, TestCancellation);
        Task<int>? originalTask = null;
        if (deliverThroughCapturedActivation)
        {
            // Model decoded ingress which already captured this activation before
            // its catalog removal, then resumes dispatch during held disposal.
            originalTask = fixture.InvokeAsync(TestCancellation);
            await control.RequestOnA.Task.WaitAsync(Timeout, TestCancellation);
            Assert.Equal(1, retiring.WaitingCount);
        }
        control.ReleaseDeactivation.TrySetResult();
        await control.UnregisterEntered.Task.WaitAsync(Timeout, TestCancellation);
        control.ReleaseUnregister.TrySetResult();
        await control.DisposalEntered.Task.WaitAsync(Timeout, TestCancellation);

        // A still has live connections but no activation. A normal invalid-activation
        // forwarding turn handles this arrival, without a retired-activation dictionary.
        Assert.Null(fixture.A.ServiceProvider.GetRequiredService<ActivationDirectory>().FindTarget(control.GrainId));
        Assert.Null(control.Directory.Registration);
        Assert.False(retiring.Deactivated.IsCompleted);
        Assert.False(fixture.CatalogRetirement.IsCompleted);
        Assert.Empty(control.Executions);

        if (deliverThroughCapturedActivation)
        {
            var capturedRequest = Assert.Single(retiring.DequeueAllWaitingRequests());
            Assert.Equal(ActivationState.Invalid, retiring.State);
            retiring.ReceiveMessage(capturedRequest);
            Assert.Equal(0, retiring.WaitingCount);
        }
        else
        {
            // B still caches A: no earlier Execute has invalidated that cache.
            originalTask = fixture.InvokeAsync(TestCancellation);
        }
        Assert.NotNull(originalTask);
        var disposition = await control.RouteUpdateObserved.Task.WaitAsync(Timeout, TestCancellation);
        var replacement = await control.ReplacementActivationEntered.Task.WaitAsync(Timeout, TestCancellation);
        var request = await control.RequestOnC.Task.WaitAsync(Timeout, TestCancellation);
        Assert.NotEqual(default, disposition.Id);
        Assert.Equal(disposition.Id, request.Id);
        Assert.Equal(fixture.C.SiloAddress, disposition.ForwardedTo);
        Assert.Equal(1, disposition.Generation);
        Assert.Equal(1, request.ForwardCount);
        Assert.Equal(OperationValue, request.Operation);
        Assert.Equal(fixture.C.SiloAddress, replacement.Address.SiloAddress);
        Assert.Equal(replacement.Address, control.Directory.Registration);
        var lateOnA = await control.IncomingRequestOnA.Task.WaitAsync(Timeout, TestCancellation);
        Assert.Equal(request.Id, lateOnA.Id);
        Assert.Equal(0, lateOnA.ForwardCount);
        Assert.Equal(fixture.A.SiloAddress, lateOnA.TargetSilo);
        Assert.Empty(control.Executions);
        Assert.Empty(control.Admissions);

        control.ReleaseDisposal.TrySetResult();
        await fixture.CatalogRetirement.WaitAsync(Timeout, TestCancellation);
        await fixture.Retirement!.WaitAsync(Timeout, TestCancellation);
        fixture.Caller.BreakOutstandingMessagesToSilo(fixture.A.SiloAddress);
        Assert.False(originalTask.IsCompleted);
        Assert.Equal(1, fixture.Caller.GetRunningRequestsCount(fixture.InterfaceType));

        control.ReleaseReplacementActivation.TrySetResult();
        Assert.Equal(Result, await originalTask.WaitAsync(Timeout, TestCancellation));
        var execution = Assert.Single(control.Executions);
        Assert.Equal(fixture.C.SiloAddress, execution.Silo);
        Assert.Equal(Argument, execution.Argument);
        Assert.Equal(OperationValue, execution.Operation);
        Assert.Equal(fixture.C.SiloAddress, Assert.Single(control.Admissions));
        Assert.Single(control.RouteUpdates);
        Assert.Equal(0, fixture.Caller.GetRunningRequestsCount(fixture.InterfaceType));
        Assert.True(retiring.Deactivated.IsCompletedSuccessfully);
    }

    private enum CallerKind
    {
        Hosted,
        Grain,
        Outside,
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly InProcessTestCluster _cluster;
        private readonly CallerKind _callerKind;
        private readonly OutsideRuntimeClient? _outsideCaller;
        private readonly ISafeSiloRetirementCallerGrain? _grainCaller;
        private readonly Catalog _catalog;
        private Task? _catalogRetirement;

        private Fixture(InProcessTestCluster cluster, Control control, CallerKind callerKind)
        {
            _cluster = cluster;
            _callerKind = callerKind;
            Control = control;
            A = cluster.Silos[0];
            _catalog = A.ServiceProvider.GetRequiredService<Catalog>();
            B = cluster.Silos[1];
            C = cluster.Silos[2];
            control.A = A.SiloAddress;
            control.B = B.SiloAddress;
            control.C = C.SiloAddress;
            Caller = B.ServiceProvider.GetRequiredService<InsideRuntimeClient>();
            var factory = callerKind == CallerKind.Outside
                ? cluster.Client
                : B.ServiceProvider.GetRequiredService<IGrainFactory>();
            Grain = control.FailureMatrix?.Stateless == true
                ? factory.GetGrain<ISafeSiloRetirementGrain>(Guid.NewGuid(), typeof(SafeSiloRetirementStatelessGrain).FullName)
                : control.FailureMatrix?.Reentrant == true
                ? factory.GetGrain<ISafeSiloRetirementGrain>(Guid.NewGuid(), typeof(SafeSiloRetirementReentrantGrain).FullName)
                : factory.GetGrain<ISafeSiloRetirementGrain>(Guid.NewGuid());
            if (callerKind == CallerKind.Outside)
            {
                _outsideCaller = cluster.Client.ServiceProvider.GetRequiredService<OutsideRuntimeClient>();
                ExpectedSendingGrain = _outsideCaller.CurrentActivationAddress.GrainId;
                var gateways = cluster.Client.ServiceProvider.GetRequiredService<GatewayManager>();
                var gatewayPort = B.ServiceProvider.GetRequiredService<IOptions<EndpointOptions>>().Value.GatewayPort;
                foreach (var candidate in gateways.GetLiveGateways().ToArray())
                {
                    if (candidate.Endpoint.Port != gatewayPort)
                    {
                        gateways.MarkAsDead(candidate);
                    }
                }

                var gateway = Assert.Single(gateways.GetLiveGateways());
                // GatewayManager uses generation-zero transport endpoints, not silo identities.
                Assert.Equal(0, gateway.Generation);
                Assert.Equal(B.SiloAddress.Endpoint.Address, gateway.Endpoint.Address);
                Assert.Equal(B.ServiceProvider.GetRequiredService<IOptions<EndpointOptions>>().Value.GatewayPort, gateway.Endpoint.Port);
            }
            else if (callerKind == CallerKind.Grain)
            {
                _grainCaller = factory.GetGrain<ISafeSiloRetirementCallerGrain>(Guid.NewGuid());
                ExpectedSendingGrain = ((GrainReference)_grainCaller).GrainId;
            }
            else
            {
                ExpectedSendingGrain = B.ServiceProvider.GetRequiredService<HostedClient>().Address.GrainId;
            }

            control.GrainId = ((GrainReference)Grain).GrainId;
        }

        public Control Control { get; }
        public InProcessSiloHandle A { get; }
        public InProcessSiloHandle B { get; }
        public InProcessSiloHandle C { get; }
        public InsideRuntimeClient Caller { get; }
        public ISafeSiloRetirementGrain Grain { get; }
        public GrainId ExpectedSendingGrain { get; }
        public GrainInterfaceType InterfaceType => ((GrainReference)Grain).InterfaceType;
        public int RunningTargetRequests => _outsideCaller?.GetRunningRequestsCount(InterfaceType) ?? Caller.GetRunningRequestsCount(InterfaceType);
        public OutsideRuntimeClient OutsideCaller => _outsideCaller!;
        public Task? Retirement { get; private set; }
        public Task CatalogRetirement => _catalogRetirement ??= _catalog.DeactivateAllActivations(TestCancellation);

        public async Task AssertCatalogRetirementFailedAsync()
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => CatalogRetirement.WaitAsync(Timeout, TestCancellation));
            Assert.Contains("retirement drains were unsuccessful", error.Message);
        }

        public static async Task<Fixture> CreateAsync(
            bool holdCancellationDelivery = false,
            CallerKind callerKind = CallerKind.Hosted,
            FailureMatrixOptions? failureMatrix = null)
        {
            var control = new Control
            {
                CancellationManager = holdCancellationDelivery ? Substitute.For<IGrainCallCancellationManager>() : null,
                FailureMatrix = failureMatrix,
                AwaitOutsideInitialRoute = callerKind == CallerKind.Outside,
            };
            var builder = new InProcessTestClusterBuilder(3);
            builder.Options.ConfigureFileLogging = false;
            if (callerKind == CallerKind.Outside)
            {
                // Freeze ONLY gateway-list refresh, not the outside callback's deadline clock.
                // Select B after the harness's startup system-target probes have completed.
                builder.ConfigureClient(client =>
                {
                    if (control.CancellationManager is { } cancellationManager)
                        client.Services.AddSingleton(cancellationManager);
                    client.Services.AddSingleton(provider => new GatewayManager(
                        provider.GetRequiredService<IOptions<GatewayOptions>>(),
                        provider.GetRequiredService<IGatewayListProvider>(),
                        provider.GetRequiredService<ILoggerFactory>(),
                        provider.GetRequiredService<ConnectionManager>(),
                        new FakeTimeProvider()));
                });
            }

            builder.ConfigureHost(host =>
            {
                TestDefaultConfiguration.ConfigureHostConfiguration(host.Configuration);
                if (callerKind == CallerKind.Outside)
                {
                    host.Logging.AddProvider(new XunitLoggerProvider(TestContext.Current.TestOutputHelper!));
                    host.Logging.SetMinimumLevel(LogLevel.Warning);
                }
            });
            builder.ConfigureSilo((specific, silo) =>
            {
                silo.Services.AddSingleton(control);
                silo.Services.AddSingleton<IGrainDirectoryResolver, ControlledDirectoryResolver>();
                silo.Services.AddSingleton<IMessageStatisticsSink, MessageObserver>();
                silo.Services.AddSingleton<IIncomingGrainCallFilter, AdmissionObserver>();
                if (control.CancellationManager is { } cancellationManager)
                {
                    silo.Services.AddSingleton(cancellationManager);
                }

                if (failureMatrix is { } matrix)
                {
                    if (matrix.HoldDestinationPlacement)
                    {
                        silo.Services.AddPlacementDirector<ResourceOptimizedPlacement>(provider =>
                            new FailureMatrixDestinationDirector(control, ActivatorUtilities.CreateInstance<ResourceOptimizedPlacementDirector>(provider)));
                    }

                    if (matrix.Stateless)
                    {
                        silo.Services.AddPlacementDirector<StatelessWorkerPlacement, FailureMatrixStatelessDirector>();
                        silo.Services.AddSingleton<IGrainPropertiesProvider, FailureMatrixStatelessProperties>();
                    }

                    if (specific.SiloName == "Silo_1")
                    {
                        silo.Services.AddKeyedSingleton<TimeProvider>(TimeProviderNames.Messaging, matrix.CallerClock);
                    }
                    else if (specific.SiloName == "Silo_0")
                    {
                        silo.Services.AddKeyedSingleton<TimeProvider>(TimeProviderNames.Grains, matrix.ActivationClock);
                    }
                }
            });
            var cluster = builder.Build();
            try
            {
                await cluster.DeployAsync(TestCancellation).WaitAsync(Timeout, TestCancellation);
                var fixture = new Fixture(cluster, control, callerKind);
                if (callerKind == CallerKind.Outside)
                {
                    var center = cluster.Client.ServiceProvider.GetRequiredService<ClientMessageCenter>();
                    var handler = (Action<Message>)typeof(ClientMessageCenter)
                        .GetField("messageHandler", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(center)!;
                    var callbacks = (ConcurrentDictionary<CorrelationId, CallbackData>)typeof(OutsideRuntimeClient)
                        .GetField("callbacks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.OutsideCaller)!;
                    center.RegisterLocalMessageHandler(message =>
                    {
                        var status = message.Result == Message.ResponseTypes.Status ? message.BodyObject as StatusResponse : null;
                        callbacks.TryGetValue(message.Id, out var callback);
                        handler(message);
                        if (status?.ForwardedTo is { } destination && callback is { IsCompleted: false }
                            && callback.Message.TargetGrain == control.GrainId
                            && destination.Equals(callback.Message.TargetSilo) && callback.Message.ForwardCount == message.ForwardCount)
                        {
                            var update = new RouteUpdate(message.Id, message.ForwardCount, destination, message.SendingSilo);
                            if (destination.Equals(control.A)) control.OutsideRouteOnA.TrySetResult(update);
                            if (destination.Equals(control.C)) control.OutsideRouteOnC.TrySetResult(update);
                        }
                    });
                }

                foreach (var silo in cluster.Silos)
                {
                    // Capture typed route bodies before response dispatch/write clears them.
                    var center = silo.ServiceProvider.GetRequiredService<MessageCenter>();
                    Action<Message> observer = message =>
                        {
                            MessageObserver.ObserveRoute(control, silo.SiloAddress, message);
                            if (silo.SiloAddress.Equals(control.A)
                                && message.Direction == Message.Directions.Request && message.TargetGrain == control.GrainId
                                && Equals(message.RequestContextData?.GetValueOrDefault(OperationKey), OperationValue))
                                control.IncomingRequestOnA.TrySetResult(MessageObserver.Snapshot(message, OperationValue));
                            if (silo.SiloAddress.Equals(control.B)
                                && message.Direction == Message.Directions.Request
                                && message.SendingGrain.IsClient() && message.TargetGrain == control.GrainId
                                && message.RequestContextData?.GetValueOrDefault(OperationKey) is string operation)
                            {
                                control.GatewayRequests.GetOrAdd(operation, _ => new(TaskCreationOptions.RunContinuationsAsynchronously))
                                    .TrySetResult(MessageObserver.Snapshot(message, operation));
                            }
                        };
                    // The runtime installs its own sniff handler. Preserve it; the public
                    // setter intentionally cannot replace an installed delegate.
                    typeof(MessageCenter).GetField("sniffIncomingMessageHandler", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .SetValue(center, Delegate.Combine(center.SniffIncomingMessage, observer));
                }
                if (fixture._grainCaller is { } grainCaller)
                {
                    RequestContext.Set(IPlacementDirector.PlacementHintKey, fixture.B.SiloAddress);
                    try
                    {
                        Assert.Equal(fixture.B.SiloAddress, await grainCaller.GetSiloAddress().WaitAsync(Timeout, TestCancellation));
                    }
                    finally
                    {
                        RequestContext.Remove(IPlacementDirector.PlacementHintKey);
                    }
                }

                RequestContext.Set(IPlacementDirector.PlacementHintKey, fixture.A.SiloAddress);
                try
                {
                    Assert.Equal(fixture.A.SiloAddress, await fixture.Grain.GetSiloAddress().WaitAsync(Timeout, TestCancellation));
                }
                finally
                {
                    RequestContext.Remove(IPlacementDirector.PlacementHintKey);
                }

                if (failureMatrix?.Stateless == true)
                {
                    var worker = await control.MatrixOriginalActivationEntered.Task.WaitAsync(Timeout, TestCancellation);
                    Assert.Equal(fixture.A.SiloAddress, worker.Address.SiloAddress);
                    Assert.False(worker.IsUsingGrainDirectory);
                    Assert.Null(control.Directory.Registration);
                }
                else
                {
                    Assert.Equal(fixture.A.SiloAddress, control.Directory.Registration?.SiloAddress);
                }
                return fixture;
            }
            catch
            {
                control.ReleaseAll();
                await cluster.DisposeAsync();
                throw;
            }
        }

        public void StartRetirement() => Retirement = _cluster.StopSiloAsync(A, TestCancellation);
        public void StartRetirementWithBudget(CancellationToken stopToken) => Retirement = _cluster.StopSiloAsync(A, stopToken);

        public Task StopSiloAsync(InProcessSiloHandle silo, CancellationToken stopToken) => _cluster.StopSiloAsync(silo, stopToken);
        public Task CrashSiloAsync(InProcessSiloHandle silo) => _cluster.KillSiloAsync(silo, TestCancellation);

        public ActivationData OriginalActivation => Assert.IsType<ActivationData>(
            A.ServiceProvider.GetRequiredService<ActivationDirectory>().FindTarget(Control.GrainId));

        public Task<int> InvokeMatrixAsync(int argument, string operation, CancellationToken cancellationToken)
        {
            RequestContext.Set(IPlacementDirector.PlacementHintKey, C.SiloAddress);
            RequestContext.Set(OperationKey, MatrixOperation(operation));
            try
            {
                return Grain.Execute(argument, cancellationToken);
            }
            finally
            {
                RequestContext.Remove(IPlacementDirector.PlacementHintKey);
                RequestContext.Remove(OperationKey);
            }
        }

        public Task InvokeMatrixOneWayAsync(int argument, string operation)
        {
            RequestContext.Set(IPlacementDirector.PlacementHintKey, C.SiloAddress);
            RequestContext.Set(OperationKey, MatrixOperation(operation));
            try
            {
                return Grain.Submit(argument);
            }
            finally
            {
                RequestContext.Remove(IPlacementDirector.PlacementHintKey);
                RequestContext.Remove(OperationKey);
            }
        }

        public void ExpireOriginalDeadline() => Control.FailureMatrix!.CallerClock.Advance(
            Caller.GetResponseTimeout() + TimeSpan.FromSeconds(2));

        public void BreakOutstandingToA()
        {
            if (_callerKind == CallerKind.Outside)
            {
                _outsideCaller!.BreakOutstandingMessagesToSilo(A.SiloAddress);
            }
            else
            {
                Caller.BreakOutstandingMessagesToSilo(A.SiloAddress);
            }
        }

        public Task<int> InvokeAsync(CancellationToken cancellationToken = default)
        {
            RequestContext.Set(IPlacementDirector.PlacementHintKey, C.SiloAddress);
            RequestContext.Set(OperationKey, OperationValue);
            try
            {
                return _grainCaller is { } grainCaller
                    ? grainCaller.Call(Grain, Argument, cancellationToken)
                    : Grain.Execute(Argument, cancellationToken);
            }
            finally
            {
                RequestContext.Remove(IPlacementDirector.PlacementHintKey);
                RequestContext.Remove(OperationKey);
            }
        }

        public Task<int> InvokeOutsideAsync(string operation, CancellationToken cancellationToken)
        {
            RequestContext.Set(IPlacementDirector.PlacementHintKey, C.SiloAddress);
            RequestContext.Set(OperationKey, MatrixOperation(operation));
            try { return Grain.Execute(Argument, cancellationToken); }
            finally
            {
                RequestContext.Remove(IPlacementDirector.PlacementHintKey);
                RequestContext.Remove(OperationKey);
            }
        }

        public async ValueTask DisposeAsync()
        {
            Control.ReleaseAll();
            await _cluster.DisposeAsync();
        }
    }

    public sealed partial class Control
    {
        internal GrainId GrainId;
        internal SiloAddress? A;
        internal SiloAddress? B;
        internal SiloAddress? C;
        internal bool FailUnregister;
        internal bool HoldDisposal;
        internal IGrainCallCancellationManager? CancellationManager;
        internal bool AwaitOutsideInitialRoute;
        internal DeactivationReasonCode DeactivationReason;
        internal ControlledDirectory Directory { get; }
        internal ConcurrentQueue<Execution> Executions { get; } = new();
        internal ConcurrentQueue<SiloAddress> Admissions { get; } = new();
        internal ConcurrentQueue<RouteUpdate> RouteUpdates { get; } = new();
        internal ConcurrentQueue<RouteUpdate> InitialRouteUpdates { get; } = new();
        internal ConcurrentDictionary<string, TaskCompletionSource<ReceivedRequest>> GatewayRequests { get; } = new();
        internal ConcurrentDictionary<(SiloAddress, string), TaskCompletionSource<ReceivedRequest>> ReceiverRequests { get; } = new();
        internal TaskCompletionSource<ReceivedRequest> GatewayRequest(string operation) => GatewayRequests.GetOrAdd(
            MatrixOperation(operation), _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
        internal TaskCompletionSource<ReceivedRequest> ReceiverRequest(SiloAddress silo, string operation) => ReceiverRequests.GetOrAdd(
            (silo, MatrixOperation(operation)), _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
        internal ConcurrentDictionary<Message, byte> RecordedRouteMessages { get; } = new(ReferenceEqualityComparer.Instance);
        internal TaskCompletionSource<ActivationData> DeactivationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<ActivationData> ReplacementActivationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<GrainAddress> UnregisterEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<ReceivedRequest> RequestOnA { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<ReceivedRequest> IncomingRequestOnA { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<ReceivedRequest> RequestOnC { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<RouteUpdate> RouteUpdateObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<RouteUpdate> OutsideRouteOnA { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<RouteUpdate> OutsideRouteOnC { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<Task<int>> GrainCallerInvocationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseDeactivation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseUnregister { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseReplacementActivation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DisposalEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseDisposal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Control() => Directory = new(this);

        internal void ReleaseAll()
        {
            ReleaseDeactivation.TrySetResult();
            ReleaseUnregister.TrySetResult();
            ReleaseReplacementActivation.TrySetResult();
            ReleaseDisposal.TrySetResult();
            OutsideRouteOnA.TrySetCanceled();
            OutsideRouteOnC.TrySetCanceled();
            ReleaseFailureMatrixBarriers();
        }
    }

    internal sealed record Execution(SiloAddress Silo, int Argument, string? Operation);
    internal sealed record ReceivedRequest(
        CorrelationId Id, GrainId SendingGrain, SiloAddress? SendingSilo, GrainId TargetGrain, SiloAddress? TargetSilo,
        int ForwardCount, string? Operation, SiloAddress? PlacementHint,
        Message.Directions Direction, TimeSpan? Remaining);
    internal sealed record RouteUpdate(
        CorrelationId Id, int Generation, SiloAddress ForwardedTo, SiloAddress? SendingSilo);

    private sealed class AdmissionObserver(Control control) : IIncomingGrainCallFilter
    {
        public Task Invoke(IIncomingGrainCallContext context)
        {
            if (context.Grain is SafeSiloRetirementGrain
                && context.MethodName is nameof(ISafeSiloRetirementGrain.Execute) or nameof(ISafeSiloRetirementGrain.Submit))
            {
                control.Admissions.Enqueue(((ActivationData)context.TargetContext).Address.SiloAddress!);
            }

            return context.Invoke();
        }
    }

    private sealed class MessageObserver(Control control, ILocalSiloDetails local) : IMessageStatisticsSink
    {
        public Action<Message> GetMessageObserver() => message =>
        {
            if ((message.Direction == Message.Directions.Request
                    || control.FailureMatrix is not null && message.Direction == Message.Directions.OneWay)
                && message.TargetGrain == control.GrainId
                && message.RequestContextData?.TryGetValue(OperationKey, out var operation) == true
                && (Equals(operation, OperationValue)
                    || operation is string name
                    && name.StartsWith(OperationValue + ":", StringComparison.Ordinal)))
            {
                var snapshot = Snapshot(message, operation as string);
                control.ObserveFailureMatrixRequest(local.SiloAddress, snapshot);
                if (local.SiloAddress.Equals(message.TargetSilo) && operation is string marker)
                    control.ReceiverRequests.GetOrAdd((local.SiloAddress, marker), _ => new(TaskCreationOptions.RunContinuationsAsynchronously))
                        .TrySetResult(snapshot);
                if (local.SiloAddress.Equals(control.A))
                {
                    control.RequestOnA.TrySetResult(snapshot);
                }
                else if (local.SiloAddress.Equals(control.C))
                {
                    control.RequestOnC.TrySetResult(snapshot);
                }
            }
            else
            {
                ObserveRoute(control, local.SiloAddress, message);
                if (message.Direction == Message.Directions.Response && message.Result == Message.ResponseTypes.Rejection
                    && message.SendingGrain == control.GrainId && local.SiloAddress.Equals(control.B))
                    control.TransportRejectionObserved.TrySetResult(message.Id);
            }
        };

        internal static ReceivedRequest Snapshot(Message message, string? operation) => new(
            message.Id, message.SendingGrain, message.SendingSilo, message.TargetGrain, message.TargetSilo,
            message.ForwardCount, operation,
            message.RequestContextData?.GetValueOrDefault(IPlacementDirector.PlacementHintKey) as SiloAddress,
            message.Direction, message.TimeToLive);

        internal static void ObserveRoute(Control control, SiloAddress observer, Message message)
        {
            if (message.Direction != Message.Directions.Response || message.Result != Message.ResponseTypes.Status
                || message.SendingGrain != control.GrainId || message.BodyObject is not StatusResponse { ForwardedTo: { } destination })
                return;
            var snapshot = new RouteUpdate(message.Id, message.ForwardCount, destination, message.SendingSilo);
            if (observer.Equals(message.SendingSilo) && control.RecordedRouteMessages.TryAdd(message, 0))
            {
                if (message.ForwardCount == 0) control.InitialRouteUpdates.Enqueue(snapshot);
                else control.RouteUpdates.Enqueue(snapshot);
            }
            // Initial outside-client placement is generation0; observe the subsequent hop.
            if (message.ForwardCount > 0) control.RouteUpdateObserved.TrySetResult(snapshot);
        }
    }

    private sealed class ControlledDirectoryResolver(Control control, GrainTypeResolver types) : IGrainDirectoryResolver
    {
        private readonly GrainType _target = types.GetGrainType(typeof(SafeSiloRetirementGrain));
        private readonly GrainType _reentrant = types.GetGrainType(typeof(SafeSiloRetirementReentrantGrain));

        public bool TryResolveGrainDirectory(GrainType grainType, GrainProperties? properties, [NotNullWhen(true)] out IGrainDirectory? grainDirectory)
        {
            grainDirectory = grainType == _target || grainType == _reentrant ? control.Directory : null;
            return grainDirectory is not null;
        }
    }

    internal sealed class ControlledDirectory(Control control) : IGrainDirectory
    {
        private readonly object _lock = new();
        private GrainAddress? _registration;
        internal GrainAddress? Registration
        {
            get
            {
                lock (_lock) return _registration;
            }
        }

        public async Task<GrainAddress?> Lookup(GrainId grainId)
        {
            if (Registration is null && control.FailureMatrix?.HoldEmptyLookup == true)
            {
                control.MatrixEmptyLookupEntered.TrySetResult(grainId);
                await control.MatrixReleaseLookup.Task;
            }

            return Registration;
        }

        public Task<GrainAddress?> Register(GrainAddress address) => Register(address, null, CancellationToken.None);

        public Task<GrainAddress?> Register(GrainAddress address, GrainAddress? previousAddress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_lock)
            {
                if (_registration is null || _registration.Equals(previousAddress))
                {
                    _registration = address;
                }

                return Task.FromResult<GrainAddress?>(_registration);
            }
        }

        public async Task Unregister(GrainAddress address)
        {
            if (address.SiloAddress!.Equals(control.A))
            {
                control.UnregisterEntered.TrySetResult(address);
                await control.ReleaseUnregister.Task;
                if (control.FailUnregister)
                {
                    throw new InvalidOperationException("Injected conditional unregister failure.");
                }
            }

            lock (_lock)
            {
                // Delayed cleanup must not erase the registration installed on C.
                if (address.Equals(_registration)) _registration = null;
            }
        }

        public Task UnregisterSilos(List<SiloAddress> siloAddresses) => Task.CompletedTask;
    }
}

public interface ISafeSiloRetirementGrain : IGrainWithGuidKey
{
    Task<SiloAddress> GetSiloAddress();
    Task<int> Execute(int argument, CancellationToken cancellationToken);
    [OneWay]
    Task Submit(int argument);
}

public interface ISafeSiloRetirementCallerGrain : IGrainWithGuidKey
{
    Task<SiloAddress> GetSiloAddress();
    Task<int> Call(ISafeSiloRetirementGrain target, int argument, CancellationToken cancellationToken);
}

public sealed class SafeSiloRetirementCallerGrain(SafeSiloRetirementTests.Control control) : Grain, ISafeSiloRetirementCallerGrain
{
    public Task<SiloAddress> GetSiloAddress() => Task.FromResult(((ActivationData)GrainContext).Address.SiloAddress!);

    public Task<int> Call(ISafeSiloRetirementGrain target, int argument, CancellationToken cancellationToken)
    {
        var originalInvocation = target.Execute(argument, cancellationToken);
        control.GrainCallerInvocationEntered.TrySetResult(originalInvocation);
        return originalInvocation;
    }
}

public class SafeSiloRetirementGrain(SafeSiloRetirementTests.Control control) : Grain, ISafeSiloRetirementGrain, IAsyncDisposable
{
    private ActivationData Activation => (ActivationData)GrainContext;

    public async Task<SiloAddress> GetSiloAddress()
    {
        if (control.AwaitOutsideInitialRoute && Activation.Address.SiloAddress!.Equals(control.A))
        {
            await control.OutsideRouteOnA.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }

        return Activation.Address.SiloAddress!;
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await control.OnFailureMatrixActivationAsync(Activation, cancellationToken);
        if (Activation.Address.SiloAddress!.Equals(control.C))
        {
            control.ReplacementActivationEntered.TrySetResult(Activation);
            await control.ReleaseReplacementActivation.Task.WaitAsync(cancellationToken);
        }
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        if (Activation.Address.SiloAddress!.Equals(control.A))
        {
            control.DeactivationReason = reason.ReasonCode;
            control.MatrixHookToken = cancellationToken;
            control.DeactivationEntered.TrySetResult(Activation);
            await control.ReleaseDeactivation.Task.WaitAsync(cancellationToken);
        }
    }

    public async Task<int> Execute(int argument, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var execution = new SafeSiloRetirementTests.Execution(
            Activation.Address.SiloAddress!, argument, RequestContext.Get("safe-retirement-operation") as string);
        control.Executions.Enqueue(execution);
        if (control.HoldFailureMatrixExecution(execution))
        {
            control.MatrixExecutionEntered.TrySetResult(execution);
            await control.MatrixReleaseExecution.Task.WaitAsync(cancellationToken);
        }

        return argument + 1;
    }

    public Task Submit(int argument) => Execute(argument, CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        if (control.HoldDisposal && Activation.Address.SiloAddress!.Equals(control.A))
        {
            control.DisposalEntered.TrySetResult();
            await control.ReleaseDisposal.Task;
        }
    }
}
