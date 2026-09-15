using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orleans;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Invocation;
using TestExtensions;
using Xunit;

namespace UnitTests.Runtime;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
[TestCategory("BVT")]
public class InvokableObjectManagerTests
{
    [Fact]
    public async Task StopAsync_WaitsForRunningNormalInvocation()
    {
        await using var fixture = new ManagerFixture();
        var a = new TestInvokable("normal-result");
        var message = fixture.Request(11, a);
        fixture.Manager.Dispatch(message);
        await fixture.WaitAsync(a.Entered.Task, "11: Entered");

        var stop = fixture.StopAsync();
        fixture.AssertPending(stop, "11: normal body held");

        a.Release.TrySetResult();
        await fixture.WaitAsync(stop, "normal drain");
        fixture.AssertSuccess(message, a, "normal-result");
        fixture.AssertResponseCount(1);
        fixture.AssertOrder(11);
    }

    [Fact]
    public async Task StopAsync_DrainsQueuedInvocationsInFifoOrder()
    {
        await using var fixture = new ManagerFixture();
        var a = new TestInvokable("first");
        var b = new TestInvokable("second");
        var c = new TestInvokable("third");
        var first = fixture.Request(11, a);
        var second = fixture.Request(12, b);
        var third = fixture.Request(13, c);
        fixture.Manager.Dispatch(first);
        await fixture.WaitAsync(a.Entered.Task, "11: Entered");
        fixture.Manager.Dispatch(second);
        fixture.Manager.Dispatch(third);
        Assert.False(b.Entered.Task.IsCompleted);
        Assert.False(c.Entered.Task.IsCompleted);
        Assert.Equal(0, b.InvocationCount);
        Assert.Equal(0, c.InvocationCount);

        var stop = fixture.StopAsync();
        fixture.AssertPending(stop, "11 held, 12/13 queued");
        a.Release.TrySetResult();
        await fixture.WaitAsync(b.Entered.Task, "12: Entered after 11");
        fixture.AssertOrder(11, 12);
        Assert.False(c.Entered.Task.IsCompleted);
        fixture.AssertSuccess(first, a, "first");

        b.Release.TrySetResult();
        await fixture.WaitAsync(c.Entered.Task, "13: Entered after 12");
        fixture.AssertOrder(11, 12, 13);
        fixture.AssertSuccess(second, b, "second");
        fixture.AssertPending(stop, "13: final queued body held");
        c.Release.TrySetResult();
        await fixture.WaitAsync(stop, "FIFO drain");
        fixture.AssertSuccess(third, c, "third");
        fixture.AssertResponseCount(3);
    }

    [Fact]
    public async Task StopAsync_WaitsForIndependentAlwaysInterleaveInvocation()
    {
        await using var fixture = new ManagerFixture();
        var a = new TestInvokable("normal");
        var b = new TestInvokable("independent");
        var normal = fixture.Request(11, a);
        var interleaved = fixture.Request(12, b, alwaysInterleave: true);
        Assert.False(InvokableObjectManager.IsCancellationRequest(interleaved));
        fixture.Manager.Dispatch(normal);
        await fixture.WaitAsync(a.Entered.Task, "11: Entered");
        fixture.Manager.Dispatch(interleaved);
        await fixture.WaitAsync(b.Entered.Task, "12: Entered while 11 held");
        Assert.False(a.Release.Task.IsCompleted);
        fixture.AssertOrder(11, 12);

        var stop = fixture.StopAsync();
        a.Release.TrySetResult();
        await fixture.WaitForResponseAsync(normal);
        fixture.AssertSuccess(normal, a, "normal");
        // Response observation precedes A's admission release; only actual stop joins the drain.
        var checkpoint = fixture.StopAsync();
        fixture.AssertPending(stop, "12: independent interleaved body held");
        fixture.AssertPending(checkpoint, "12: fresh stop with interleaved body held");
        Assert.False(b.Exited.Task.IsCompleted);

        b.Release.TrySetResult();
        await fixture.WaitAsync(Task.WhenAll(stop, checkpoint), "interleaved drain");
        fixture.AssertSuccess(interleaved, b, "independent");
        fixture.AssertResponseCount(2);
    }

