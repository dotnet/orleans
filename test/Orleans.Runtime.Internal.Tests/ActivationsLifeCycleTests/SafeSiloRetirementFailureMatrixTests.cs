using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Orleans.Concurrency;
using Orleans.Configuration;
using Orleans.Connections;
using Orleans.Connections.Transport;
using Orleans.Metadata;
using Orleans.Placement;
using Orleans.Runtime;
using Orleans.Runtime.Diagnostics;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Messaging;
using Orleans.Runtime.Placement;
using Orleans.Runtime.Scheduler;
using Orleans.TestingHost;
using Orleans.TestingHost.Diagnostics;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Invocation;
using Xunit;

namespace UnitTests.ActivationsLifeCycleTests;

public sealed partial class SafeSiloRetirementTests
{
    private const int RunningArgument = 73;
    private const int InterleavedArgument = 83;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureMatrix_AmbiguousAcceptedWrite_DoesNotReplayAndAwaitsOriginalOutcome(bool expireDeadline)
    {
        await using var fixture = await Fixture.CreateAsync(holdCancellationDelivery: true, failureMatrix: new());
        var control = fixture.Control;
        var activation = fixture.OriginalActivation;
        activation.Deactivate(new(DeactivationReasonCode.ShuttingDown, "Hold original accepted request."), TestCancellation);
        await control.DeactivationEntered.Task.WaitAsync(Timeout, TestCancellation);
        var invocation = fixture.InvokeMatrixAsync(Argument, "accepted-write", TestCancellation);
        var original = await MatrixSentAsync(control, "accepted-write");
        await MatrixReceivedAsync(control, fixture.A.SiloAddress, "accepted-write");
        Assert.Equal(1, activation.WaitingCount);
        var connection = await fixture.B.ServiceProvider.GetRequiredService<ConnectionManager>()
            .GetConnection(fixture.A.SiloAddress);
        using var accepted = new Message
        {
            Direction = Message.Directions.Request,
            Id = original.Id,
            SendingGrain = original.SendingGrain,
            SendingSilo = original.SendingSilo,
            TargetGrain = original.TargetGrain,
            TargetSilo = original.TargetSilo,
            BodyObject = "must never replay this transport payload",
            TimeToLive = original.Remaining,
        };
        var shared = fixture.B.ServiceProvider.GetRequiredService<MessageHandlerShared>();
        var write = shared.GetSendMessageHandler(connection);
        write.WriteMessage(accepted);
        write.CompleteWriting();
        await using var transport = new BoundaryWriteTransport();
        Assert.True(transport.EnqueueWrite(write));
        Assert.Same(accepted, write.GetMessage(0));
        // Fail an actually accepted MessageWriteRequest bound to the real DI
        // SiloConnection. No message flag or direct retry/reroute invocation.
        await transport.CloseAsync(new ConnectionClosedException(), TestCancellation);
        await connection.DrainAsync().WaitAsync(Timeout, TestCancellation);
        Assert.False(control.TransportRejectionObserved.Task.IsCompleted);
        Assert.Null(accepted.BodyObject);
        Assert.Equal(original.Id, accepted.Id);
        Assert.Equal(original.SendingGrain, accepted.SendingGrain);
        Assert.Equal(original.TargetGrain, accepted.TargetGrain);
        Assert.False(invocation.IsCompleted);
        Assert.Equal(1, fixture.RunningTargetRequests);
        Assert.Equal(1, activation.WaitingCount);
        Assert.Equal(0, accepted.RetryCount);
        Assert.Equal(1, transport.AcceptedWrites);
        Assert.Empty(control.Executions);
        if (expireDeadline)
        {
            fixture.ExpireOriginalDeadline();
            await AssertOriginalTimeoutAsync(invocation);
            Assert.Equal(0, fixture.RunningTargetRequests);
            foreach (var message in activation.DequeueAllWaitingRequests()) message.Dispose();
        }
        else
        {
            control.ReleaseDeactivation.TrySetResult();
            await control.UnregisterEntered.Task.WaitAsync(Timeout, TestCancellation);
            control.ReleaseUnregister.TrySetResult();
            await control.ReplacementActivationEntered.Task.WaitAsync(Timeout, TestCancellation);
            var forwarded = await MatrixReceivedAsync(control, fixture.C.SiloAddress, "accepted-write");
            Assert.Equal(original.Id, forwarded.Id);
            Assert.Equal(1, forwarded.ForwardCount);
            control.ReleaseReplacementActivation.TrySetResult();
            Assert.Equal(Result, await invocation.WaitAsync(Timeout, TestCancellation));
            AssertMatrixEntry(control, "accepted-write", fixture.C.SiloAddress, Argument);
            Assert.Single(control.Executions);
            Assert.Single(control.Admissions);
            Assert.Equal(0, fixture.RunningTargetRequests);
        }
    }

