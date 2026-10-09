using System;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Orleans.Runtime;
using Orleans.Serialization.Invocation;
using Orleans.Serialization;
using Orleans.Runtime.GrainDirectory;
using Xunit;

namespace Tester;

public class CallbackDataTests
{
    [Fact, TestCategory("BVT")]
    public async Task RetirementInvalidationPreservesKnownSameSiloReplacement()
    {
        await using var cache = new LruGrainDirectoryCache(16, TimeSpan.FromMinutes(1), TimeProvider.System);
        var id = GrainId.Create("test", "replacement");
        var silo = SiloAddress.New(IPAddress.Loopback, 30000, 1);
        var old = new GrainAddress { GrainId = id, SiloAddress = silo, ActivationId = ActivationId.NewId() };
        var replacement = new GrainAddress { GrainId = id, SiloAddress = silo, ActivationId = ActivationId.NewId() };
        cache.AddOrUpdate(replacement, 1);
        Assert.False(cache.Remove(old));
        Assert.True(cache.LookUp(id, out var current, out _));
        Assert.Equal(replacement, current);
        Assert.True(cache.Remove(new GrainAddress { GrainId = id, SiloAddress = silo }));
        Assert.False(cache.LookUp(id, out _, out _));
    }

    [Fact, TestCategory("BVT")]
    public void PhysicalTargetDeathPreservesRelocatableInvocation()
    {
        using var services = CreateServiceProvider();
        var completion = new TestResponseCompletionSource();
        var callback = CreateRouteCallback(services, completion);

        callback.OnTargetSiloFail();
        Assert.False(callback.IsCompleted);
        Assert.Null(completion.Response);
        callback.DoCallback(new Message { BodyObject = Response.FromResult(42) });
        Assert.True(callback.IsCompleted);
        Assert.NotNull(completion.Response);
        Assert.Equal(42, completion.Response.GetResult<int>());
    }

    [Theory, TestCategory("BVT")]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void PinnedTargetDeathRemainsTerminal(bool systemMessage, bool localOnly)
    {
        using var services = CreateServiceProvider();
        var completion = new TestResponseCompletionSource();
        var callback = CreateRouteCallback(services, completion);
        callback.Message.IsSystemMessage = systemMessage;
        callback.Message.IsLocalOnly = localOnly;

        callback.OnTargetSiloFail();

        Assert.True(callback.IsCompleted);
        Assert.IsType<SiloUnavailableException>(completion.Response.Exception);
    }

    [Fact, TestCategory("BVT")]
    public void InitialRouteAndDuplicateStatusPreserveOriginalMessageIdentity()
    {
        using var services = CreateServiceProvider();
        var completion = new TestResponseCompletionSource();
        var request = CreateRequest();
        var callback = CreateRouteCallback(services, completion, request: request);
        var original = callback.Message;
        var expiry = original._timeToExpiry.GetRawTimestamp();
        var destination = SiloAddress.New(IPAddress.Loopback, 30002, 1);
        var status = new StatusResponse(false, false, []) { ForwardedTo = destination, ForwardingGeneration = 0 };

        Assert.True(callback.OnStatusUpdate(status));
        Assert.False(callback.OnStatusUpdate(status));

        Assert.Same(request, original);
        Assert.Same(original, callback.Message);
        Assert.False(callback.IsCompleted);
        Assert.Equal(new CorrelationId(123), original.Id);
        Assert.Equal(GrainId.Create("test", "caller"), original.SendingGrain);
        Assert.Equal(GrainId.Create("test", "target"), original.TargetGrain);
        Assert.Equal("original payload", original.BodyObject);
        Assert.Equal(expiry, original._timeToExpiry.GetRawTimestamp());
        Assert.Equal(0, original.ForwardCount);
        Assert.Equal(destination, original.TargetSilo);
        Assert.Null(completion.Response);
        callback.OnHostShutdown();
    }