    [Fact]
    public async Task StopAsync_WaitsForAlwaysInterleaveWithoutNormalWork()
    {
        await using var fixture = new ManagerFixture();
        var body = new TestInvokable("interleaved-only");
        var message = fixture.Request(11, body, alwaysInterleave: true);
        fixture.Manager.Dispatch(message);
        await fixture.WaitAsync(body.Entered.Task, "11: only interleaved body entered");

        // No normal invocation can mask missing interleaved ownership. If this admission
        // is uncounted, both gates close synchronously and StopAsync is already completed.
        var stop = fixture.StopAsync();
        fixture.AssertPending(stop, "11: only interleaved admission held");
        Assert.False(body.Exited.Task.IsCompleted);
        body.Release.TrySetResult();
        await fixture.WaitAsync(stop, "interleaved-only drain");
        fixture.AssertSuccess(message, body, "interleaved-only");
        fixture.AssertResponseCount(1);
        fixture.AssertOrder(11);
    }

    [Fact]
    public async Task StopAsync_WaitsForAdmittedControlWithoutApplicationWork()
    {
        await using var fixture = new ManagerFixture();
        var control = new CancellationControlInvokable(fixture.Sender, new CorrelationId(11));
        var message = fixture.Request(91, control, alwaysInterleave: true);
        fixture.Manager.Dispatch(message);
        await fixture.WaitAsync(control.CancellationSent.Task, "91: control entered with no application work");

        // The application gate is empty: its asynchronous release cannot mask a missing
        // control drain. This complements cancellation admitted DURING application drain.
        var stop = fixture.StopAsync();
        fixture.AssertPending(stop, "91: only cancellation-control admission held");
        Assert.False(control.Exited.Task.IsCompleted);
        Assert.Equal(1, control.InvocationCount);
        control.Release.TrySetResult();
        await fixture.WaitAsync(stop, "control-only drain");
        fixture.AssertCompletedControl(message, control);
        fixture.AssertResponseCount(1);
        fixture.AssertOrder(91);
    }

    [Fact]
    public async Task Dispatch_RejectsLateNormalRequestDuringDrain()
    {
        await using var fixture = new ManagerFixture();
        var a = new TestInvokable("admitted");
        var late = new TestInvokable("must not run");
        var original = fixture.Request(11, a);
        var rejected = fixture.Request(12, late);
        fixture.Manager.Dispatch(original);
        await fixture.WaitAsync(a.Entered.Task, "11: Entered");

        var stop = fixture.StopAsync();
        fixture.Manager.Dispatch(rejected);
        fixture.AssertUnavailable(rejected);
        Assert.Equal(0, late.InvocationCount);
        Assert.Null(late.TargetHolder);
        fixture.AssertPending(stop, "11 held after rejecting late normal 12");

        a.Release.TrySetResult();
        await fixture.WaitAsync(stop, "drain after late normal rejection");
        fixture.AssertSuccess(original, a, "admitted");
        fixture.AssertUnavailable(rejected);
        fixture.AssertResponseCount(2);
        fixture.AssertOrder(11);
    }

