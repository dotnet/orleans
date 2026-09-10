using System;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Orleans.Runtime;
using Orleans.Serialization.Invocation;
using Xunit;

namespace Tester;

public class CallbackDataTests
{
    [TestSuite("BVT"), TestProvider("None")]
    [Theory, TestCategory("BVT")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CancellationPolicyCombinesInvocationAndGlobalOptions(bool global, bool invocation)
    {
        using var serviceProvider = CreateServiceProvider();
        using var cancellation = new CancellationTokenSource();
        var completion = new TestResponseCompletionSource();
        var manager = new TestCancellationManager();
        var unregisterCount = 0;
        var message = new Message { BodyObject = new CancellableRequest() };
        var shared = new SharedCallbackData(
            _ => unregisterCount++,
            NullLogger<CallbackData>.Instance,
            TimeProvider.System,
            TimeSpan.FromSeconds(1),
            cancelOnTimeout: false,
            waitForCancellationAcknowledgement: global,
            manager);
        var callback = new CallbackData(shared, completion, message, CreateInstruments(serviceProvider), invocation);
        callback.SubscribeForCancellation(cancellation.Token);

        cancellation.Cancel();

        Assert.Equal(1, manager.SignalCount);
        Assert.Equal(message.Id, manager.MessageId);
        Assert.Equal(!(global || invocation), callback.IsCompleted);
        Assert.Equal(global || invocation ? 0 : 1, unregisterCount);
        if (global || invocation)
        {
            Assert.Null(completion.Response);
            callback.DoCallback(new Message { BodyObject = Response.FromResult(42) });
            Assert.Equal(42, Assert.IsAssignableFrom<Response>(completion.Response).GetResult<int>());
        }
        else
        {
            Assert.Equal(cancellation.Token, Assert.IsType<OperationCanceledException>(completion.Response.Exception).CancellationToken);
        }

        Assert.Equal(1, completion.CompletionCount);
    }

    [TestSuite("BVT"), TestProvider("None")]
    [Theory, TestCategory("BVT")]
    [InlineData("response")]
    [InlineData("acknowledgement")]
    [InlineData("timeout")]
    [InlineData("shutdown")]
    [InlineData("silo failure")]
    public void OptedInCancellationRetainsTerminalRuntimeOutcomes(string outcome)
    {
        using var serviceProvider = CreateServiceProvider();
        using var cancellation = new CancellationTokenSource();
        var timeProvider = new FakeTimeProvider();
        var completion = new TestResponseCompletionSource();
        var unregisterCount = 0;
        var manager = new TestCancellationManager();
        var shared = new SharedCallbackData(
            _ => unregisterCount++,
            NullLogger<CallbackData>.Instance,
            timeProvider,
            TimeSpan.FromSeconds(1),
            cancelOnTimeout: false,
            waitForCancellationAcknowledgement: false,
            manager);
        var callback = new CallbackData(
            shared, completion, new Message { BodyObject = new CancellableRequest() },
            CreateInstruments(serviceProvider), waitForCancellationAcknowledgement: true);
        cancellation.Cancel();
        callback.SubscribeForCancellation(cancellation.Token);
        Assert.False(callback.IsCompleted);
        Assert.Null(completion.Response);
        Assert.Equal(1, manager.SignalCount);

        switch (outcome)
        {
            case "response":
                callback.DoCallback(new Message { BodyObject = Response.FromResult(42) });
                Assert.Equal(42, Assert.IsAssignableFrom<Response>(completion.Response).GetResult<int>());
                break;
            case "acknowledgement":
                callback.DoCallback(new Message { BodyObject = Response.FromException(new OperationCanceledException()) });
                Assert.IsType<OperationCanceledException>(Assert.IsAssignableFrom<Response>(completion.Response).Exception);
                break;
            case "timeout":
                timeProvider.Advance(TimeSpan.FromSeconds(2));
                Assert.True(callback.IsExpired(timeProvider.GetTimestamp()));
                callback.OnTimeout();
                Assert.IsType<TimeoutException>(Assert.IsAssignableFrom<Response>(completion.Response).Exception);
                break;
            case "shutdown":
                callback.OnHostShutdown();
                Assert.IsType<SiloUnavailableException>(Assert.IsAssignableFrom<Response>(completion.Response).Exception);
                break;
            case "silo failure":
                callback.OnTargetSiloFail();
                Assert.IsType<SiloUnavailableException>(Assert.IsAssignableFrom<Response>(completion.Response).Exception);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        Assert.True(callback.IsCompleted);
        Assert.Equal(outcome is "response" or "acknowledgement" ? 0 : 1, unregisterCount);
        callback.OnTimeout();
        callback.OnHostShutdown();
        Assert.Equal(1, completion.CompletionCount);
        Assert.Equal(1, manager.SignalCount);
    }

    [TestSuite("BVT"), TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void ReusingRequestDoesNotRetainInvocationPolicy()
    {
        using var serviceProvider = CreateServiceProvider();
        var request = new CancellableRequest();
        var shared = CreateSharedCallbackData(_ => { }, TimeProvider.System, TimeSpan.FromSeconds(1));
        foreach (var optedIn in new[] { true, false, true, false })
        {
            using var cancellation = new CancellationTokenSource();
            var completion = new TestResponseCompletionSource();
            var callback = new CallbackData(
                shared, completion, new Message { BodyObject = request },
                CreateInstruments(serviceProvider), optedIn);
            callback.SubscribeForCancellation(cancellation.Token);
            cancellation.Cancel();
            Assert.Equal(!optedIn, callback.IsCompleted);
            callback.OnHostShutdown();
            Assert.Equal(1, completion.CompletionCount);
            request.Dispose();
        }
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

        public int CompletionCount { get; private set; }

        public void Complete(Response value)
        {
            Response = value;
            CompletionCount++;
        }

        public void Complete() => Complete(Orleans.Serialization.Invocation.Response.Completed);
    }

    private sealed class TestCancellationManager : IGrainCallCancellationManager
    {
        public int SignalCount { get; private set; }
        public CorrelationId MessageId { get; private set; }

        public void SignalCancellation(SiloAddress? targetSilo, GrainId targetGrainId, GrainId sendingGrainId, CorrelationId messageId)
        {
            SignalCount++;
            MessageId = messageId;
        }
    }

    private sealed class CancellableRequest : RequestBase
    {
        private readonly object _target = new();

        public override bool IsCancellable => true;
        public override void Dispose() { }
        public override object GetTarget() => _target;
        public override void SetTarget(ITargetHolder holder) => throw new NotSupportedException();
        public override ValueTask<Response> Invoke() => throw new NotSupportedException();
        public override string GetMethodName() => nameof(Invoke);
        public override string GetInterfaceName() => nameof(CancellableRequest);
        public override string GetActivityName() => nameof(CancellableRequest);
        public override Type GetInterfaceType() => typeof(CancellableRequest);
        public override MethodInfo GetMethod() => typeof(CancellableRequest).GetMethod(nameof(Invoke))!;
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