    [Theory, TestCategory("BVT")]
    [InlineData("timeout")]
    [InlineData("shutdown")]
    [InlineData("response")]
    [InlineData("cancellation")]
    public void TerminalCompletionRejectsSubsequentRouteUpdates(string terminal)
    {
        using var services = CreateServiceProvider();
        using var cancellation = new CancellationTokenSource();
        var completion = new TestResponseCompletionSource();
        var callback = CreateRouteCallback(services, completion);
        callback.SubscribeForCancellation(cancellation.Token);
        var initialTarget = callback.Message.TargetSilo;
        switch (terminal)
        {
            case "timeout": callback.OnTimeout(); break;
            case "shutdown": callback.OnHostShutdown(); break;
            case "response": callback.DoCallback(new Message { BodyObject = Response.FromResult(42) }); break;
            case "cancellation": cancellation.Cancel(); break;
        }

        Assert.True(callback.IsCompleted);
        Assert.False(callback.OnStatusUpdate(new(false, false, [])
        {
            ForwardedTo = SiloAddress.New(IPAddress.Loopback, 30002, 1),
            ForwardingGeneration = 0,
        }));
        Assert.Equal(initialTarget, callback.Message.TargetSilo);
        Assert.Equal(1, completion.CompletionCount);
        if (terminal == "response") Assert.Equal(42, completion.Response.GetResult<int>());
        else Assert.IsType(terminal switch
        {
            "timeout" => typeof(TimeoutException),
            "shutdown" => typeof(SiloUnavailableException),
            _ => typeof(OperationCanceledException),
        }, completion.Response.Exception);
    }

    [Fact, TestCategory("BVT")]
    public void RouteUpdatesDoNotRestartOriginalResponseDeadline()
    {
        using var services = CreateServiceProvider();
        var clock = new FakeTimeProvider();
        var completion = new TestResponseCompletionSource();
        var callback = CreateRouteCallback(services, completion, clock);
        var expiry = callback.Message._timeToExpiry.GetRawTimestamp();
        for (var generation = 0; generation < 3; generation++)
        {
            Assert.True(callback.OnStatusUpdate(new(false, false, [])
            {
                ForwardedTo = SiloAddress.New(IPAddress.Loopback, 30002 + generation, 1),
                ForwardingGeneration = generation,
            }));
            clock.Advance(TimeSpan.FromSeconds(20));
            Assert.False(callback.IsExpired(clock.GetTimestamp()));
            Assert.Equal(expiry, callback.Message._timeToExpiry.GetRawTimestamp());
        }

        clock.Advance(TimeSpan.FromTicks(1));
        Assert.True(callback.IsExpired(clock.GetTimestamp()));
        callback.OnTimeout();
        Assert.IsType<TimeoutException>(completion.Response!.Exception);
        Assert.Equal(1, completion.CompletionCount);
        Assert.Equal(0, callback.Message.ForwardCount);
    }

    [Fact, TestCategory("BVT")]
    public void ForwardingRouteUpdatesAreMonotonicAndAdvisory()
    {
        using var services = CreateServiceProvider();
        var callback = CreateRouteCallback(services, new());
        var message = callback.Message;
        var originalTarget = message.TargetSilo;
        var destination = SiloAddress.New(IPAddress.Loopback, 30002, 1);
        Assert.True(callback.OnStatusUpdate(new(false, false, []) { ForwardedTo = destination, ForwardingGeneration = 2 }));
        Assert.Same(message, callback.Message);
        Assert.Equal(destination, message.TargetSilo);
        Assert.False(callback.OnStatusUpdate(new(false, false, []) { ForwardedTo = originalTarget, ForwardingGeneration = 1 }));
        Assert.Equal(destination, message.TargetSilo);
        Assert.False(callback.OnStatusUpdate(new(false, false, []) { ForwardedTo = originalTarget, ForwardingGeneration = 2 }));
        Assert.Equal(destination, message.TargetSilo);
        Assert.True(callback.OnStatusUpdate(new(false, false, []) { ForwardedTo = originalTarget, ForwardingGeneration = 3 }));
        Assert.Equal(originalTarget, message.TargetSilo);
        Assert.False(callback.IsCompleted);
        Assert.Equal(0, message.ForwardCount);
        callback.OnHostShutdown();
        Assert.False(callback.OnStatusUpdate(new(false, false, []) { ForwardedTo = destination, ForwardingGeneration = 4 }));
        Assert.Equal(originalTarget, message.TargetSilo);
    }

