using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Orleans.Runtime;
using Orleans.Serialization.Invocation;
using Xunit;

namespace Tester;

public class CallbackDataTests
{
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

    [TestSuite("BVT"), TestProvider("None"), Fact, TestCategory("BVT")]
    public void TargetSiloFailureCompletesCallWhichWasNotRetargeted()
    {
        using var services = CreateServiceProvider();
        var completion = new TestResponseCompletionSource();
        var callback = CreateRequestCallback(completion, CreateInstruments(services), out _);
        callback.OnTargetSiloFail(callback.Message.TargetSilo!);
        Assert.True(callback.IsCompleted);
        Assert.IsType<SiloUnavailableException>(completion.Response.Exception);
    }

    [TestSuite("BVT"), TestProvider("None"), Fact, TestCategory("BVT")]
    public void RetargetMovesCallAwayFromDepartingSilo()
    {
        using var services = CreateServiceProvider();
        var completion = new TestResponseCompletionSource();
        var callback = CreateRequestCallback(completion, CreateInstruments(services), out _);
        var original = callback.Message.TargetSilo;
        var destination = SiloAddress.New(IPAddress.Loopback, 30002, 1);
        Assert.True(callback.TryRetarget(destination, 1, NotDead));
        Assert.Equal(destination, callback.Message.TargetSilo);
        Assert.Equal(1, callback.Message.ForwardCount);
        Assert.NotEqual(original, callback.Message.TargetSilo);
        Assert.False(callback.IsCompleted);
        Assert.Null(completion.Response);
    }

    [TestSuite("BVT"), TestProvider("None"), Fact, TestCategory("BVT")]
    public void RetargetDoesNotRestartTheCallersTimeout()
    {
        using var services = CreateServiceProvider();
        var time = new FakeTimeProvider();
        var completion = new TestResponseCompletionSource();
        var callback = CreateRequestCallback(completion, CreateInstruments(services), out _, timeProvider: time);
        time.Advance(CancellableInvokable.ResponseTimeout - TimeSpan.FromTicks(1));
        Assert.False(callback.IsExpired(time.GetTimestamp()));
        Assert.True(callback.TryRetarget(SiloAddress.New(IPAddress.Loopback, 30002, 1), 1, NotDead));
        time.Advance(TimeSpan.FromTicks(2));
        Assert.True(callback.IsExpired(time.GetTimestamp()));
        callback.OnTimeout();
        Assert.True(callback.IsCompleted);
        Assert.IsType<TimeoutException>(completion.Response.Exception);
    }

    [TestSuite("BVT"), TestProvider("None"), Fact, TestCategory("BVT")]
    public void RetargetsAreOrderedByForwardCount()
    {
        using var services = CreateServiceProvider();
        var callback = CreateRequestCallback(new TestResponseCompletionSource(), CreateInstruments(services), out _);
        var first = SiloAddress.New(IPAddress.Loopback, 30002, 1);
        var second = SiloAddress.New(IPAddress.Loopback, 30003, 1);
        Assert.True(callback.TryRetarget(second, 2, NotDead));
        Assert.False(callback.TryRetarget(first, 1, NotDead));
        Assert.False(callback.TryRetarget(first, 2, NotDead));
        Assert.False(callback.TryRetarget(first, 0, NotDead));
        Assert.Equal(second, callback.Message.TargetSilo);
        Assert.Equal(2, callback.Message.ForwardCount);
    }

    [TestSuite("BVT"), TestProvider("None"), Fact, TestCategory("BVT")]
    public void TargetSiloFailureDoesNotFailCallRetargetedAwayFromIt()
    {
        using var services = CreateServiceProvider();
        var completion = new TestResponseCompletionSource();
        var callback = CreateRequestCallback(completion, CreateInstruments(services), out _);
        var failed = callback.Message.TargetSilo!;
        var destination = SiloAddress.New(IPAddress.Loopback, 30002, 1);
        Assert.True(callback.TryRetarget(destination, 1, NotDead));
        callback.OnTargetSiloFail(failed);
        Assert.False(callback.IsCompleted);
        Assert.Null(completion.Response);
        Assert.Equal(destination, callback.Message.TargetSilo);
    }

    [TestSuite("BVT"), TestProvider("None"), Fact, TestCategory("BVT")]
    public void RetargetToDeadSiloDoesNotFailCallRetargetedAgain()
    {
        using var services = CreateServiceProvider();
        var completion = new TestResponseCompletionSource();
        var callback = CreateRequestCallback(completion, CreateInstruments(services), out _);
        var dead = SiloAddress.New(IPAddress.Loopback, 30002, 1);
        var live = SiloAddress.New(IPAddress.Loopback, 30003, 1);
        Assert.True(callback.TryRetarget(dead, 1, silo =>
        {
            Assert.True(callback.TryRetarget(live, 2, NotDead));
            return silo.Equals(dead);
        }));
        Assert.False(callback.IsCompleted);
        Assert.Null(completion.Response);
        Assert.Equal(live, callback.Message.TargetSilo);
        Assert.Equal(2, callback.Message.ForwardCount);
    }

