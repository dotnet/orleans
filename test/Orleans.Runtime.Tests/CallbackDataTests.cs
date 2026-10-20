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
using Orleans.CodeGeneration;
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
            TimeSpan.Zero,
            CreateInstruments(serviceProvider));

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

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void SharedInstrumentsRecordEachCallbacksLatencyOnce()
    {
        using var serviceProvider = CreateServiceProvider();
        var meterFactory = serviceProvider.GetRequiredService<IMeterFactory>();
        using var collector = new MetricCollector<double>(meterFactory, "Microsoft.Orleans", InstrumentNames.APP_REQUESTS_LATENCY_HISTOGRAM);
        var timeProvider = new FakeTimeProvider();
        var shared = CreateSharedCallbackData(_ => { }, timeProvider, TimeSpan.FromMinutes(1), CreateInstruments(serviceProvider));
        var first = new CallbackData(shared, new TestResponseCompletionSource(), new Message());
        var second = new CallbackData(shared, new TestResponseCompletionSource(), new Message());

        timeProvider.Advance(TimeSpan.FromMilliseconds(5));
        first.OnHostShutdown();
        timeProvider.Advance(TimeSpan.FromMilliseconds(5));
        second.OnHostShutdown();
        first.OnHostShutdown();
        second.OnHostShutdown();

        var measurements = collector.GetMeasurementSnapshot();
        Assert.Collection(measurements,
            measurement => Assert.Equal(5, measurement.Value),
            measurement => Assert.Equal(10, measurement.Value));
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void TargetSiloFailureCompletesCallWhichWasNotRetargeted()
    {
        using var serviceProvider = CreateServiceProvider();
        var completion = new TestResponseCompletionSource();
        var callback = CreateRequestCallback(completion, CreateInstruments(serviceProvider), out _);

        callback.OnTargetSiloFail(callback.Message.TargetSilo!);

        Assert.True(callback.IsCompleted);
        Assert.IsType<SiloUnavailableException>(completion.Response.Exception);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void RetargetMovesCallAwayFromDepartingSilo()
    {
        using var serviceProvider = CreateServiceProvider();
        var completion = new TestResponseCompletionSource();
        var callback = CreateRequestCallback(completion, CreateInstruments(serviceProvider), out _);
        var original = callback.Message.TargetSilo;
        var destination = SiloAddress.New(IPAddress.Loopback, 30002, 1);

        Assert.True(callback.TryRetarget(destination, forwardCount: 1, NotDead));

        Assert.Equal(destination, callback.Message.TargetSilo);
        Assert.Equal(1, callback.Message.ForwardCount);
        Assert.NotEqual(original, callback.Message.TargetSilo);
        Assert.False(callback.IsCompleted);
        Assert.Null(completion.Response);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void RetargetsAreOrderedByForwardCount()
    {
        using var serviceProvider = CreateServiceProvider();
        var callback = CreateRequestCallback(new TestResponseCompletionSource(), CreateInstruments(serviceProvider), out _);
        var first = SiloAddress.New(IPAddress.Loopback, 30002, 1);
        var second = SiloAddress.New(IPAddress.Loopback, 30003, 1);

        Assert.True(callback.TryRetarget(second, forwardCount: 2, NotDead));
        Assert.False(callback.TryRetarget(first, forwardCount: 1, NotDead));
        Assert.False(callback.TryRetarget(first, forwardCount: 2, NotDead));
        Assert.False(callback.TryRetarget(first, forwardCount: 0, NotDead));

        Assert.Equal(second, callback.Message.TargetSilo);
        Assert.Equal(2, callback.Message.ForwardCount);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void TargetSiloFailureDoesNotFailCallRetargetedAwayFromIt()
    {
        using var serviceProvider = CreateServiceProvider();
        var completion = new TestResponseCompletionSource();
        var callback = CreateRequestCallback(completion, CreateInstruments(serviceProvider), out _);
        var failed = callback.Message.TargetSilo!;
        var destination = SiloAddress.New(IPAddress.Loopback, 30002, 1);

        // A failure sweep which observed the original target before the call was retargeted completes after the retarget.
        Assert.True(callback.TryRetarget(destination, forwardCount: 1, NotDead));
        callback.OnTargetSiloFail(failed);

        Assert.False(callback.IsCompleted);
        Assert.Null(completion.Response);
        Assert.Equal(destination, callback.Message.TargetSilo);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void RetargetToDeadSiloDoesNotFailCallRetargetedAgain()
    {
        using var serviceProvider = CreateServiceProvider();
        var completion = new TestResponseCompletionSource();
        var callback = CreateRequestCallback(completion, CreateInstruments(serviceProvider), out _);
        var dead = SiloAddress.New(IPAddress.Loopback, 30002, 1);
        var live = SiloAddress.New(IPAddress.Loopback, 30003, 1);

        // A newer retarget to a live silo lands between the first retarget and its dead-silo check.
        Assert.True(callback.TryRetarget(dead, forwardCount: 1, silo =>
        {
            Assert.True(callback.TryRetarget(live, forwardCount: 2, NotDead));
            return silo.Equals(dead);
        }));

        Assert.False(callback.IsCompleted);
        Assert.Null(completion.Response);
        Assert.Equal(live, callback.Message.TargetSilo);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void RetargetToDeadSiloFailsCall()
    {
        using var serviceProvider = CreateServiceProvider();
        var completion = new TestResponseCompletionSource();
        var callback = CreateRequestCallback(completion, CreateInstruments(serviceProvider), out _);
        var destination = SiloAddress.New(IPAddress.Loopback, 30002, 1);

        // The destination's failure sweep ran before the retarget was published, so the retarget fails the call.
        Assert.True(callback.TryRetarget(destination, forwardCount: 1, silo => silo.Equals(destination)));

        Assert.True(callback.IsCompleted);
        Assert.IsType<SiloUnavailableException>(completion.Response.Exception);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void RetargetRacingTimeoutSignalsCancellationToNewSilo()
    {
        using var serviceProvider = CreateServiceProvider();
        var completion = new TestResponseCompletionSource();
        var callback = CreateRequestCallback(
            completion,
            CreateInstruments(serviceProvider),
            out var cancellationManager,
            cancelOnTimeout: true);
        var original = callback.Message.TargetSilo;
        var destination = SiloAddress.New(IPAddress.Loopback, 30002, 1);

        // The timeout signals the original silo. A retarget which raced with it signals the silo which now owns the request.
        callback.OnTimeout();
        Assert.Equal([original], cancellationManager.Targets);
        Assert.True(callback.TryRetarget(destination, forwardCount: 1, NotDead));

        Assert.Equal([original, destination], cancellationManager.Targets);
        Assert.IsType<TimeoutException>(completion.Response.Exception);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Theory, TestCategory("BVT")]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationIsSignaledToRetargetedSilo(bool cancelBeforeRetarget)
    {
        using var serviceProvider = CreateServiceProvider();
        using var cancellation = new CancellationTokenSource();
        var callback = CreateRequestCallback(
            new TestResponseCompletionSource(),
            CreateInstruments(serviceProvider),
            out var cancellationManager,
            waitForCancellationAcknowledgement: true);
        var original = callback.Message.TargetSilo;
        var destination = SiloAddress.New(IPAddress.Loopback, 30002, 1);
        callback.SubscribeForCancellation(cancellation.Token);

        if (cancelBeforeRetarget)
        {
            cancellation.Cancel();
            Assert.True(callback.TryRetarget(destination, forwardCount: 1, NotDead));
            Assert.Equal([original, destination], cancellationManager.Targets);
        }
        else
        {
            Assert.True(callback.TryRetarget(destination, forwardCount: 1, NotDead));
            Assert.Empty(cancellationManager.Targets);
            cancellation.Cancel();
            Assert.Equal([destination], cancellationManager.Targets);
        }

        Assert.False(callback.IsCompleted);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void CallKeepsItsInvocationsTimeoutAndCancellationAfterItsMessageBodyIsReleased()
    {
        using var serviceProvider = CreateServiceProvider();
        using var cancellation = new CancellationTokenSource();
        var timeProvider = new FakeTimeProvider();
        var callback = CreateRequestCallback(
            new TestResponseCompletionSource(),
            CreateInstruments(serviceProvider),
            out var cancellationManager,
            waitForCancellationAcknowledgement: true,
            timeProvider: timeProvider);
        callback.SubscribeForCancellation(cancellation.Token);

        // A message which a connection retries holds its serialized body, which is released once the retry is sent.
        callback.Message.BodyObject = null;

        // The call's own response timeout, which is longer than the default, still applies.
        timeProvider.Advance(TimeSpan.FromMinutes(2));
        Assert.False(callback.IsExpired(timeProvider.GetTimestamp()));
        timeProvider.Advance(CancellableInvokable.ResponseTimeout);
        Assert.True(callback.IsExpired(timeProvider.GetTimestamp()));

        // The call is still cancellable, so its cancellation is signaled to its target.
        cancellation.Cancel();
        Assert.Equal([callback.Message.TargetSilo], cancellationManager.Targets);
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
        var shared = new SharedCallbackData(
            _ => { },
            logger: NullLogger<CallbackData>.Instance,
            timeProvider ?? TimeProvider.System,
            TimeSpan.FromMinutes(1),
            cancelOnTimeout,
            waitForCancellationAcknowledgement,
            cancellationManager,
            instruments);
        var message = new Message
        {
            Direction = Message.Directions.Request,
            Id = new CorrelationId(17),
            SendingGrain = GrainId.Create("test", "caller"),
            TargetGrain = GrainId.Create("test", "target"),
            TargetSilo = SiloAddress.New(IPAddress.Loopback, 30001, 1),
            BodyObject = new CancellableInvokable(),
        };

        return new CallbackData(shared, completion, message);
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
            responseTimeout ?? TimeSpan.FromMinutes(1),
            instruments);
        return new CallbackData(shared, completion, new Message());
    }

    private static SharedCallbackData CreateSharedCallbackData(
        Action<Message> unregister,
        TimeProvider timeProvider,
        TimeSpan responseTimeout,
        ApplicationRequestInstruments instruments)
        => new(
            unregister,
            logger: NullLogger<CallbackData>.Instance,
            timeProvider,
            responseTimeout,
            cancelOnTimeout: false,
            waitForCancellationAcknowledgement: false,
            cancellationManager: null,
            instruments);

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