    [Fact]
    public async Task Dispatch_RejectsLateNonControlAlwaysInterleaveRequest()
    {
        await using var fixture = new ManagerFixture();
        var a = new TestInvokable("admitted");
        var late = new TestInvokable("must not run");
        var original = fixture.Request(11, a);
        var rejected = fixture.Request(12, late, alwaysInterleave: true);
        Assert.Equal(typeof(IGrainObserver), ((IInvokable)late).GetInterfaceType());
        Assert.False(InvokableObjectManager.IsCancellationRequest(rejected));
        fixture.Manager.Dispatch(original);
        await fixture.WaitAsync(a.Entered.Task, "11: Entered");

        var stop = fixture.StopAsync();
        fixture.Manager.Dispatch(rejected);
        fixture.AssertUnavailable(rejected);
        Assert.Equal(0, late.InvocationCount);
        Assert.Null(late.TargetHolder);
        fixture.AssertPending(stop, "11 held after rejecting late non-control 12");

        a.Release.TrySetResult();
        await fixture.WaitAsync(stop, "drain after interleaved rejection");
        fixture.AssertSuccess(original, a, "admitted");
        fixture.AssertUnavailable(rejected);
        fixture.AssertResponseCount(2);
        fixture.AssertOrder(11);
    }

    [Fact]
    public async Task Dispatch_RejectsCancellationControlAfterDrain()
    {
        await using var fixture = new ManagerFixture();
        await fixture.WaitAsync(fixture.StopAsync(), "empty manager fully drained");
        var control = new CancellationControlInvokable(fixture.Sender, new CorrelationId(11));
        var message = fixture.Request(91, control, alwaysInterleave: true);
        Assert.True(InvokableObjectManager.IsCancellationRequest(message));

        fixture.Manager.Dispatch(message);

        fixture.AssertUnavailable(message);
        Assert.Equal(0, control.InvocationCount);
        Assert.Null(control.CancellationTarget);
        Assert.False(control.CancellationSent.Task.IsCompleted);
        fixture.AssertResponseCount(1);
        fixture.AssertOrder();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopAsync_AdmitsRunningCancellationAndDrainsControl(bool holdControlAfterCancellation)
    {
        await using var fixture = new ManagerFixture();
        var a = new CancellableTestInvokable();
        var original = fixture.Request(11, a);
        var control = new CancellationControlInvokable(original.SendingGrain, original.Id);
        var cancellation = fixture.Request(91, control, alwaysInterleave: true);
        if (!holdControlAfterCancellation)
        {
            control.Release.TrySetResult();
        }

        fixture.Manager.Dispatch(original);
        await fixture.WaitAsync(a.Entered.Task, "11: cancellable body Entered");
        var stop = fixture.StopAsync();
        fixture.AssertPending(stop, "11 awaits its own cancellation token");
        Assert.True(InvokableObjectManager.IsCancellationRequest(cancellation));
        fixture.Manager.Dispatch(cancellation);

        await fixture.WaitAsync(control.CancellationSent.Task, "91: cancellation of sender/11 sent");
        await fixture.WaitAsync(a.CancellationObserved.Task, "11: request token cancellation observed");
        await fixture.WaitAsync(a.Exited.Task, "11: canceled body Exited");
        await fixture.WaitForResponseAsync(original);
        fixture.AssertException<OperationCanceledException>(original);
        Assert.Equal(1, a.InvocationCount);
        Assert.Equal(1, a.CancelCount);
        Assert.True(a.IsCancellationRequested);
        Assert.False(a.Release.Task.IsCompleted);
        Assert.Same(a.TargetHolder, control.CancellationTarget);
        Assert.Equal(original.SendingGrain, control.Sender);
        Assert.Equal(original.Id, control.MessageId);
        Assert.NotEqual(cancellation.Id, control.MessageId);

        var checkpoint = fixture.StopAsync();
        if (holdControlAfterCancellation)
        {
            // This is a held-control checkpoint, not private application-gate release evidence.
            Assert.Equal(1, control.InvocationCount);
            Assert.False(control.Exited.Task.IsCompleted);
            fixture.AssertPending(checkpoint, "91: control held after 11 canceled and responded");
            fixture.AssertPending(stop, "91: original stop still waits for held control");
            fixture.AssertResponseCount(1);
        }

        control.Release.TrySetResult();
        await fixture.WaitAsync(Task.WhenAll(stop, checkpoint), "application and control drains");
        fixture.AssertException<OperationCanceledException>(original);
        fixture.AssertCompletedControl(cancellation, control);
        fixture.AssertOrder(11, 91);
        fixture.AssertResponseCount(2);
    }

    [Fact]
    public async Task StopAsync_AdmitsCancellationOfQueuedInvocation()
    {
        await using var fixture = new ManagerFixture();
        var a = new TestInvokable("running");
        var b = new CancellableTestInvokable();
        var first = fixture.Request(11, a);
        var queued = fixture.Request(12, b);
        var control = new CancellationControlInvokable(queued.SendingGrain, queued.Id);
        control.Release.TrySetResult();
        var cancellation = fixture.Request(91, control, alwaysInterleave: true);
        fixture.Manager.Dispatch(first);
        await fixture.WaitAsync(a.Entered.Task, "11: Entered");
        fixture.Manager.Dispatch(queued);

        var stop = fixture.StopAsync();
        fixture.Manager.Dispatch(cancellation);
        await fixture.WaitAsync(control.CancellationSent.Task, "91: queued 12 cancellation sent");
        await fixture.WaitForResponseAsync(queued);
        fixture.AssertException<OperationCanceledException>(queued);
        Assert.Equal(0, b.InvocationCount);
        Assert.Equal(0, b.CancelCount);
        Assert.Null(b.TargetHolder);
        Assert.False(a.Release.Task.IsCompleted);
        Assert.Same(a.TargetHolder, control.CancellationTarget);
        fixture.AssertPending(stop, "11 held after actual control dispatch removed queued 12");

        a.Release.TrySetResult();
        await fixture.WaitAsync(stop, "queued-cancellation drain");
        fixture.AssertSuccess(first, a, "running");
        fixture.AssertException<OperationCanceledException>(queued);
        fixture.AssertCompletedControl(cancellation, control);
        Assert.Equal(0, b.InvocationCount);
        Assert.Null(b.TargetHolder);
        fixture.AssertOrder(11, 91);
        fixture.AssertResponseCount(3);
    }

    [Fact]
    public async Task CancelRequestAsync_ReleasesQueuedAdmissionExactlyOnce()
    {
        await using var fixture = new ManagerFixture(registerObserver: false);
        var data = new InvokableObjectManager.LocalObjectData(fixture.Observer, fixture.ObserverId, fixture.Manager);
        var a = new TestInvokable("first");
        var b = new CancellableTestInvokable();
        var c = new TestInvokable("last");
        var first = fixture.Request(11, a);
        var canceled = fixture.Request(12, b);
        var last = fixture.Request(13, c);
        data.ReceiveMessage(first);
        await fixture.WaitAsync(a.Entered.Task, "11: Entered");
        data.ReceiveMessage(canceled);
        data.ReceiveMessage(last);
        var stop = fixture.StopAsync();

        await ((IGrainCallCancellationExtension)data).CancelRequestAsync(
            canceled.SendingGrain, canceled.Id, TestContext.Current.CancellationToken);
        fixture.AssertException<OperationCanceledException>(canceled);
        Assert.Equal(0, b.InvocationCount);
        Assert.Equal(0, b.CancelCount);
        Assert.False(c.Entered.Task.IsCompleted);
        a.Release.TrySetResult();
        await fixture.WaitAsync(c.Entered.Task, "13: Entered after 11 released its queue admission");

        // The sequential pump cannot enter C until A's ProcessMessageAsync has disposed its token.
        // A fresh stop detects double-release of B even if an older async wrapper has not resumed.
        var checkpoint = fixture.StopAsync();
        fixture.AssertPending(checkpoint, "13 held, canceled 12 owns no admission");
        fixture.AssertPending(stop, "13 held after direct queued cancellation");
        fixture.AssertSuccess(first, a, "first");
        fixture.AssertOrder(11, 13);
        c.Release.TrySetResult();
        await fixture.WaitAsync(Task.WhenAll(stop, checkpoint), "exactly-once queued ownership drain");
        fixture.AssertSuccess(last, c, "last");
        fixture.AssertException<OperationCanceledException>(canceled);
        Assert.Equal(0, b.InvocationCount);
        Assert.Null(b.TargetHolder);
        fixture.AssertResponseCount(3);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceiveMessage_PrearrivalCancellationReleasesAdmission(bool alwaysInterleave)
    {
        await using var fixture = new ManagerFixture(registerObserver: false);
        var data = new InvokableObjectManager.LocalObjectData(fixture.Observer, fixture.ObserverId, fixture.Manager);
        var request = new CancellableTestInvokable();
        var message = fixture.Request(11, request, alwaysInterleave);
        await ((IGrainCallCancellationExtension)data).CancelRequestAsync(
            message.SendingGrain, message.Id, TestContext.Current.CancellationToken);

        data.ReceiveMessage(message);

        fixture.AssertException<OperationCanceledException>(message);
        await fixture.WaitAsync(fixture.StopAsync(), $"11: prearrival cancellation drain, interleave={alwaysInterleave}");
        Assert.Equal(0, request.InvocationCount);
        Assert.Equal(0, request.CancelCount);
        Assert.Null(request.TargetHolder);
        fixture.AssertException<OperationCanceledException>(message);
        fixture.AssertResponseCount(1);
        fixture.AssertOrder();
    }

    [Fact]
    public async Task Dispatch_InvocationExceptionReleasesAdmission()
    {
        await using var fixture = new ManagerFixture();
        var failing = new TestInvokable("unused")
        {
            Body = async request =>
            {
                await request.Release.Task;
                throw new InvalidOperationException("observer invocation failed");
            }
        };
        var sentinel = new TestInvokable("sentinel-result");
        var failed = fixture.Request(11, failing);
        var next = fixture.Request(12, sentinel);
        fixture.Manager.Dispatch(failed);
        await fixture.WaitAsync(failing.Entered.Task, "11: failing invocation Entered");
        fixture.Manager.Dispatch(next);
        var stop = fixture.StopAsync();
        failing.Release.TrySetResult();

        await fixture.WaitAsync(sentinel.Entered.Task, "12: sentinel Entered after exception");
        var exception = fixture.AssertException<InvalidOperationException>(failed);
        Assert.Equal("observer invocation failed", exception.Message);
        Assert.Equal(1, failing.InvocationCount);
        fixture.AssertPending(stop, "12: sentinel held after exception");
        sentinel.Release.TrySetResult();
        await fixture.WaitAsync(stop, "exception-path drain");
        fixture.AssertSuccess(next, sentinel, "sentinel-result");
        fixture.AssertOrder(11, 12);
        fixture.AssertResponseCount(2);
    }

    [Fact]
    public async Task Dispatch_InvalidBodyReleasesAdmission()
    {
        await using var fixture = new ManagerFixture();
        var invalid = fixture.Request(11, "not an IInvokable");
        var sentinel = new TestInvokable("after-invalid");
        var next = fixture.Request(12, sentinel);
        fixture.Manager.Dispatch(invalid);
        fixture.Manager.Dispatch(next);
        var stop = fixture.StopAsync();

        await fixture.WaitAsync(sentinel.Entered.Task, "12: sentinel Entered after invalid body");
        var exception = fixture.AssertException<InvalidOperationException>(invalid);
        Assert.Equal("Message body is not an invokable request", exception.Message);
        fixture.AssertPending(stop, "12: sentinel held after invalid body");
        sentinel.Release.TrySetResult();
        await fixture.WaitAsync(stop, "invalid-body drain");
        fixture.AssertSuccess(next, sentinel, "after-invalid");
        fixture.AssertOrder(12);
        fixture.AssertResponseCount(2);
    }

    [Fact]
    public async Task Dispatch_ExpiredRequestReleasesAdmission()
    {
        await using var fixture = new ManagerFixture();
        var expiredBody = new TestInvokable("must not run");
        var expired = fixture.Request(11, expiredBody);
        expired.TimeToLive = TimeSpan.FromMilliseconds(-1);
        Assert.True(expired.IsExpired);
        var sentinel = new TestInvokable("after-expired");
        var next = fixture.Request(12, sentinel);
        fixture.Manager.Dispatch(expired);
        fixture.Manager.Dispatch(next);
        var stop = fixture.StopAsync();

        await fixture.WaitAsync(sentinel.Entered.Task, "12: sentinel Entered after expired 11");
        Assert.Equal(0, expiredBody.InvocationCount);
        Assert.Null(expiredBody.TargetHolder);
        fixture.AssertNoResponse(expired);
        fixture.AssertPending(stop, "12: sentinel held after expired request");
        sentinel.Release.TrySetResult();
        await fixture.WaitAsync(stop, "expired-request drain");
        fixture.AssertSuccess(next, sentinel, "after-expired");
        fixture.AssertNoResponse(expired);
        fixture.AssertOrder(12);
        fixture.AssertResponseCount(1);
    }

    [Fact]
    public async Task ReceiveMessage_NullWeakTargetReleasesAdmission()
    {
        await using var fixture = new ManagerFixture(registerObserver: false);
        var data = new InvokableObjectManager.LocalObjectData(fixture.Observer, fixture.ObserverId, fixture.Manager);
        data.LocalObject.Target = null;
        var body = new TestInvokable("must not run");
        var message = fixture.Request(11, body);

        data.ReceiveMessage(message);
        await fixture.WaitAsync(fixture.StopAsync(), "11: null weak target drain");

        Assert.Equal(0, body.InvocationCount);
        Assert.Null(body.TargetHolder);
        fixture.AssertNoResponse(message);
        fixture.AssertResponseCount(0);
        fixture.AssertOrder();
    }

    [Fact]
    public async Task StopAsync_WaitsForTerminalResponseHandling()
    {
        await using var fixture = new ManagerFixture();
        var responseEntered = DrainTestHelpers.CreateSignal();
        var responseRelease = DrainTestHelpers.CreateSignal();
        var responseReturned = DrainTestHelpers.CreateSignal();
        var body = new TestInvokable("terminal-result");
        var message = fixture.Request(11, body);
        fixture.BeforeResponse = request =>
        {
            if (request.Id != message.Id)
            {
                throw new InvalidOperationException($"Unexpected response id {request.Id}, expected {message.Id}.");
            }

            // SendResponse is synchronous, but this callback runs on the manager's worker,
            // never on the orchestrator. Its actual worker lifetime is joined by manager drain.
            responseEntered.TrySetResult();
            try
            {
                DrainTestHelpers.AwaitPhaseAsync(
                    responseRelease.Task, "11: response sink Release", fixture.DescribeState,
                    TestContext.Current.CancellationToken).GetAwaiter().GetResult();
                responseReturned.TrySetResult();
            }
            catch (Exception exception)
            {
                responseReturned.TrySetException(exception);
                throw;
            }
        };

        try
        {
            body.Release.TrySetResult();
            fixture.Manager.Dispatch(message);
            await fixture.WaitAsync(responseEntered.Task, "11: ResponseEntered on manager worker");
            Assert.True(body.Exited.Task.IsCompleted);
            Assert.Equal(1, body.InvocationCount);
            var stop = fixture.StopAsync();
            fixture.AssertPending(stop, "11: body exited, terminal response sink still held");
            fixture.AssertResponseCount(0);

            responseRelease.TrySetResult();
            await fixture.WaitAsync(responseReturned.Task, "11: response sink returned");
            await fixture.WaitAsync(stop, "terminal response worker and admission drain");
            fixture.AssertSuccess(message, body, "terminal-result");
            fixture.AssertResponseCount(1);
        }
        finally
        {
            responseRelease.TrySetResult();
            // The await-using finally below joins every actual drain before disposing DI.
        }
    }

    private sealed class ManagerFixture : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly string _testName;
        private readonly Dictionary<CorrelationId, TestInvokable> _requests = [];
        private readonly ConcurrentQueue<CorrelationId> _executionOrder = new();
        private readonly ConcurrentQueue<ResponseObservation> _responses = new();
        private readonly ConcurrentDictionary<CorrelationId, TaskCompletionSource> _responseSignals = new();
        private readonly List<Task> _stops = [];

        internal ManagerFixture(bool registerObserver = true, [CallerMemberName] string testName = "")
        {
            _testName = testName;
            var services = new ServiceCollection();
            services.AddSerializer();
            services.AddLogging();
            services.AddMetrics();
            _services = services.BuildServiceProvider();
            var runtime = Substitute.For<IRuntimeClient>();
            runtime.ServiceProvider.Returns(_services);
            runtime.When(client => client.SendResponse(Arg.Any<Message>(), Arg.Any<Response>()))
                .Do(call => RecordResponse(call.Arg<Message>(), call.Arg<Response>()));
            var instruments = new OrleansInstruments(_services.GetRequiredService<IMeterFactory>());
            var trace = new MessagingTrace(
                _services.GetRequiredService<ILoggerFactory>(),
                new MessagingInstruments(instruments),
                new MessagingProcessingInstruments(instruments));
            Manager = new InvokableObjectManager(
                Substitute.For<IGrainContext>(),
                runtime,
                _services.GetRequiredService<DeepCopier>(),
                trace,
                _services.GetRequiredService<DeepCopier<Response>>(),
                new InterfaceToImplementationMappingCache(),
                _services.GetRequiredService<ILogger<InvokableObjectManager>>());
            if (registerObserver)
            {
                Assert.True(Manager.TryRegister(Observer, ObserverId));
            }
        }

        internal DrainObserver Observer { get; } = new();
        internal ObserverGrainId ObserverId { get; } =
            ObserverGrainId.Create(ClientGrainId.Create("observer-drain"), IdSpan.Create("observer"));
        internal GrainId Sender { get; } = ClientGrainId.Create("observer-drain-sender").GrainId;
        internal InvokableObjectManager Manager { get; }
        internal Action<Message>? BeforeResponse { get; set; }

        internal Message Request(long id, object body, bool alwaysInterleave = false)
        {
            var correlationId = new CorrelationId(id);
            Assert.True(_responseSignals.TryAdd(correlationId, DrainTestHelpers.CreateSignal()));
            if (body is TestInvokable request)
            {
                _requests.Add(correlationId, request);
                request.OnEntered = () => _executionOrder.Enqueue(correlationId);
            }

            return new Message
            {
                Id = correlationId,
                SendingGrain = Sender,
                TargetGrain = ObserverId.GrainId,
                Direction = Message.Directions.Request,
                IsAlwaysInterleave = alwaysInterleave,
                BodyObject = body
            };
        }

        internal Task StopAsync()
        {
            // Intentionally invoke directly: application admission closes before this returns.
            var stop = Manager.StopAsync();
            _stops.Add(stop);
            return stop;
        }

        internal Task WaitAsync(Task task, string phase) =>
            DrainTestHelpers.AwaitPhaseAsync(task, phase, DescribeState, TestContext.Current.CancellationToken);

        internal Task WaitForResponseAsync(Message message) =>
            WaitAsync(_responseSignals[message.Id].Task, $"{message.Id}: terminal response recorded");

        internal string DescribeState() =>
            $"{_testName}; observer={ObserverId}; sender={Sender}; "
            + $"requests=[{string.Join(", ", _requests.Select(pair => $"{pair.Key}: calls={pair.Value.InvocationCount}, entered={pair.Value.Entered.Task.Status}, release={pair.Value.Release.Task.Status}, exited={pair.Value.Exited.Task.Status}"))}]; "
            + $"order=[{string.Join(", ", _executionOrder)}]; "
            + $"responses=[{string.Join(", ", _responses.Select(response => $"{response.Id}:{response.Exception?.GetType().Name ?? response.Result?.ToString() ?? "completed"}"))}]; "
            + $"stops=[{string.Join(", ", _stops.Select(stop => stop.Status))}]";

        internal void AssertPending(Task stop, string phase) =>
            Assert.False(stop.IsCompleted, $"Stop unexpectedly completed at {phase}. {DescribeState()}");

        internal void AssertOrder(params long[] ids) =>
            Assert.Equal(ids.Select(id => new CorrelationId(id)).ToArray(), _executionOrder.ToArray());

        internal void AssertResponseCount(int count) => Assert.Equal(count, _responses.Count);
        internal void AssertNoResponse(Message message) => Assert.DoesNotContain(_responses, response => response.Id == message.Id);

        internal void AssertSuccess(Message message, TestInvokable body, string expectedResult)
        {
            var response = SingleResponse(message);
            Assert.Null(response.Exception);
            Assert.Equal(expectedResult, Assert.IsType<string>(response.Result));
            Assert.Equal(1, body.InvocationCount);
            Assert.Same(Observer, body.TargetHolder?.GetTarget());
        }

        internal TException AssertException<TException>(Message message) where TException : Exception
        {
            var response = SingleResponse(message);
            Assert.Null(response.Result);
            return Assert.IsType<TException>(response.Exception);
        }

        internal void AssertUnavailable(Message message)
        {
            var exception = AssertException<SiloUnavailableException>(message);
            Assert.Equal(
                "The local Orleans host is shutting down and can no longer accept observer invocations.",
                exception.Message);
        }

        internal void AssertCompletedControl(Message message, CancellationControlInvokable control)
        {
            var response = SingleResponse(message);
            Assert.Null(response.Exception);
            Assert.Null(response.Result);
            Assert.Equal(1, control.InvocationCount);
            Assert.True(control.CancellationSent.Task.IsCompletedSuccessfully);
            Assert.Same(Observer, control.TargetHolder?.GetTarget());
        }

        private ResponseObservation SingleResponse(Message message)
        {
            var response = Assert.Single(_responses, response => response.Id == message.Id);
            Assert.Equal(message.Id, response.Id);
            Assert.Equal(Sender, response.Sender);
            Assert.Equal(ObserverId.GrainId, response.Target);
            return response;
        }

        private void RecordResponse(Message message, Response response)
        {
            BeforeResponse?.Invoke(message);
            var exception = response.Exception;
            _responses.Enqueue(new(
                message.Id, message.SendingGrain, message.TargetGrain,
                exception is null ? response.GetResult<object>() : null, exception));
            _responseSignals[message.Id].TrySetResult();
        }

        async ValueTask IAsyncDisposable.DisposeAsync()
        {
            // await using supplies a finally in every test. Runner cancellation cannot bypass cleanup.
            foreach (var request in _requests.Values)
            {
                request.ReleaseForCleanup();
            }

            var drain = Task.WhenAll(_stops.Append(Manager.StopAsync()));
            await DrainTestHelpers.AwaitCleanupAsync(
                drain, "cleanup: all actual manager drains (provider retained on timeout)", DescribeState);
            foreach (var request in _requests.Values)
            {
                if (request.InvocationCount != 0)
                {
                    await DrainTestHelpers.AwaitCleanupAsync(
                        request.Exited.Task, "cleanup: entered body Exited", DescribeState);
                }

                ((IDisposable)request).Dispose();
            }

            await _services.DisposeAsync();
            // Retain the marker through drain; no forced collection or GC-timing assertion.
            GC.KeepAlive(Observer);
        }

        private sealed record ResponseObservation(
            CorrelationId Id, GrainId Sender, GrainId Target, object? Result, Exception? Exception);
    }
}