    [Fact, TestCategory("BVT")]
    public void LegacyDiagnosticStatusDoesNotConsumeInitialRouteGeneration()
    {
        using var services = CreateServiceProvider();
        var completion = new TestResponseCompletionSource();
        var callback = CreateRouteCallback(services, completion);
        var originalTarget = callback.Message.TargetSilo;
        Assert.True(callback.OnStatusUpdate(new(false, true, ["held below admission"])));
        Assert.Equal(originalTarget, callback.Message.TargetSilo);
        var destination = SiloAddress.New(IPAddress.Loopback, 30002, 1);
        Assert.True(callback.OnStatusUpdate(new(false, false, []) { ForwardedTo = destination, ForwardingGeneration = 0 }));
        Assert.Equal(destination, callback.Message.TargetSilo);
        callback.OnTimeout();
        Assert.Contains("held below admission", Assert.IsType<TimeoutException>(completion.Response.Exception).Message);
        Assert.Equal(1, completion.CompletionCount);
    }

    [Theory, TestCategory("BVT")]
    [InlineData(false)]
    [InlineData(true)]
    public void ClearedBodyCancellationUsesCachedCapability(bool cancellable)
    {
        using var services = CreateServiceProvider();
        using var cancellation = new CancellationTokenSource();
        using var request = CreateRequest();
        var body = new CancellableTestInvokable { Cancellable = cancellable };
        request.BodyObject = body;
        var manager = new RecordingCancellationManager();
        var shared = CreateSharedCallbackData(_ => { }, TimeProvider.System, TimeSpan.FromMinutes(1));
        shared.CancellationManager = manager;
        var completion = new TestResponseCompletionSource();
        var callback = new CallbackData(shared, completion, request, CreateInstruments(services));
        Assert.Same(body, callback.Message.BodyObject);
        request.ReleaseBodyBuffer();
        callback.SubscribeForCancellation(cancellation.Token);
        cancellation.Cancel();
        Assert.True(callback.IsCompleted);
        var exception = Assert.IsType<OperationCanceledException>(completion.Response.Exception);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(1, completion.CompletionCount);
        Assert.Equal(0, body.Disposals);
        Assert.Null(request.BodyObject);
        if (cancellable)
        {
            var signal = Assert.Single(manager.Signals);
            Assert.Equal(request.TargetSilo, signal.Silo);
            Assert.Equal(request.Id, signal.Id);
            Assert.Equal(request.TargetGrain, signal.Target);
            Assert.Equal(request.SendingGrain, signal.Caller);
        }
        else Assert.Empty(manager.Signals);
    }

    [Fact, TestCategory("BVT")]
    public async Task ConcurrentDuplicateRouteUpdatesAcceptExactlyOneStatus()
    {
        using var services = CreateServiceProvider();
        var accepted = 0;
        var callback = CreateRouteCallback(services, new());
        var target = SiloAddress.New(IPAddress.Loopback, 30002, 1);
        var status = new StatusResponse(false, false, []) { ForwardedTo = target, ForwardingGeneration = 0 };
        using var start = new Barrier(2);
        await Task.WhenAll(Task.Run(Receive, TestContext.Current.CancellationToken), Task.Run(Receive, TestContext.Current.CancellationToken));
        Assert.Equal(1, accepted);
        Assert.Equal(target, callback.Message.TargetSilo);
        Assert.Equal(0, callback.Message.ForwardCount);
        Assert.False(callback.IsCompleted);
        callback.OnHostShutdown();

        void Receive()
        {
            Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            if (callback.OnStatusUpdate(status)) Interlocked.Increment(ref accepted);
        }
    }