    [Theory]
    [InlineData(false, "grain", false)]
    [InlineData(true, "grain", true)]
    [InlineData(false, "client", false)]
    [InlineData(true, "client", true)]
    [InlineData(false, "system", false)]
    [InlineData(true, "system", false)]
    public async Task FailureMatrix_RetirementPhaseRecordsApplicationWriteFailuresAcrossRemovedConnections(
        bool beginBeforeFailure, string origin, bool expectedFailure)
    {
        await using var fixture = await Fixture.CreateAsync();
        var center = fixture.B.ServiceProvider.GetRequiredService<MessageCenter>();
        var manager = fixture.B.ServiceProvider.GetRequiredService<ConnectionManager>();
        var connection = await manager.GetConnection(fixture.A.SiloAddress);
        var shared = fixture.B.ServiceProvider.GetRequiredService<MessageHandlerShared>();
        var systemCaller = SystemTargetGrainId.Create(Constants.CatalogType, fixture.A.SiloAddress).GrainId;
        using var reply = new Message
        {
            Direction = Message.Directions.Response,
            IsSystemMessage = true,
            Id = new CorrelationId(173),
            SendingSilo = fixture.B.SiloAddress,
            SendingGrain = origin switch
            {
                "grain" => fixture.Control.GrainId,
                "client" => GrainId.Create("sys.client", "observer"),
                _ => SystemTargetGrainId.Create(Constants.CatalogType, fixture.B.SiloAddress).GrainId,
            },
            TargetGrain = systemCaller,
            TargetSilo = fixture.A.SiloAddress,
            BodyObject = Response.FromResult(174),
        };
        Assert.True(reply.TargetGrain.IsSystemTarget());
        Assert.Equal(origin != "system", reply.RequiresApplicationDrain);
        if (beginBeforeFailure) center.BeginRetirement();
        var write = shared.GetSendMessageHandler(connection);
        write.WriteMessage(reply);
        write.CompleteWriting();
        await using var transport = new BoundaryWriteTransport();
        Assert.True(transport.EnqueueWrite(write));
        // Exercise the accepted-write exception and concrete accounting hook. Response
        // traffic retains its normal reroute behavior, but the initial failure preceded
        // the connection's drain and must be remembered only by the silo phase boundary.
        await transport.CloseAsync(new ConnectionClosedException(), TestCancellation);
        if (!beginBeforeFailure) center.BeginRetirement();
        await connection.DrainAsync().WaitAsync(Timeout, TestCancellation);
        // Remove every live connection before drain: MessageCenter must retain the earlier
        // phase failure rather than relying on the ConnectionManager's current snapshot.
        await manager.Close(TestCancellation).WaitAsync(Timeout, TestCancellation);
        Assert.Equal(0, manager.ConnectionCount);
        Assert.True(manager.Closed.IsCompletedSuccessfully);
        if (expectedFailure)
        {
            var error = await Assert.ThrowsAsync<ConnectionClosedException>(
                () => center.DrainRetirementAsync(TestCancellation).WaitAsync(Timeout, TestCancellation));
            Assert.Contains("unsuccessful", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        else await center.DrainRetirementAsync(TestCancellation).WaitAsync(Timeout, TestCancellation);
        Assert.Equal(1, transport.AcceptedWrites);
        Assert.Empty(fixture.Control.Executions);
    }

    [Fact]
    public async Task FailureMatrix_GatewayApplicationReplyFailureIsRecordedBeforeConnectionDrain()
    {
        await using var fixture = await Fixture.CreateAsync();
        var services = fixture.B.ServiceProvider;
        var center = services.GetRequiredService<MessageCenter>();
        var shared = services.GetRequiredService<MessageHandlerShared>();
        await using var transport = new BoundaryWriteTransport();
        var connection = new GatewayInboundConnection(
            transport, center.Gateway!, services.GetRequiredService<OverloadDetector>(),
            services.GetRequiredService<ILocalSiloDetails>(), services.GetRequiredService<IOptions<ConnectionOptions>>().Value,
            center, services.GetRequiredService<ConnectionCommon>(),
            services.GetRequiredService<ConnectionPreambleHelper>(), new GatewayInstruments(services.GetRequiredService<OrleansInstruments>()));
        using var reply = new Message
        {
            Direction = Message.Directions.Response,
            IsSystemMessage = true,
            SendingGrain = GrainId.Create("sys.client", "observer"),
            SendingSilo = fixture.B.SiloAddress,
            TargetGrain = SystemTargetGrainId.Create(Constants.CatalogType, fixture.A.SiloAddress).GrainId,
            TargetSilo = fixture.A.SiloAddress,
            BodyObject = Response.FromResult(174),
            // Exercise the failure hook without admitting another physical write during
            // the nonrelocatable response's normal reroute/drop handling.
            RetryCount = MessagingOptions.DEFAULT_MAX_MESSAGE_SEND_RETRIES,
        };
        Assert.True(reply.SendingGrain.IsClient());
        Assert.True(reply.TargetGrain.IsSystemTarget());
        Assert.True(reply.RequiresApplicationDrain);
        center.BeginRetirement();
        var write = shared.GetSendMessageHandler(connection);
        write.WriteMessage(reply);
        write.CompleteWriting();
        Assert.True(transport.EnqueueWrite(write));
        await transport.CloseAsync(new ConnectionClosedException(), TestCancellation);
        // The concrete gateway hook records the failure before this local drain begins.
        await connection.DrainAsync().WaitAsync(Timeout, TestCancellation);
        await connection.CloseAsync(null).WaitAsync(Timeout, TestCancellation);
        var error = await Assert.ThrowsAsync<ConnectionClosedException>(
            () => center.DrainRetirementAsync(TestCancellation).WaitAsync(Timeout, TestCancellation));
        Assert.Contains("unsuccessful", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, transport.AcceptedWrites);
        Assert.Null(reply.BodyObject);
        Assert.Empty(fixture.Control.Executions);
    }

    private sealed class BoundaryWriteTransport : MessageTransport
    {
        private MessageWriteRequest? _write;
        public int AcceptedWrites { get; private set; }
        public override CancellationToken Closed => default;
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override bool EnqueueRead(ReadRequest request) => false;
        public override bool EnqueueWrite(WriteRequest request)
        {
            Assert.Null(_write);
            _write = Assert.IsType<MessageWriteRequest>(request);
            AcceptedWrites++;
            return true;
        }
        public override ValueTask CloseAsync(Exception? closeException, CancellationToken cancellationToken = default)
        {
            Interlocked.Exchange(ref _write, null)?.SetException(closeException ?? new ConnectionClosedException());
            return default;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureMatrix_RunningAndWaitingRequests_OnlyWaitingInvocationMoves(bool reentrant)
    {
        var options = new FailureMatrixOptions { Reentrant = reentrant, HeldArgument = RunningArgument };
        await using var fixture = await Fixture.CreateAsync(
            holdCancellationDelivery: true, failureMatrix: options);
        var control = fixture.Control;
        var activation = fixture.OriginalActivation;
        var runningTask = fixture.InvokeMatrixAsync(RunningArgument, "running", TestCancellation);
        var running = await MatrixSentAsync(control, "running");
        var entered = await control.MatrixExecutionEntered.Task.WaitAsync(Timeout, TestCancellation);
        Assert.Equal(fixture.A.SiloAddress, entered.Silo);
        Assert.Equal(RunningArgument, entered.Argument);
        Assert.Equal(MatrixOperation("running"), entered.Operation);
        Assert.Equal(1, activation.GetRequestCount());

        ReceivedRequest? interleaved = null;
        if (reentrant)
        {
            // Verify real reentrant admission, not merely an attribute on a dormant class.
            var probe = fixture.InvokeMatrixAsync(InterleavedArgument, "interleaved", TestCancellation);
            interleaved = await MatrixSentAsync(control, "interleaved");
            Assert.Equal(InterleavedArgument + 1, await probe.WaitAsync(Timeout, TestCancellation));
            Assert.False(runningTask.IsCompleted);
            await activation.QueueAction(
                context => Assert.Equal(1, context.GetRequestCount()),
                activation).WaitAsync(Timeout, TestCancellation);
        }

        using var events = new DiagnosticEventCollector(GrainLifecycleEvents.ListenerName);
        var deactivating = events.WaitForEventAsync(
            nameof(GrainLifecycleEvents.Deactivating),
            e => e.Payload is GrainLifecycleEvents.Deactivating d && ReferenceEquals(d.GrainContext, activation),
            Timeout, TestCancellation);
        fixture.StartRetirement();
        await deactivating;
        await AssertDeactivationStartedAsync(activation);
        Assert.False(control.DeactivationEntered.Task.IsCompleted);

        var queuedTask = fixture.InvokeMatrixAsync(Argument, "waiting", TestCancellation);
        var queued = await MatrixSentAsync(control, "waiting");
        var queuedOnA = await MatrixReceivedAsync(control, fixture.A.SiloAddress, "waiting");
        Assert.NotEqual(running.Id, queued.Id);
        Assert.Equal(queued.Id, queuedOnA.Id);
        Assert.Equal(1, activation.WaitingCount);
        Assert.Equal(2, activation.GetRequestCount());
        Assert.True(activation.IsCurrentlyExecuting);
        Assert.False(queuedTask.IsCompleted);
        Assert.DoesNotContain(control.Executions, e => e.Operation == MatrixOperation("waiting"));
        Assert.Equal(reentrant ? 2 : 1, control.Admissions.Count);
        Assert.All(control.Admissions, silo => Assert.Equal(fixture.A.SiloAddress, silo));

        control.MatrixReleaseExecution.TrySetResult();
        Assert.Equal(RunningArgument + 1, await runningTask.WaitAsync(Timeout, TestCancellation));
        Assert.Equal(activation, await control.DeactivationEntered.Task.WaitAsync(Timeout, TestCancellation));
        control.ReleaseDeactivation.TrySetResult();
        await control.UnregisterEntered.Task.WaitAsync(Timeout, TestCancellation);
        control.ReleaseUnregister.TrySetResult();
        var replacement = await control.ReplacementActivationEntered.Task.WaitAsync(Timeout, TestCancellation);
        var onC = await MatrixReceivedAsync(control, fixture.C.SiloAddress, "waiting");
        Assert.Equal(queued.Id, onC.Id);
        Assert.Equal(1, onC.ForwardCount);
        Assert.Equal(queued.SendingGrain, onC.SendingGrain);
        Assert.Equal(queued.SendingSilo, onC.SendingSilo);
        Assert.Equal(queued.TargetGrain, onC.TargetGrain);
        Assert.NotNull(queued.Remaining);
        Assert.NotNull(onC.Remaining);
        Assert.True(onC.Remaining > TimeSpan.Zero);
        Assert.True(onC.Remaining <= queued.Remaining);
        Assert.Equal(MatrixOperation("waiting"), onC.Operation);
        Assert.Equal(fixture.C.SiloAddress, replacement.Address.SiloAddress);
        Assert.DoesNotContain(control.MatrixRequests, r =>
            r.Observer.Equals(fixture.C.SiloAddress) && r.Request.Id == running.Id);
        if (interleaved is not null)
        {
            Assert.DoesNotContain(control.MatrixRequests, r =>
                r.Observer.Equals(fixture.C.SiloAddress) && r.Request.Id == interleaved.Id);
        }

        await fixture.CatalogRetirement.WaitAsync(Timeout, TestCancellation);
        await fixture.Retirement!.WaitAsync(Timeout, TestCancellation);
        fixture.BreakOutstandingToA();
        Assert.False(queuedTask.IsCompleted);
        Assert.Equal(1, fixture.RunningTargetRequests);
        control.ReleaseReplacementActivation.TrySetResult();
        Assert.Equal(Result, await queuedTask.WaitAsync(Timeout, TestCancellation));
        Assert.Equal(0, fixture.RunningTargetRequests);
        AssertMatrixEntry(control, "running", fixture.A.SiloAddress, RunningArgument);
        AssertMatrixEntry(control, "waiting", fixture.C.SiloAddress, Argument);
        if (reentrant)
        {
            AssertMatrixEntry(control, "interleaved", fixture.A.SiloAddress, InterleavedArgument);
        }

        Assert.Equal(reentrant ? 3 : 2, control.Executions.Count);
        Assert.Equal(reentrant ? 3 : 2, control.Admissions.Count);
        var route = Assert.Single(control.RouteUpdates);
        Assert.Equal(queued.Id, route.Id);
        Assert.Equal(fixture.C.SiloAddress, route.ForwardedTo);
        Assert.Equal(1, route.Generation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureMatrix_WholeHostBudgetExpiresBeforeUnregister_NoForwarding(bool holdDirectory)
    {
        await using var fixture = await Fixture.CreateAsync(
            holdCancellationDelivery: true, failureMatrix: new());
        var control = fixture.Control;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(TestCancellation);
        fixture.StartRetirementWithBudget(budget.Token);
        var activation = await control.DeactivationEntered.Task.WaitAsync(Timeout, TestCancellation);
        var originalTask = fixture.InvokeMatrixAsync(Argument, "host-budget", TestCancellation);
        var sent = await MatrixSentAsync(control, "host-budget");
        var received = await MatrixReceivedAsync(control, fixture.A.SiloAddress, "host-budget");
        Assert.Equal(sent.Id, received.Id);
        Assert.Equal(1, activation.WaitingCount);
        Assert.False(activation.Deactivated.IsCompleted);
        Assert.False(fixture.CatalogRetirement.IsCompleted);
        Assert.Empty(control.Executions);
        Assert.Empty(control.Admissions);
        if (holdDirectory)
        {
            control.ReleaseDeactivation.TrySetResult();
            Assert.Equal(activation.Address, await control.UnregisterEntered.Task.WaitAsync(Timeout, TestCancellation));
        }

        // This token is the REAL host-stop budget. Its deterministic expiration occurs only
        // after the selected hook/directory barrier, without sleeping for a wall-clock timer.
        budget.Cancel();
        await AwaitHostStoppedAsync(fixture.Retirement!, fixture.A, budget.Token);
        Assert.True(control.MatrixHookToken.IsCancellationRequested);
        await fixture.AssertCatalogRetirementFailedAsync();
        Assert.Equal(activation.Address, control.Directory.Registration);
        Assert.Empty(control.RouteUpdates);
        Assert.False(control.ReplacementActivationEntered.Task.IsCompleted);
        Assert.Empty(control.Executions);
        Assert.Empty(control.Admissions);
        fixture.Caller.BreakOutstandingMessagesToSilo(fixture.A.SiloAddress);
        fixture.ExpireOriginalDeadline();
        await AssertEstablishedFailureAsync(originalTask);
        Assert.Equal(0, fixture.RunningTargetRequests);
        Assert.DoesNotContain(control.MatrixRequests, r =>
            r.Observer.Equals(fixture.C.SiloAddress) && r.Request.Id == sent.Id);
    }

    [Fact]
    public async Task FailureMatrix_DisposalCaptureAndRemovedActivationHoldCatalogRetirement()
    {
        var options = new FailureMatrixOptions();
        await using var fixture = await Fixture.CreateAsync(failureMatrix: options);
        var control = fixture.Control;
        control.HoldDisposal = true;
        fixture.StartRetirement();
        var activation = await control.DeactivationEntered.Task.WaitAsync(Timeout, TestCancellation);
        var queuedTask = fixture.InvokeMatrixAsync(Argument, "queued-through-disposal", TestCancellation);
        var queued = await MatrixSentAsync(control, "queued-through-disposal");
        await MatrixReceivedAsync(control, fixture.A.SiloAddress, "queued-through-disposal");
        Assert.Equal(1, activation.WaitingCount);

        control.ReleaseDeactivation.TrySetResult();
        await control.UnregisterEntered.Task.WaitAsync(Timeout, TestCancellation);
        control.ReleaseUnregister.TrySetResult();
        await control.DisposalEntered.Task.WaitAsync(Timeout, TestCancellation);
        Assert.Equal(ActivationState.Invalid, activation.State);
        Assert.Null(fixture.A.ServiceProvider.GetRequiredService<ActivationDirectory>().FindTarget(control.GrainId));
        Assert.Equal(1, activation.WaitingCount);
        Assert.False(activation.Deactivated.IsCompleted);
        Assert.False(fixture.CatalogRetirement.IsCompleted);

        // The retired activation still owns its old queue while disposal is held.
        // A new catalog arrival uses the ordinary invalid-activation path instead.
        var lateTask = fixture.InvokeMatrixAsync(273, "late-through-disposal", TestCancellation);
        var late = await MatrixSentAsync(control, "late-through-disposal");
        Assert.Equal(fixture.A.SiloAddress, late.TargetSilo);
        ReceivedRequest forwardedLate;
        try
        {
            forwardedLate = await MatrixReceivedAsync(control, fixture.C.SiloAddress, "late-through-disposal");
        }
        catch (TimeoutException error)
        {
            throw new TimeoutException(
                $"Late request did not reach C during disposal: late={lateTask.Status}, queued={queuedTask.Status}, "
                + $"activation={activation.State}, waiting={activation.WaitingCount}, registration={control.Directory.Registration}, "
                + $"routes=[{string.Join(", ", control.RouteUpdates)}], "
                + $"requests=[{string.Join(", ", control.MatrixRequests.Select(request => $"{request.Observer}:{request.Request.TargetSilo}/{request.Request.ForwardCount}/{request.Request.Operation}"))}].",
                error);
        }
        Assert.Equal(1, activation.WaitingCount);
        Assert.Equal(late.Id, Assert.Single(control.RouteUpdates).Id);
        Assert.Equal(fixture.A.SiloAddress, Assert.Single(control.RouteUpdates).SendingSilo);
        Assert.Empty(control.Executions);
        Assert.Empty(control.Admissions);
        Assert.False(fixture.Retirement!.IsCompleted);

        control.ReleaseDisposal.TrySetResult();
        await activation.Deactivated.WaitAsync(Timeout, TestCancellation);
        await fixture.CatalogRetirement.WaitAsync(Timeout, TestCancellation);
        Assert.Equal(0, activation.WaitingCount);
        Assert.False(queuedTask.IsCompleted);
        Assert.False(lateTask.IsCompleted);
        var forwardedQueued = await MatrixReceivedAsync(control, fixture.C.SiloAddress, "queued-through-disposal");
        foreach (var (original, forwarded) in new[] { (queued, forwardedQueued), (late, forwardedLate) })
        {
            Assert.Equal(original.Id, forwarded.Id);
            Assert.Equal(original.SendingGrain, forwarded.SendingGrain);
            Assert.Equal(original.TargetGrain, forwarded.TargetGrain);
            Assert.Equal(original.Operation, forwarded.Operation);
            Assert.Equal(fixture.C.SiloAddress, forwarded.TargetSilo);
            Assert.Equal(1, forwarded.ForwardCount);
        }
        await fixture.Retirement.WaitAsync(Timeout, TestCancellation);
        fixture.BreakOutstandingToA();
        control.ReleaseReplacementActivation.TrySetResult();
        Assert.Equal(Result, await queuedTask.WaitAsync(Timeout, TestCancellation));
        Assert.Equal(274, await lateTask.WaitAsync(Timeout, TestCancellation));
        Assert.Equal(2, control.Executions.Count);
        Assert.All(control.Executions, execution => Assert.Equal(fixture.C.SiloAddress, execution.Silo));
        Assert.Equal(0, fixture.RunningTargetRequests);
        AssertNoCallerReplay(control, "queued-through-disposal", queued.Id, maxForwardCount: 1);
        AssertNoCallerReplay(control, "late-through-disposal", late.Id, maxForwardCount: 1);
    }

    [Fact]
    public async Task FailureMatrix_UpstreamHostBudgetExpiresDuringDisposal_ReportsCanceledDrain()
    {
        await using var fixture = await Fixture.CreateAsync(
            holdCancellationDelivery: true, failureMatrix: new());
        var control = fixture.Control;
        control.HoldDisposal = true;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(TestCancellation);
        fixture.StartRetirementWithBudget(budget.Token);
        var activation = await control.DeactivationEntered.Task.WaitAsync(Timeout, TestCancellation);
        var originalTask = fixture.InvokeMatrixAsync(Argument, "disposal-budget", TestCancellation);
        var original = await MatrixSentAsync(control, "disposal-budget");
        await MatrixReceivedAsync(control, fixture.A.SiloAddress, "disposal-budget");
        control.ReleaseDeactivation.TrySetResult();
        await control.UnregisterEntered.Task.WaitAsync(Timeout, TestCancellation);
        control.ReleaseUnregister.TrySetResult();
        await control.DisposalEntered.Task.WaitAsync(Timeout, TestCancellation);
        Assert.Null(control.Directory.Registration);
        Assert.False(activation.Deactivated.IsCompleted);
        Assert.False(fixture.CatalogRetirement.IsCompleted);
        Assert.Empty(control.Executions);
        Assert.Empty(control.Admissions);

        // Disposal itself canceled the linked command CTS already. Expiring the ORIGINAL
        // host budget now is a separate event which must not be lost in an earlier snapshot.
        budget.Cancel();
        Assert.True(budget.IsCancellationRequested);
        Assert.False(activation.Deactivated.IsCompleted);
        Assert.False(fixture.CatalogRetirement.IsCompleted);
        control.ReleaseDisposal.TrySetResult();
        await fixture.AssertCatalogRetirementFailedAsync();
        await AwaitHostStoppedAsync(fixture.Retirement!, fixture.A, budget.Token);
        Assert.True(activation.Deactivated.IsCompletedSuccessfully);
        Assert.Empty(control.Executions);
        Assert.Empty(control.Admissions);
        Assert.NotEqual(default, original.Id);
        fixture.ExpireOriginalDeadline();
        await AssertEstablishedFailureAsync(originalTask);
        Assert.Equal(0, fixture.RunningTargetRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureMatrix_StuckDeactivation_IsAbandonedWithoutCallerReplay(bool reentrant)
    {
        var options = new FailureMatrixOptions
        {
            Reentrant = reentrant,
            HeldArgument = RunningArgument,
            HoldEveryReplacementActivation = true,
        };
        await using var fixture = await Fixture.CreateAsync(
            holdCancellationDelivery: true, failureMatrix: options);
        var control = fixture.Control;
        var activation = fixture.OriginalActivation;
        var runningTask = fixture.InvokeMatrixAsync(RunningArgument, "stuck-running", TestCancellation);
        var running = await MatrixSentAsync(control, "stuck-running");
        await control.MatrixExecutionEntered.Task.WaitAsync(Timeout, TestCancellation);
        activation.Deactivate(new(DeactivationReasonCode.ShuttingDown, "Failure matrix stuck retirement."), TestCancellation);
        Assert.Equal(ActivationState.Deactivating, activation.State);
        Assert.False(control.DeactivationEntered.Task.IsCompleted);
        Assert.False(fixture.CatalogRetirement.IsCompleted);

        var maxProcessing = fixture.A.ServiceProvider.GetRequiredService<IOptions<SiloMessagingOptions>>().Value.MaxRequestProcessingTime;
        options.ActivationClock.Advance(maxProcessing + TimeSpan.FromSeconds(1));
        var waitingTask = fixture.InvokeMatrixAsync(Argument, "stuck-waiting", TestCancellation);
        var waiting = await MatrixSentAsync(control, "stuck-waiting");
        Assert.NotEqual(running.Id, waiting.Id);
        await fixture.AssertCatalogRetirementFailedAsync();
        Assert.True(activation.Deactivated.IsCompletedSuccessfully);
        Assert.Equal(ActivationState.Deactivating, activation.State);
        Assert.True(activation.IsCurrentlyExecuting);
        Assert.False(control.DeactivationEntered.Task.IsCompleted);
        Assert.DoesNotContain(control.RouteUpdates, route => route.Id == running.Id);
        AssertMatrixEntry(control, "stuck-running", fixture.A.SiloAddress, RunningArgument);
        Assert.DoesNotContain(control.Executions, e => e.Operation == MatrixOperation("stuck-waiting"));

        // Normal receiver forwarding may recover the poison address locally.
        // Freeze that replacement below admission; the running invocation never moves.
        var replacement = await control.MatrixReplacementActivationEntered.Task.WaitAsync(Timeout, TestCancellation);
        Assert.NotEqual(activation.ActivationId, replacement.ActivationId);
        Assert.Equal(control.GrainId, replacement.GrainId);
        await control.MatrixSecondReceipt("stuck-waiting").Task.WaitAsync(Timeout, TestCancellation);
        Assert.Equal(1, replacement.WaitingCount);
        var advisory = Assert.Single(control.RouteUpdates);
        Assert.Equal(waiting.Id, advisory.Id);
        Assert.Equal(1, advisory.Generation);
        Assert.Equal(replacement.Address.SiloAddress, advisory.ForwardedTo);
        Assert.All(
            control.MatrixRequests.Where(r => r.Request.Operation == MatrixOperation("stuck-waiting")),
            r => Assert.Equal(waiting.Id, r.Request.Id));
        Assert.DoesNotContain(control.MatrixRequests, r =>
            !r.Observer.Equals(fixture.B.SiloAddress)
            && r.Request.Id == running.Id
            && r.Request.ForwardCount > 0);
        AssertNoCallerReplay(control, "stuck-waiting", waiting.Id, maxForwardCount: 1);

        fixture.ExpireOriginalDeadline();
        await AssertOriginalTimeoutAsync(waitingTask);
        await AssertOriginalTimeoutAsync(runningTask);
        Assert.Equal(0, fixture.RunningTargetRequests);
        // Remove the already-terminal waiting request cooperatively before releasing the
        // frozen replacement; releasing a live queued copy would otherwise execute it later.
        await ((IGrainCallCancellationExtension)replacement).CancelRequestAsync(
            waiting.SendingGrain, waiting.Id, TestCancellation).AsTask().WaitAsync(Timeout, TestCancellation);
        Assert.Equal(0, replacement.WaitingCount);
        Assert.Single(control.Executions);
        Assert.Single(control.Admissions);
        Assert.DoesNotContain(control.RouteUpdates, route => route.Id == running.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureMatrix_AfterUnregisterBeforeForward_CancellationOrCallerStopCannotReplay(bool stopCallerHost)
    {
        var options = new FailureMatrixOptions();
        await using var fixture = await Fixture.CreateAsync(
            holdCancellationDelivery: true, failureMatrix: options);
        var control = fixture.Control;
        var caller = fixture.Caller;
        var messageCenter = fixture.A.ServiceProvider.GetRequiredService<MessageCenter>();
        using var invocationCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestCancellation);
        using var stopBudget = CancellationTokenSource.CreateLinkedTokenSource(TestCancellation);
        options.HoldEmptyLookup = true; // Warmup must complete before enabling the empty-lookup barrier.
        fixture.StartRetirement();
        var activation = await control.DeactivationEntered.Task.WaitAsync(Timeout, TestCancellation);
        var originalTask = fixture.InvokeMatrixAsync(Argument, "queued-after-proof", invocationCancellation.Token);
        var original = await MatrixSentAsync(control, "queued-after-proof");
        await MatrixReceivedAsync(control, fixture.A.SiloAddress, "queued-after-proof");
        control.ReleaseDeactivation.TrySetResult();
        await control.UnregisterEntered.Task.WaitAsync(Timeout, TestCancellation);
        control.ReleaseUnregister.TrySetResult();
        Assert.Equal(control.GrainId, await control.MatrixEmptyLookupEntered.Task.WaitAsync(Timeout, TestCancellation));
        await fixture.CatalogRetirement.WaitAsync(Timeout, TestCancellation);
        Assert.Empty(control.RouteUpdates); // No advisory notice before placement has selected a target.
        Assert.False(originalTask.IsCompleted);
        Assert.Equal(1, fixture.RunningTargetRequests);
        Assert.False(control.ReplacementActivationEntered.Task.IsCompleted);
        Assert.Empty(control.Executions);
        Assert.Empty(control.Admissions);

        // A owns the forwarding placement producer. B callback cancellation cannot
        // retract it, and A's producer drain cannot finish until lookup is released.
        var producerDrain = messageCenter.DrainRetirementAsync(TestCancellation);
        Assert.False(producerDrain.IsCompleted);
        Task? stop = null;
        if (stopCallerHost)
        {
            stop = fixture.StopSiloAsync(fixture.B, stopBudget.Token);
            stopBudget.Cancel();
            var error = await Assert.ThrowsAsync<SiloUnavailableException>(
                () => originalTask.WaitAsync(Timeout, TestCancellation));
            Assert.Contains("local Orleans host is shutting down", error.Message);
        }
        else
        {
            invocationCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => originalTask.WaitAsync(Timeout, TestCancellation));
        }

        Assert.Equal(0, caller.GetRunningRequestsCount(fixture.InterfaceType));
        control.MatrixReleaseLookup.TrySetResult();
        await producerDrain.WaitAsync(Timeout, TestCancellation);
        if (stop is not null)
        {
            await AwaitHostStoppedAsync(stop, fixture.B, stopBudget.Token);
        }

        var replacement = await control.ReplacementActivationEntered.Task.WaitAsync(Timeout, TestCancellation);
        var forwarded = await MatrixReceivedAsync(control, fixture.C.SiloAddress, "queued-after-proof");
        Assert.Equal(original.Id, forwarded.Id);
        Assert.Equal(1, forwarded.ForwardCount);
        await ((IGrainCallCancellationExtension)replacement).CancelRequestAsync(
            original.SendingGrain, original.Id, TestCancellation).AsTask().WaitAsync(Timeout, TestCancellation);
        Assert.Equal(0, replacement.WaitingCount);
        Assert.Empty(control.Executions);
        Assert.Empty(control.Admissions);
        AssertNoCallerReplay(control, "queued-after-proof", original.Id, maxForwardCount: 1);
        Assert.Equal(0, caller.GetRunningRequestsCount(fixture.InterfaceType));
        Assert.Equal(replacement.Address, control.Directory.Registration);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureMatrix_OriginalSiloCrashBeforeProof_NeverReplays(bool holdDirectory)
    {
        await using var fixture = await Fixture.CreateAsync(
            holdCancellationDelivery: true, failureMatrix: new());
        var control = fixture.Control;
        var activation = fixture.OriginalActivation;
        using var owner = CancellationTokenSource.CreateLinkedTokenSource(
            TestCancellation, fixture.A.SiloHost.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping);
        activation.Deactivate(new(DeactivationReasonCode.ShuttingDown, "Crash before retirement proof."), owner.Token);
        await control.DeactivationEntered.Task.WaitAsync(Timeout, TestCancellation);
        var invocation = fixture.InvokeMatrixAsync(Argument, "owner-crash", TestCancellation);
        var original = await MatrixSentAsync(control, "owner-crash");
        var received = await MatrixReceivedAsync(control, fixture.A.SiloAddress, "owner-crash");
        Assert.Equal(original.Id, received.Id);
        Assert.Equal(1, activation.WaitingCount);
        if (holdDirectory)
        {
            control.ReleaseDeactivation.TrySetResult();
            await control.UnregisterEntered.Task.WaitAsync(Timeout, TestCancellation);
        }

        Assert.False(activation.Deactivated.IsCompleted);
        Assert.False(fixture.CatalogRetirement.IsCompleted);
        await fixture.CrashSiloAsync(fixture.A).WaitAsync(Timeout, TestCancellation);
        Assert.False(fixture.A.IsActive);
        Assert.True(owner.IsCancellationRequested);
        await fixture.AssertCatalogRetirementFailedAsync();
        Assert.Equal(activation.Address, control.Directory.Registration);
        Assert.Empty(control.RouteUpdates);
        Assert.Empty(control.Executions);
        Assert.Empty(control.Admissions);
        Assert.False(control.ReplacementActivationEntered.Task.IsCompleted);
        fixture.BreakOutstandingToA();
        fixture.ExpireOriginalDeadline();
        await AssertEstablishedFailureAsync(invocation);
        Assert.Equal(0, fixture.RunningTargetRequests);
        AssertNoCallerReplay(control, "owner-crash", original.Id, maxForwardCount: 0);
    }

    [Fact]
    public async Task FailureMatrix_OriginalSiloCrashAfterForwardBeforeInvocation_OriginalInvocationStillSucceeds()
    {
        var options = new FailureMatrixOptions();
        await using var fixture = await Fixture.CreateAsync(
            holdCancellationDelivery: true, failureMatrix: options);
        var control = fixture.Control;
        var activation = fixture.OriginalActivation;
        activation.Deactivate(new(DeactivationReasonCode.ShuttingDown, "Retire activation before owner crash."), TestCancellation);
        await control.DeactivationEntered.Task.WaitAsync(Timeout, TestCancellation);
        var invocation = fixture.InvokeMatrixAsync(Argument, "owner-crash-after-proof", TestCancellation);
        var original = await MatrixSentAsync(control, "owner-crash-after-proof");
        await MatrixReceivedAsync(control, fixture.A.SiloAddress, "owner-crash-after-proof");
        control.ReleaseDeactivation.TrySetResult();
        await control.UnregisterEntered.Task.WaitAsync(Timeout, TestCancellation);
        control.ReleaseUnregister.TrySetResult();
        var replacement = await control.ReplacementActivationEntered.Task.WaitAsync(Timeout, TestCancellation);
        var accepted = await MatrixReceivedAsync(control, fixture.C.SiloAddress, "owner-crash-after-proof");
        await fixture.CatalogRetirement.WaitAsync(Timeout, TestCancellation);
        Assert.Equal(original.Id, Assert.Single(control.RouteUpdates).Id);
        Assert.Equal(original.Id, accepted.Id);
        Assert.Equal(1, accepted.ForwardCount);
        Assert.Equal(fixture.C.SiloAddress, replacement.Address.SiloAddress);

        // A is still an active host because only its activation retired. Kill it AFTER
        // receiver forwarding has reached C, but BEFORE replacement invocation admission.
        await fixture.CrashSiloAsync(fixture.A).WaitAsync(Timeout, TestCancellation);
        fixture.BreakOutstandingToA();
        Assert.False(invocation.IsCompleted);
        Assert.Equal(1, fixture.RunningTargetRequests);
        Assert.Empty(control.Executions);
        control.ReleaseReplacementActivation.TrySetResult();
        Assert.Equal(Result, await invocation.WaitAsync(Timeout, TestCancellation));
        AssertMatrixEntry(control, "owner-crash-after-proof", fixture.C.SiloAddress, Argument);
        Assert.Single(control.Executions);
        Assert.Single(control.Admissions);
        Assert.Equal(0, fixture.RunningTargetRequests);
    }

    [Fact]
    public async Task FailureMatrix_DestinationCrashAfterReceiptBeforeInvocation_ZeroReplay()
    {
        await using var fixture = await Fixture.CreateAsync(
            holdCancellationDelivery: true, failureMatrix: new());
        var control = fixture.Control;
        var call = await BeginMatrixForwardingAsync(fixture, "destination-before-entry");
        var replacement = await control.ReplacementActivationEntered.Task.WaitAsync(Timeout, TestCancellation);
        var received = await MatrixReceivedAsync(control, fixture.C.SiloAddress, "destination-before-entry");
        Assert.Equal(call.Original.Id, received.Id);
        Assert.Equal(1, received.ForwardCount);
        Assert.Equal(1, replacement.WaitingCount);
        Assert.Empty(control.Executions);
        Assert.Empty(control.Admissions);

        await fixture.CrashSiloAsync(fixture.A).WaitAsync(Timeout, TestCancellation);
        await fixture.CrashSiloAsync(fixture.C).WaitAsync(Timeout, TestCancellation);
        fixture.Caller.BreakOutstandingMessagesToSilo(fixture.A.SiloAddress);
        fixture.Caller.BreakOutstandingMessagesToSilo(fixture.C.SiloAddress);
        fixture.ExpireOriginalDeadline();
        await AssertEstablishedFailureAsync(call.Invocation);
        Assert.Equal(0, fixture.RunningTargetRequests);
        Assert.Empty(control.Executions);
        Assert.Empty(control.Admissions);
        AssertNoCallerReplay(control, "destination-before-entry", call.Original.Id, maxForwardCount: 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureMatrix_DestinationLostAfterApplicationEntry_OriginalTimeoutAndNoReplay(
        bool partition)
    {
        var options = new FailureMatrixOptions { HeldArgument = Argument, HoldOnC = true };
        await using var fixture = await Fixture.CreateAsync(
            holdCancellationDelivery: true, failureMatrix: options);
        var control = fixture.Control;
        var call = await BeginMatrixForwardingAsync(fixture, "destination-after-entry");
        await control.ReplacementActivationEntered.Task.WaitAsync(Timeout, TestCancellation);
        var received = await MatrixReceivedAsync(control, fixture.C.SiloAddress, "destination-after-entry");
        Assert.Equal(call.Original.Id, received.Id);
        Assert.Equal(1, received.ForwardCount);
        control.ReleaseReplacementActivation.TrySetResult();
        var entry = await control.MatrixExecutionEntered.Task.WaitAsync(Timeout, TestCancellation);
        Assert.Equal(fixture.C.SiloAddress, entry.Silo);
        Assert.Equal(Argument, entry.Argument);
        AssertMatrixEntry(control, "destination-after-entry", fixture.C.SiloAddress, Argument);
        Assert.Single(control.Admissions);

        // A loss after server forwarding/control delivery cannot replay the invocation.
        await fixture.CrashSiloAsync(fixture.A).WaitAsync(Timeout, TestCancellation);
        if (partition)
        {
            await CutSiloTransportAsync(fixture.C, fixture.B.SiloAddress);
        }
        else
        {
            await fixture.CrashSiloAsync(fixture.C).WaitAsync(Timeout, TestCancellation);
        }

        fixture.Caller.BreakOutstandingMessagesToSilo(fixture.A.SiloAddress);
        fixture.Caller.BreakOutstandingMessagesToSilo(fixture.C.SiloAddress);
        Assert.False(call.Invocation.IsCompleted);
        Assert.Equal(1, fixture.RunningTargetRequests);
        fixture.ExpireOriginalDeadline();
        await AssertOriginalTimeoutAsync(call.Invocation);
        Assert.Equal(0, fixture.RunningTargetRequests);
        Assert.Single(control.Executions);
        Assert.Single(control.Admissions);
        AssertNoCallerReplay(control, "destination-after-entry", call.Original.Id, maxForwardCount: 1);
        Assert.Single(control.RouteUpdates);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureMatrix_DestinationLostBeforeReceipt_ZeroEntryAndNoReplay(
        bool partition)
    {
        var options = new FailureMatrixOptions();
        await using var fixture = await Fixture.CreateAsync(
            holdCancellationDelivery: true, failureMatrix: options);
        var control = fixture.Control;
        options.HoldEmptyLookup = true;
        var activation = fixture.OriginalActivation;
        activation.Deactivate(new(DeactivationReasonCode.ShuttingDown, "Retire before unavailable destination."), TestCancellation);
        await control.DeactivationEntered.Task.WaitAsync(Timeout, TestCancellation);
        var invocation = fixture.InvokeMatrixAsync(Argument, "destination-before-receipt", TestCancellation);
        var original = await MatrixSentAsync(control, "destination-before-receipt");
        await MatrixReceivedAsync(control, fixture.A.SiloAddress, "destination-before-receipt");
        control.ReleaseDeactivation.TrySetResult();
        await control.UnregisterEntered.Task.WaitAsync(Timeout, TestCancellation);
        control.ReleaseUnregister.TrySetResult();
        await control.MatrixEmptyLookupEntered.Task.WaitAsync(Timeout, TestCancellation);
        await fixture.CatalogRetirement.WaitAsync(Timeout, TestCancellation);
        Assert.False(control.ReplacementActivationEntered.Task.IsCompleted);
        if (partition)
        {
            await CutSiloTransportAsync(fixture.C, fixture.B.SiloAddress);
        }
        else
        {
            await fixture.CrashSiloAsync(fixture.C).WaitAsync(Timeout, TestCancellation);
        }

        var source = fixture.A;
        var producerDrain = source.ServiceProvider.GetRequiredService<MessageCenter>().DrainRetirementAsync(TestCancellation);
        Assert.False(producerDrain.IsCompleted);
        control.MatrixReleaseLookup.TrySetResult();
        try
        {
            // Do not expire the caller while the very first placement is still held: that
            // would test cancellation of queued work again instead of destination loss.
            await producerDrain.WaitAsync(Timeout, TestCancellation);
        }
        catch (ConnectionClosedException error)
        {
            // A's receiver-owned forwarding can have a failed write. Drain reports
            // uncertainty, never permission for B to replay the application invocation.
            Assert.Contains("unsuccessful", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        fixture.Caller.BreakOutstandingMessagesToSilo(fixture.C.SiloAddress);
        fixture.ExpireOriginalDeadline();
        await AssertEstablishedFailureAsync(invocation);
        Assert.Equal(0, fixture.RunningTargetRequests);
        Assert.False(control.ReplacementActivationEntered.Task.IsCompleted);
        Assert.Empty(control.Executions);
        Assert.Empty(control.Admissions);
        Assert.DoesNotContain(control.MatrixRequests, r =>
            r.Observer.Equals(fixture.C.SiloAddress) && r.Request.Id == original.Id);
        AssertNoCallerReplay(control, "destination-before-receipt", original.Id, maxForwardCount: 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureMatrix_OneWayRetirement_RemainsCallbackFreeAndHasNoRouteStatus(bool expireHostBudget)
    {
        var options = new FailureMatrixOptions { HeldArgument = Argument, HoldOnC = true };
        await using var fixture = await Fixture.CreateAsync(
            holdCancellationDelivery: true, failureMatrix: options);
        var control = fixture.Control;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(TestCancellation);
        fixture.StartRetirementWithBudget(budget.Token);
        var activation = await control.DeactivationEntered.Task.WaitAsync(Timeout, TestCancellation);
        await fixture.InvokeMatrixOneWayAsync(Argument, "one-way").WaitAsync(Timeout, TestCancellation);
        var original = await MatrixSentAsync(control, "one-way");
        var onA = await MatrixReceivedAsync(control, fixture.A.SiloAddress, "one-way");
        Assert.NotEqual(default, original.Id);
        Assert.Equal(original.Id, onA.Id);
        Assert.Equal(Message.Directions.OneWay, original.Direction);
        Assert.Equal(0, fixture.RunningTargetRequests);
        Assert.Equal(1, activation.WaitingCount);
        Assert.Empty(control.Executions);
        if (expireHostBudget)
        {
            budget.Cancel();
            await AwaitHostStoppedAsync(fixture.Retirement!, fixture.A, budget.Token);
            await fixture.AssertCatalogRetirementFailedAsync();
            Assert.False(control.ReplacementActivationEntered.Task.IsCompleted);
            Assert.Empty(control.Executions);
            Assert.Empty(control.Admissions);
        }
        else
        {
            control.ReleaseDeactivation.TrySetResult();
            await control.UnregisterEntered.Task.WaitAsync(Timeout, TestCancellation);
            control.ReleaseUnregister.TrySetResult();
            await control.ReplacementActivationEntered.Task.WaitAsync(Timeout, TestCancellation);
            var onC = await MatrixReceivedAsync(control, fixture.C.SiloAddress, "one-way");
            Assert.Equal(original.Id, onC.Id);
            Assert.Equal(Message.Directions.OneWay, onC.Direction);
            Assert.Equal(1, onC.ForwardCount);
            await fixture.CatalogRetirement.WaitAsync(Timeout, TestCancellation);
            await fixture.Retirement!.WaitAsync(Timeout, TestCancellation);
            fixture.BreakOutstandingToA();
            Assert.Equal(0, fixture.RunningTargetRequests);
            control.ReleaseReplacementActivation.TrySetResult();
            var entered = await control.MatrixExecutionEntered.Task.WaitAsync(Timeout, TestCancellation);
            Assert.Equal(fixture.C.SiloAddress, entered.Silo);
            AssertMatrixEntry(control, "one-way", fixture.C.SiloAddress, Argument);
            Assert.Single(control.Executions);
            Assert.Single(control.Admissions);
        }

        Assert.Empty(control.RouteUpdates);
        Assert.Equal(0, fixture.RunningTargetRequests);
    }

    [Fact]
    public async Task FailureMatrix_StatelessWorkerRetirement_UsesItsDirectoryFreeAdmissionContract()
    {
        var options = new FailureMatrixOptions { Stateless = true, HeldArgument = RunningArgument };
        await using var fixture = await Fixture.CreateAsync(
            holdCancellationDelivery: true, failureMatrix: options);
        var control = fixture.Control;
        var worker = await control.MatrixOriginalActivationEntered.Task.WaitAsync(Timeout, TestCancellation);
        Assert.False(worker.IsUsingGrainDirectory);
        var parent = Assert.IsType<StatelessWorkerGrainContext>(
            fixture.A.ServiceProvider.GetRequiredService<ActivationDirectory>().FindTarget(control.GrainId));
        var runningTask = fixture.InvokeMatrixAsync(RunningArgument, "stateless-running", TestCancellation);
        var running = await MatrixSentAsync(control, "stateless-running");
        await control.MatrixExecutionEntered.Task.WaitAsync(Timeout, TestCancellation);
        var invocation = fixture.InvokeMatrixAsync(Argument, "stateless", TestCancellation);
        var original = await MatrixSentAsync(control, "stateless");
        var receivedA = await MatrixReceivedAsync(control, fixture.A.SiloAddress, "stateless");
        Assert.Equal(original.Id, receivedA.Id);
        Assert.NotEqual(running.Id, original.Id);
        Assert.Equal(1, worker.WaitingCount);
        Assert.Single(control.Executions);
        Assert.Single(control.Admissions);
        Assert.Null(control.Directory.Registration);
        Assert.False(control.UnregisterEntered.Task.IsCompleted);
        using var events = new DiagnosticEventCollector(GrainLifecycleEvents.ListenerName);
        var deactivating = events.WaitForEventAsync(
            nameof(GrainLifecycleEvents.Deactivating),
            e => e.Payload is GrainLifecycleEvents.Deactivating d && ReferenceEquals(d.GrainContext, worker),
            Timeout, TestCancellation);
        fixture.StartRetirement();
        await deactivating;
        await AssertDeactivationStartedAsync(worker);
        control.MatrixReleaseExecution.TrySetResult();
        Assert.Equal(RunningArgument + 1, await runningTask.WaitAsync(Timeout, TestCancellation));
        Assert.Equal(worker, await control.DeactivationEntered.Task.WaitAsync(Timeout, TestCancellation));
        control.ReleaseDeactivation.TrySetResult();
        // No directory lease exists to retire. Closed worker/parent admission and exclusive
        // waiting ownership provide the equivalent disposition guarantee for this strategy.
        var destination = await control.ReplacementActivationEntered.Task.WaitAsync(Timeout, TestCancellation);
        var receivedC = await MatrixReceivedAsync(control, fixture.C.SiloAddress, "stateless");
        Assert.Equal(original.Id, receivedC.Id);
        Assert.Equal(1, receivedC.ForwardCount);
        Assert.False(destination.IsUsingGrainDirectory);
        Assert.Equal(fixture.C.SiloAddress, destination.Address.SiloAddress);
        Assert.Single(control.Executions);

        await fixture.CatalogRetirement.WaitAsync(Timeout, TestCancellation);
        await parent.Deactivated.WaitAsync(Timeout, TestCancellation);
        await parent.DrainRequestsAsync().WaitAsync(Timeout, TestCancellation);
        await fixture.Retirement!.WaitAsync(Timeout, TestCancellation);
        fixture.BreakOutstandingToA();
        Assert.False(invocation.IsCompleted);
        Assert.Equal(1, fixture.RunningTargetRequests);
        control.ReleaseReplacementActivation.TrySetResult();
        Assert.Equal(Result, await invocation.WaitAsync(Timeout, TestCancellation));
        AssertMatrixEntry(control, "stateless-running", fixture.A.SiloAddress, RunningArgument);
        AssertMatrixEntry(control, "stateless", fixture.C.SiloAddress, Argument);
        Assert.Equal(2, control.Executions.Count);
        Assert.Equal(2, control.Admissions.Count);
        Assert.DoesNotContain(control.MatrixRequests, r =>
            r.Observer.Equals(fixture.C.SiloAddress) && r.Request.Id == running.Id);
        var route = Assert.Single(control.RouteUpdates);
        Assert.Equal(original.Id, route.Id);
        Assert.Equal(fixture.C.SiloAddress, route.ForwardedTo);
        Assert.Equal(1, route.Generation);
        Assert.False(control.UnregisterEntered.Task.IsCompleted);
        Assert.Null(control.Directory.Registration);
        Assert.Equal(0, fixture.RunningTargetRequests);
    }

    // A deterministic placement seam for this fixture's stateless grain only: reproduce a
    // stale initial physical route to A, then select C during A's receiver-forwarding
    // turn. All unrelated stateless placement uses the real director.
    private sealed class FailureMatrixStatelessDirector(Control control) : IPlacementDirector
    {
        private readonly StatelessWorkerDirector _ordinary = new();

        public Task<SiloAddress> OnAddActivation(PlacementStrategy strategy, PlacementTarget target, IPlacementContext context)
        {
            if (target.GrainIdentity != control.GrainId)
            {
                return _ordinary.OnAddActivation(strategy, target, context);
            }

            return Task.FromResult(context.LocalSiloStatus.IsTerminating() ? control.C! : control.A!);
        }
    }

    private sealed class FailureMatrixStatelessProperties : IGrainPropertiesProvider
    {
        public void Populate(Type grainClass, GrainType grainType, Dictionary<string, string> properties)
        {
            if (grainClass == typeof(SafeSiloRetirementStatelessGrain))
            {
                properties["remove-idle-workers"] = "false";
            }
        }
    }

    private static async Task<(Task<int> Invocation, ReceivedRequest Original)> BeginMatrixForwardingAsync(
        Fixture fixture, string operation)
    {
        var control = fixture.Control;
        var activation = fixture.OriginalActivation;
        activation.Deactivate(new(DeactivationReasonCode.ShuttingDown, "Failure matrix receiver forwarding."), TestCancellation);
        await control.DeactivationEntered.Task.WaitAsync(Timeout, TestCancellation);
        var invocation = fixture.InvokeMatrixAsync(Argument, operation, TestCancellation);
        var original = await MatrixSentAsync(control, operation);
        var received = await MatrixReceivedAsync(control, fixture.A.SiloAddress, operation);
        Assert.Equal(original.Id, received.Id);
        Assert.NotEqual(default, original.Id);
        control.ReleaseDeactivation.TrySetResult();
        await control.UnregisterEntered.Task.WaitAsync(Timeout, TestCancellation);
        control.ReleaseUnregister.TrySetResult();
        await fixture.CatalogRetirement.WaitAsync(Timeout, TestCancellation);
        return (invocation, original);
    }

    private static async Task CutSiloTransportAsync(InProcessSiloHandle silo, SiloAddress other)
    {
        var manager = silo.ServiceProvider.GetRequiredService<ConnectionManager>();
        await manager.Close(TestCancellation).WaitAsync(Timeout, TestCancellation);
        Assert.True(silo.IsActive); // The process/activation remain alive; this is transport loss.
        Assert.True(manager.Closed.IsCompletedSuccessfully);
        Assert.Equal(0, manager.ConnectionCount);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.GetConnection(other).AsTask().WaitAsync(Timeout, TestCancellation));
    }

    private static void AssertNoCallerReplay(Control control, string operation, CorrelationId id, int maxForwardCount)
    {
        var requests = control.MatrixRequests.Where(r => r.Request.Operation == MatrixOperation(operation)).ToArray();
        Assert.NotEmpty(requests);
        Assert.All(requests, observed =>
        {
            Assert.Equal(id, observed.Request.Id);
            Assert.InRange(observed.Request.ForwardCount, 0, maxForwardCount);
        });
        var sentByCaller = Assert.Single(requests, r => r.Observer.Equals(control.B) && r.Request.ForwardCount == 0);
        Assert.Equal(0, sentByCaller.Request.ForwardCount);
        // If C is known dead, receiver placement may select B itself. B observing that
        // forwarded receipt is not a caller replay; only an additional outbound hop is.
        Assert.DoesNotContain(requests, r => r.Observer.Equals(control.B)
            && r.Request.ForwardCount > 0 && !Equals(r.Request.TargetSilo, control.B));
    }

    private static string MatrixOperation(string operation) => OperationValue + ":" + operation;

    private static Task<ReceivedRequest> MatrixSentAsync(Control control, string operation)
        => control.MatrixSent(operation).Task.WaitAsync(Timeout, TestCancellation);

    private static Task<ReceivedRequest> MatrixReceivedAsync(Control control, SiloAddress silo, string operation)
        => control.MatrixReceived(silo, operation).Task.WaitAsync(Timeout, TestCancellation);

    private static void AssertMatrixEntry(Control control, string operation, SiloAddress silo, int argument)
    {
        var entry = Assert.Single(control.Executions, e => e.Operation == MatrixOperation(operation));
        Assert.Equal(silo, entry.Silo);
        Assert.Equal(argument, entry.Argument);
        Assert.Equal(MatrixOperation(operation), entry.Operation);
    }

    private static async Task AssertDeactivationStartedAsync(ActivationData activation)
    {
        // The diagnostic event is emitted before SetState. Acquire an actual runtime state
        // read under its lock, rather than assuming the event callback races no further work.
        await activation.QueueAction(
            context =>
            {
                Assert.True(context.GetRequestCount() > 0);
                Assert.Equal(ActivationState.Deactivating, context.State);
            }, activation).WaitAsync(Timeout, TestCancellation);
    }

    private static async Task AwaitHostStoppedAsync(Task stop, InProcessSiloHandle silo, CancellationToken budget)
    {
        try
        {
            await stop.WaitAsync(Timeout, TestCancellation);
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            // Generic host may propagate the caller's expired budget after all stop stages.
        }

        Assert.False(silo.IsActive);
        Assert.True(stop.IsCompleted);
    }

    private static async Task AssertOriginalTimeoutAsync(Task<int> invocation)
    {
        var exception = await Assert.ThrowsAsync<TimeoutException>(
            () => invocation.WaitAsync(Timeout, TestCancellation));
        Assert.Contains("Response did not arrive on time", exception.Message);
    }

    private static async Task AssertEstablishedFailureAsync(Task<int> invocation)
    {
        var exception = await Record.ExceptionAsync(() => invocation.WaitAsync(Timeout, TestCancellation));
        Assert.True(
            exception is TimeoutException or OrleansMessageRejectionException or SiloUnavailableException
                or ConnectionFailedException or ConnectionClosedException,
            $"Expected original timeout or explicit runtime rejection, got {exception}");
        if (exception is TimeoutException timeout)
        {
            // Distinguish the invocation's ORIGINAL timeout from this test's guard timeout.
            Assert.Contains("Response did not arrive on time", timeout.Message);
        }
        else
        {
            Assert.False(string.IsNullOrWhiteSpace(exception!.Message));
        }
    }

    internal sealed class FailureMatrixOptions
    {
        internal bool Reentrant { get; init; }
        internal bool Stateless { get; init; }
        internal int? HeldArgument { get; init; }
        internal bool HoldOnC { get; init; }
        internal bool HoldEmptyLookup { get; set; }
        internal bool HoldEveryReplacementActivation { get; init; }
        internal FakeTimeProvider CallerClock { get; } = new(new DateTimeOffset(2026, 10, 9, 0, 0, 0, TimeSpan.Zero));
        internal FakeTimeProvider ActivationClock { get; } = new(new DateTimeOffset(2026, 10, 9, 0, 0, 0, TimeSpan.Zero));
    }

    public sealed partial class Control
    {
        internal FailureMatrixOptions? FailureMatrix;
        internal CancellationToken MatrixHookToken;
        private GrainAddress? _matrixOriginalAddress;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<ReceivedRequest>> _matrixSent = new();
        private readonly ConcurrentDictionary<(SiloAddress, string), TaskCompletionSource<ReceivedRequest>> _matrixReceived = new();
        private readonly ConcurrentDictionary<string, int> _matrixReceiptCount = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<ReceivedRequest>> _matrixSecondReceipt = new();
        internal ConcurrentQueue<MatrixRequest> MatrixRequests { get; } = new();
        internal TaskCompletionSource<ActivationData> MatrixOriginalActivationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<Execution> MatrixExecutionEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource MatrixReleaseExecution { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<GrainId> MatrixEmptyLookupEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource MatrixReleaseLookup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<ActivationData> MatrixReplacementActivationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource MatrixReleaseReplacement { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<CorrelationId> TransportRejectionObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<ReceivedRequest> MatrixSent(string operation) => _matrixSent.GetOrAdd(
            MatrixOperation(operation), _ => new(TaskCreationOptions.RunContinuationsAsynchronously));

        internal TaskCompletionSource<ReceivedRequest> MatrixReceived(SiloAddress silo, string operation) => _matrixReceived.GetOrAdd(
            (silo, MatrixOperation(operation)), _ => new(TaskCreationOptions.RunContinuationsAsynchronously));

        internal TaskCompletionSource<ReceivedRequest> MatrixSecondReceipt(string operation) => _matrixSecondReceipt.GetOrAdd(
            MatrixOperation(operation), _ => new(TaskCreationOptions.RunContinuationsAsynchronously));

        internal void ObserveFailureMatrixRequest(SiloAddress observer, ReceivedRequest request)
        {
            if (FailureMatrix is null || request.Operation is not { } operation) return;
            MatrixRequests.Enqueue(new(observer, request));
            if (observer.Equals(B) && observer.Equals(request.SendingSilo))
            {
                _matrixSent.GetOrAdd(operation, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(request);
            }

            if (observer.Equals(request.TargetSilo))
            {
                _matrixReceived.GetOrAdd((observer, operation), _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(request);
                if (_matrixReceiptCount.AddOrUpdate(operation, 1, (_, previous) => previous + 1) == 2)
                {
                    _matrixSecondReceipt.GetOrAdd(operation, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(request);
                }
            }
        }

        internal bool HoldFailureMatrixExecution(Execution execution) => FailureMatrix is { HeldArgument: { } held } options
            && execution.Argument == held
            && execution.Silo.Equals(options.HoldOnC ? C : A);

        internal async Task OnFailureMatrixActivationAsync(ActivationData activation, CancellationToken cancellationToken)
        {
            if (FailureMatrix is not { } options) return;
            var previous = Interlocked.CompareExchange(ref _matrixOriginalAddress, activation.Address, null);
            if (previous is null)
            {
                MatrixOriginalActivationEntered.TrySetResult(activation);
            }

            if (previous is not null && options.HoldEveryReplacementActivation)
            {
                MatrixReplacementActivationEntered.TrySetResult(activation);
                await MatrixReleaseReplacement.Task.WaitAsync(cancellationToken);
            }
        }

        internal void ReleaseFailureMatrixBarriers()
        {
            MatrixReleaseExecution.TrySetResult();
            MatrixReleaseLookup.TrySetResult();
            MatrixReleaseReplacement.TrySetResult();
        }
    }

    internal sealed record MatrixRequest(SiloAddress Observer, ReceivedRequest Request);
}

[Reentrant]
public sealed class SafeSiloRetirementReentrantGrain(SafeSiloRetirementTests.Control control) : SafeSiloRetirementGrain(control);

[StatelessWorker(1)]
public sealed class SafeSiloRetirementStatelessGrain(SafeSiloRetirementTests.Control control) : SafeSiloRetirementGrain(control);