    [TestSuite("BVT"), TestProvider("None"), Fact, TestCategory("BVT")]
    public void RetargetToDeadSiloFailsCall()
    {
        using var services = CreateServiceProvider();
        var completion = new TestResponseCompletionSource();
        var callback = CreateRequestCallback(completion, CreateInstruments(services), out _);
        var destination = SiloAddress.New(IPAddress.Loopback, 30002, 1);
        Assert.True(callback.TryRetarget(destination, 1, silo => silo.Equals(destination)));
        Assert.True(callback.IsCompleted);
        Assert.IsType<SiloUnavailableException>(completion.Response.Exception);
    }

    [TestSuite("BVT"), TestProvider("None"), Fact, TestCategory("BVT")]
    public void RetargetRacingTimeoutSignalsCancellationToNewSilo()
    {
        using var services = CreateServiceProvider();
        var completion = new TestResponseCompletionSource();
        var callback = CreateRequestCallback(completion, CreateInstruments(services), out var cancellation, cancelOnTimeout: true);
        var original = callback.Message.TargetSilo;
        var destination = SiloAddress.New(IPAddress.Loopback, 30002, 1);
        callback.OnTimeout();
        Assert.Equal([original], cancellation.Targets);
        Assert.True(callback.TryRetarget(destination, 1, NotDead));
        Assert.Equal([original, destination], cancellation.Targets);
        Assert.IsType<TimeoutException>(completion.Response.Exception);
    }

    [TestSuite("BVT"), TestProvider("None"), Theory, TestCategory("BVT")]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationIsSignaledToRetargetedSilo(bool cancelBeforeRetarget)
    {
        using var services = CreateServiceProvider();
        using var token = new CancellationTokenSource();
        var callback = CreateRequestCallback(new TestResponseCompletionSource(), CreateInstruments(services), out var cancellation,
            waitForCancellationAcknowledgement: true);
        var original = callback.Message.TargetSilo;
        var destination = SiloAddress.New(IPAddress.Loopback, 30002, 1);
        callback.SubscribeForCancellation(token.Token);
        if (cancelBeforeRetarget)
        {
            token.Cancel();
            Assert.True(callback.TryRetarget(destination, 1, NotDead));
            Assert.Equal([original, destination], cancellation.Targets);
        }
        else
        {
            Assert.True(callback.TryRetarget(destination, 1, NotDead));
            Assert.Empty(cancellation.Targets);
            token.Cancel();
            Assert.Equal([destination], cancellation.Targets);
        }

        Assert.False(callback.IsCompleted);
    }

    private static readonly Func<SiloAddress, bool> NotDead = _ => false;

    private static CallbackData CreateRequestCallback(
        IResponseCompletionSource completion,
        ApplicationRequestInstruments instruments,
        out RecordingCancellationManager cancellationManager,
        bool waitForCancellationAcknowledgement = false,
        bool cancelOnTimeout = false,
        TimeProvider? timeProvider = null)
    {
        cancellationManager = new RecordingCancellationManager();
        var shared = new SharedCallbackData(_ => { }, NullLogger<CallbackData>.Instance, timeProvider ?? TimeProvider.System,
            TimeSpan.FromMinutes(1), cancelOnTimeout, waitForCancellationAcknowledgement, cancellationManager);
        return new CallbackData(shared, completion, new Message
        {
            Direction = Message.Directions.Request,
            Id = new CorrelationId(17),
            SendingGrain = GrainId.Create("test", "caller"),
            TargetGrain = GrainId.Create("test", "target"),
            TargetSilo = SiloAddress.New(IPAddress.Loopback, 30001, 1),
            BodyObject = new CancellableInvokable(),
        }, instruments);
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
        return services.BuildServiceProvider();
    }

    private static ApplicationRequestInstruments CreateInstruments(IServiceProvider serviceProvider) =>
        new(new OrleansInstruments(serviceProvider.GetRequiredService<IMeterFactory>()));

    private sealed class TestResponseCompletionSource : IResponseCompletionSource
    {
        public Response Response { get; private set; } = null!;

        public void Complete(Response value) => Response = value;

        public void Complete() => Response = Orleans.Serialization.Invocation.Response.Completed;
    }

    private sealed class RecordingCancellationManager : IGrainCallCancellationManager
    {
        public List<SiloAddress?> Targets { get; } = [];
        public void SignalCancellation(SiloAddress? targetSilo, GrainId targetGrainId, GrainId sendingGrainId, CorrelationId messageId)
            => Targets.Add(targetSilo);
    }

    private sealed class CancellableInvokable : IInvokable
    {
        public static readonly TimeSpan ResponseTimeout = TimeSpan.FromMinutes(3);
        public bool IsCancellable => true;
        public TimeSpan? GetDefaultResponseTimeout() => ResponseTimeout;
        public object? GetTarget() => null;
        public void SetTarget(ITargetHolder holder) { }
        public System.Threading.Tasks.ValueTask<Response> Invoke() => throw new NotSupportedException();
        public int GetArgumentCount() => 0;
        public object? GetArgument(int index) => throw new ArgumentOutOfRangeException(nameof(index));
        public void SetArgument(int index, object value) => throw new ArgumentOutOfRangeException(nameof(index));
        public string GetMethodName() => "Test";
        public string GetInterfaceName() => "ITest";
        public string GetActivityName() => "ITest/Test";
        public System.Reflection.MethodInfo GetMethod() => throw new NotSupportedException();
        public Type GetInterfaceType() => typeof(object);
        public void Dispose() { }
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