    [Fact, TestCategory("BVT")]
    public void CancellationTracksNewestRouteAfterSuccessfulWriteClearsOriginalBody()
    {
        using var services = CreateServiceProvider();
        using var cancellation = new CancellationTokenSource();
        var manager = new RecordingCancellationManager();
        var shared = CreateSharedCallbackData(_ => { }, TimeProvider.System, TimeSpan.FromMinutes(1));
        shared.CancellationManager = manager;
        var message = new Message
        {
            Direction = Message.Directions.Request,
            TargetGrain = GrainId.Create("test", "target"),
            SendingGrain = GrainId.Create("test", "caller"),
            Id = new CorrelationId(123),
            BodyObject = new CancellableTestInvokable(),
        };
        var completion = new TestResponseCompletionSource();
        var callback = new CallbackData(shared, completion, message, CreateInstruments(services));
        Assert.Same(message, callback.Message);
        var body = Assert.IsType<CancellableTestInvokable>(message.BodyObject);
        message.ReleaseBodyBuffer();
        Assert.Null(message.BodyObject);
        Assert.Equal(0, body.Disposals);
        callback.SubscribeForCancellation(cancellation.Token);
        var newest = SiloAddress.New(IPAddress.Loopback, 30003, 1);
        Assert.True(callback.OnStatusUpdate(new(false, false, []) { ForwardedTo = newest, ForwardingGeneration = 3 }));
        Assert.False(callback.OnStatusUpdate(new(false, false, [])
        {
            ForwardedTo = SiloAddress.New(IPAddress.Loopback, 30002, 1),
            ForwardingGeneration = 2,
        }));
        Assert.Equal(newest, callback.Message.TargetSilo);
        cancellation.Cancel();
        var signal = Assert.Single(manager.Signals);
        Assert.Equal(newest, signal.Silo);
        Assert.Equal(message.Id, signal.Id);
        Assert.Equal(message.TargetGrain, signal.Target);
        Assert.Equal(message.SendingGrain, signal.Caller);
        Assert.Equal(0, body.Disposals);
        callback.OnTimeout();
        callback.OnHostShutdown();
        callback.DoCallback(new Message { BodyObject = Response.FromResult(42) });
        Assert.Equal(0, body.Disposals);
        Assert.Equal(1, completion.CompletionCount);
        Assert.IsType<OperationCanceledException>(completion.Response!.Exception);
        message.Dispose();
    }

    [Fact, TestCategory("BVT")]
    public void ClearedBodyRetainsMethodSpecificResponseTimeout()
    {
        using var services = CreateServiceProvider();
        var clock = new FakeTimeProvider();
        var completion = new TestResponseCompletionSource();
        using var message = CreateRequest();
        var body = new CancellableTestInvokable();
        message.BodyObject = body;
        var callback = CreateRouteCallback(services, completion, clock, message);
        message.ReleaseBodyBuffer();
        clock.Advance(TimeSpan.FromSeconds(7));
        Assert.False(callback.IsExpired(clock.GetTimestamp()));
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.True(callback.IsExpired(clock.GetTimestamp()));
        callback.OnTimeout();
        Assert.True(callback.IsCompleted);
        Assert.Contains("00:00:07", Assert.IsType<TimeoutException>(completion.Response.Exception).Message);
        Assert.Null(message.BodyObject);
        Assert.Equal(0, body.Disposals);
        Assert.Equal(1, completion.CompletionCount);
    }

    [Fact, TestCategory("BVT")]
    public void PhysicalTargetDeathWaitsForOriginalDeadlineWithoutReplay()
    {
        using var services = CreateServiceProvider();
        var clock = new FakeTimeProvider();
        var completion = new TestResponseCompletionSource();
        var callback = CreateRouteCallback(services, completion, clock);
        callback.OnTargetSiloFail();
        Assert.False(callback.IsCompleted);
        Assert.Null(completion.Response);
        clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromTicks(1));
        Assert.True(callback.IsExpired(clock.GetTimestamp()));
        callback.OnTimeout();
        Assert.IsType<TimeoutException>(completion.Response!.Exception);
        Assert.Equal(1, completion.CompletionCount);
    }

    [Fact, TestCategory("BVT")]
    public void ApplicationSiloUnavailableExceptionRemainsTerminalWithoutReplay()
    {
        using var services = CreateServiceProvider();
        var completion = new TestResponseCompletionSource();
        var callback = CreateRouteCallback(services, completion);
        var exception = new SiloUnavailableException("Application failure");
        callback.DoCallback(new Message { BodyObject = Response.FromException(exception) });
        Assert.True(callback.IsCompleted);
        Assert.Same(exception, completion.Response.Exception);
        Assert.Equal(1, completion.CompletionCount);
    }

    [Theory, TestCategory("BVT")]
    [InlineData("death-route-response")]
    [InlineData("route-death-response")]
    [InlineData("route-response-death")]
    [InlineData("response-route-death")]
    public void MembershipAndRouteOrderingPreservesOneLogicalCompletion(string ordering)
    {
        using var services = CreateServiceProvider();
        var completion = new TestResponseCompletionSource();
        var callback = CreateRouteCallback(services, completion);
        var originalTarget = callback.Message.TargetSilo;
        var destination = SiloAddress.New(IPAddress.Loopback, 30002, 1);
        var status = new StatusResponse(false, false, []) { ForwardedTo = destination, ForwardingGeneration = 0 };
        foreach (var step in ordering.Split('-'))
        {
            switch (step)
            {
                case "death": callback.OnTargetSiloFail(); break;
                case "route":
                    Assert.Equal(!callback.IsCompleted, callback.OnStatusUpdate(status));
                    break;
                case "response": callback.DoCallback(new Message { BodyObject = Response.FromResult(174) }); break;
            }
        }

        Assert.Equal(1, completion.CompletionCount);
        Assert.Equal(174, completion.Response.GetResult<int>());
        Assert.Equal(ordering.StartsWith("response", StringComparison.Ordinal) ? originalTarget : destination, callback.Message.TargetSilo);
        Assert.Equal(0, callback.Message.ForwardCount);
    }

    [Theory, TestCategory("BVT")]
    [InlineData("timeout")]
    [InlineData("cancellation")]
    [InlineData("shutdown")]
    [InlineData("response")]
    public async Task RouteUpdateRacingTerminalTransitionCannotChangeCompletedRoute(string terminal)
    {
        using var services = CreateServiceProvider();
        using var cancellation = new CancellationTokenSource();
        var completion = new TestResponseCompletionSource();
        var callback = CreateRouteCallback(services, completion);
        callback.SubscribeForCancellation(cancellation.Token);
        var destination = SiloAddress.New(IPAddress.Loopback, 30002, 1);
        var status = new StatusResponse(false, false, []) { ForwardedTo = destination, ForwardingGeneration = 0 };
        var accepted = false;
        using var barrier = new Barrier(2);
        await Task.WhenAll(
            Task.Run(() =>
            {
                Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
                accepted = callback.OnStatusUpdate(status);
            }, TestContext.Current.CancellationToken),
            Task.Run(() =>
            {
                Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
                switch (terminal)
                {
                    case "timeout": callback.OnTimeout(); break;
                    case "cancellation": cancellation.Cancel(); break;
                    case "shutdown": callback.OnHostShutdown(); break;
                    case "response": callback.DoCallback(new Message { BodyObject = Response.FromResult(174) }); break;
                }
            }, TestContext.Current.CancellationToken));

        Assert.True(callback.IsCompleted);
        Assert.Equal(1, completion.CompletionCount);
        Assert.Equal(accepted ? destination : SiloAddress.New(IPAddress.Loopback, 30000, 1), callback.Message.TargetSilo);
        var terminalRoute = callback.Message.TargetSilo;
        Assert.False(callback.OnStatusUpdate(new(false, false, [])
        {
            ForwardedTo = SiloAddress.New(IPAddress.Loopback, 30003, 1),
            ForwardingGeneration = 1,
        }));
        Assert.Equal(terminalRoute, callback.Message.TargetSilo);
        if (terminal == "response") Assert.Equal(174, completion.Response.GetResult<int>());
    }

    [Fact, TestCategory("BVT")]
    public void CancellationAwaitingAcknowledgementTracksAcceptedRouteAfterBodyRelease()
    {
        using var services = CreateServiceProvider();
        using var cancellation = new CancellationTokenSource();
        var completion = new TestResponseCompletionSource();
        var manager = new RecordingCancellationManager();
        var shared = new SharedCallbackData(_ => { }, NullLogger<CallbackData>.Instance,
            TimeProvider.System, TimeSpan.FromMinutes(1), false, true, manager);
        using var request = CreateRequest();
        request.BodyObject = new CancellableTestInvokable();
        var callback = new CallbackData(shared, completion, request, CreateInstruments(services));
        request.ReleaseBodyBuffer();
        callback.SubscribeForCancellation(cancellation.Token);
        cancellation.Cancel();
        Assert.False(callback.IsCompleted);
        Assert.Null(completion.Response);
        var initialSignal = Assert.Single(manager.Signals);
        Assert.Equal(request.TargetSilo, initialSignal.Silo);
        var next = SiloAddress.New(IPAddress.Loopback, 30002, 1);
        Assert.True(callback.OnStatusUpdate(new(false, false, []) { ForwardedTo = next, ForwardingGeneration = 0 }));
        Assert.False(callback.OnStatusUpdate(new(false, false, []) { ForwardedTo = next, ForwardingGeneration = 0 }));
        Assert.Equal(2, manager.Signals.Count);
        Assert.Equal(next, manager.Signals[1].Silo);
        Assert.Equal(request.Id, manager.Signals[1].Id);
        Assert.Equal(request.TargetGrain, manager.Signals[1].Target);
        Assert.Equal(request.SendingGrain, manager.Signals[1].Caller);
        callback.DoCallback(new Message { BodyObject = Response.FromException(new OperationCanceledException()) });
        Assert.IsType<OperationCanceledException>(completion.Response!.Exception);
        Assert.Equal(1, completion.CompletionCount);
    }

    [Fact, TestCategory("BVT")]
    public void MultiHopRouteNoticesDiscardStaleOwnershipAndCompletedUpdates()
    {
        using var services = CreateServiceProvider();
        var completion = new TestResponseCompletionSource();
        var callback = CreateRouteCallback(services, completion);
        var c = SiloAddress.New(IPAddress.Loopback, 30002, 1);
        var d = SiloAddress.New(IPAddress.Loopback, 30003, 1);
        Assert.True(callback.OnStatusUpdate(new(false, false, []) { ForwardedTo = d, ForwardingGeneration = 2 }));
        Assert.False(callback.OnStatusUpdate(new(false, false, []) { ForwardedTo = c, ForwardingGeneration = 1 }));
        Assert.Equal(d, callback.Message.TargetSilo);
        Assert.False(callback.IsCompleted);
        callback.OnTargetSiloFail();
        Assert.False(callback.IsCompleted);
        callback.DoCallback(new Message { BodyObject = Response.FromResult(174) });
        Assert.False(callback.OnStatusUpdate(new(false, false, []) { ForwardedTo = c, ForwardingGeneration = 3 }));
        Assert.Equal(d, callback.Message.TargetSilo);
        Assert.Equal(174, completion.Response.GetResult<int>());
        Assert.Equal(1, completion.CompletionCount);
    }

    [GenerateSerializer]
    public sealed class CancellableTestInvokable : IInvokable
    {
        [Id(0)] public int Disposals { get; private set; }
        [Id(1)] public bool Cancellable { get; init; } = true;
        public bool IsCancellable => Cancellable;
        public TimeSpan? GetDefaultResponseTimeout() => TimeSpan.FromSeconds(7);
        public void Dispose() => Disposals++;
        public object? GetTarget() => null;
        public void SetTarget(ITargetHolder holder) { }
        public ValueTask<Response> Invoke() => throw new InvalidOperationException("This request is a callback fixture.");
        public int GetArgumentCount() => 0;
        public object? GetArgument(int index) => throw new ArgumentOutOfRangeException(nameof(index));
        public void SetArgument(int index, object value) => throw new ArgumentOutOfRangeException(nameof(index));
        public string GetMethodName() => nameof(Invoke);
        public string GetInterfaceName() => nameof(IInvokable);
        public string GetActivityName() => nameof(CancellableTestInvokable);
        public MethodInfo GetMethod() => typeof(CancellableTestInvokable).GetMethod(nameof(Invoke))!;
        public Type GetInterfaceType() => typeof(IInvokable);
    }

    private sealed class RecordingCancellationManager : IGrainCallCancellationManager
    {
        public List<(SiloAddress? Silo, GrainId Target, GrainId Caller, CorrelationId Id)> Signals { get; } = [];
        public void SignalCancellation(SiloAddress? targetSilo, GrainId targetGrainId, GrainId sendingGrainId, CorrelationId messageId)
            => Signals.Add((targetSilo, targetGrainId, sendingGrainId, messageId));
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void AlreadyCanceledTokenCompletesCallback()
    {
        using var serviceProvider = CreateServiceProvider();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var completion = new TestResponseCompletionSource();
        var unregisterCount = 0;
        var callback = CreateCallback(
            completion,
            _ => Interlocked.Increment(ref unregisterCount),
            CreateInstruments(serviceProvider));

        callback.SubscribeForCancellation(cancellation.Token);

        Assert.True(callback.IsCompleted);
        Assert.Equal(1, unregisterCount);
        var exception = Assert.IsType<OperationCanceledException>(completion.Response.Exception);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void CancellationSubscriptionAfterCompletionDoesNotRetainCallback()
    {
        using var serviceProvider = CreateServiceProvider();
        using var cancellation = new CancellationTokenSource();

        var callbackReference = CreateCompletedCallback(cancellation.Token, CreateInstruments(serviceProvider));

        for (var attempt = 0; attempt < 10 && callbackReference.IsAlive; attempt++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        Assert.False(callbackReference.IsAlive);
        GC.KeepAlive(cancellation);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void ExpirationUsesConfiguredTimeProvider()
    {
        using var serviceProvider = CreateServiceProvider();
        var timeProvider = new FakeTimeProvider();
        var timeout = TimeSpan.FromSeconds(1);
        var callback = CreateCallback(
            new TestResponseCompletionSource(),
            _ => { },
            CreateInstruments(serviceProvider),
            timeProvider,
            timeout);

        Assert.False(callback.IsExpired(timeProvider.GetTimestamp()));

        timeProvider.Advance(timeout);
        Assert.False(callback.IsExpired(timeProvider.GetTimestamp()));

        timeProvider.Advance(TimeSpan.FromTicks(1));
        Assert.True(callback.IsExpired(timeProvider.GetTimestamp()));
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void TimestampConversionPreservesTimeSpanPrecision()
    {
        using var serviceProvider = CreateServiceProvider();
        var timeProvider = new FakeTimeProvider();
        var timeout = TimeSpan.FromTicks((1L << 53) + 1);
        var callback = CreateCallback(
            new TestResponseCompletionSource(),
            _ => { },
            CreateInstruments(serviceProvider),
            timeProvider,
            timeout);

        Assert.False(callback.IsExpired(timeProvider.GetTimestamp()));

        timeProvider.Advance(timeout);
        Assert.False(callback.IsExpired(timeProvider.GetTimestamp()));

        timeProvider.Advance(TimeSpan.FromTicks(1));
        Assert.True(callback.IsExpired(timeProvider.GetTimestamp()));
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void TimestampConversionClampsToLongRange()
    {
        using var serviceProvider = CreateServiceProvider();
        var shared = CreateSharedCallbackData(
            _ => { },
            new HighFrequencyTimeProvider(),
            TimeSpan.Zero);

        Assert.Equal(long.MaxValue, shared.GetTimestampTicks(TimeSpan.MaxValue));
        Assert.Equal(long.MinValue, shared.GetTimestampTicks(TimeSpan.MinValue));
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void DisabledLatencyDiagnosticsDoNotReadCompletionTimestamp()
    {
        using var serviceProvider = CreateServiceProvider();
        var timeProvider = new CountingTimeProvider();
        var callback = CreateCallback(
            new TestResponseCompletionSource(),
            _ => { },
            CreateInstruments(serviceProvider),
            timeProvider);

        Assert.Equal(1, timeProvider.GetTimestampCallCount);

        callback.OnHostShutdown();

        Assert.Equal(1, timeProvider.GetTimestampCallCount);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void LatencyDiagnosticsPreserveFractionalMilliseconds()
    {
        using var serviceProvider = CreateServiceProvider();
        var meterFactory = serviceProvider.GetRequiredService<IMeterFactory>();
        using var collector = new MetricCollector<double>(meterFactory, "Microsoft.Orleans", InstrumentNames.APP_REQUESTS_LATENCY_HISTOGRAM);
        var timeProvider = new FakeTimeProvider();
        var callback = CreateCallback(
            new TestResponseCompletionSource(),
            _ => { },
            CreateInstruments(serviceProvider),
            timeProvider);

        timeProvider.Advance(TimeSpan.FromMicroseconds(125));
        callback.OnHostShutdown();

        Assert.Equal(0.125, Assert.Single(collector.GetMeasurementSnapshot()).Value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateCompletedCallback(CancellationToken cancellationToken, ApplicationRequestInstruments instruments)
    {
        var callback = CreateCallback(new TestResponseCompletionSource(), _ => { }, instruments);

        callback.OnHostShutdown();
        callback.SubscribeForCancellation(cancellationToken);

        return new WeakReference(callback);
    }

    private static CallbackData CreateCallback(
        IResponseCompletionSource completion,
        Action<Message> unregister,
        ApplicationRequestInstruments instruments,
        TimeProvider? timeProvider = null,
        TimeSpan? responseTimeout = null)
    {
        var shared = CreateSharedCallbackData(
            unregister,
            timeProvider ?? TimeProvider.System,
            responseTimeout ?? TimeSpan.FromMinutes(1));
        return new CallbackData(shared, completion, new Message(), instruments);
    }

    private static SharedCallbackData CreateSharedCallbackData(
        Action<Message> unregister,
        TimeProvider timeProvider,
        TimeSpan responseTimeout)
        => new(
            unregister,
            logger: NullLogger<CallbackData>.Instance,
            timeProvider,
            responseTimeout,
            cancelOnTimeout: false,
            waitForCancellationAcknowledgement: false,
            cancellationManager: null);

    private static ServiceProvider CreateServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddSerializer();
        return services.BuildServiceProvider();
    }

    private static CallbackData CreateRouteCallback(
        IServiceProvider services,
        TestResponseCompletionSource completion,
        TimeProvider? clock = null,
        Message? request = null)
    {
        var shared = CreateSharedCallbackData(_ => { }, clock ?? TimeProvider.System, TimeSpan.FromMinutes(1));
        return new CallbackData(shared, completion, request ?? CreateRequest(), CreateInstruments(services));
    }

    private static Message CreateRequest() => new()
        {
            Direction = Message.Directions.Request,
            TargetGrain = GrainId.Create("test", "target"),
            TargetSilo = SiloAddress.New(IPAddress.Loopback, 30000, 1),
            SendingGrain = GrainId.Create("test", "caller"),
            Id = new CorrelationId(123),
            BodyObject = "original payload",
            TimeToLive = TimeSpan.FromMinutes(1),
        };

    private static ApplicationRequestInstruments CreateInstruments(IServiceProvider serviceProvider) =>
        new(new OrleansInstruments(serviceProvider.GetRequiredService<IMeterFactory>()));

    private sealed class TestResponseCompletionSource : IResponseCompletionSource
    {
        public Response Response { get; private set; } = null!;
        public int CompletionCount { get; private set; }

        public void Complete(Response value)
        {
            Response = value;
            CompletionCount++;
        }

        public void Complete() => Response = Orleans.Serialization.Invocation.Response.Completed;
    }

    private sealed class HighFrequencyTimeProvider : TimeProvider
    {
        public override long TimestampFrequency => long.MaxValue;
    }

    private sealed class CountingTimeProvider : TimeProvider
    {
        public int GetTimestampCallCount { get; private set; }

        public override long GetTimestamp() => ++GetTimestampCallCount;
    }
}
