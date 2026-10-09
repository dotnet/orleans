using System;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Orleans.Runtime;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.Buffers;
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

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Theory, TestCategory("BVT")]
    [InlineData(false)]
    [InlineData(true)]
    public void CompletionPublicationFailure_StillCompletesOwnedResources(bool timeout)
    {
        using var services = CreateServiceProvider();
        using var source = new ArcBufferWriter();
        source.Write(new byte[] { 4, 7, 9 });
        var owner = new PinOwner(source.PeekSlice(source.Length));
        var page = owner.Buffer.First;
        var firstCause = new InvalidOperationException("completion publication failed");
        var message = new Message { BodyObject = owner };
        var callback = new CallbackData(CreateSharedCallbackData(_ => { }, TimeProvider.System, TimeSpan.FromSeconds(1)),
            new ThrowingCompletionSource(firstCause), message, CreateInstruments(services));
        var observed = Assert.Throws<InvalidOperationException>(() =>
        {
            if (timeout) callback.OnTimeout();
            else callback.OnHostShutdown();
        });
        Assert.Same(firstCause, observed);
        Assert.True(callback.IsCompleted);
        Assert.Equal(1, owner.CompletionCount);
        Assert.Null(owner.Buffer.First);
        callback.OnHostShutdown();
        message.Dispose();
        Assert.Equal(1, owner.CompletionCount);
        Assert.Null(owner.Buffer.First);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void CancellationAwaitingAcknowledgement_DoesNotCompleteOwnedResources()
    {
        using var services = CreateServiceProvider();
        using var source = new ArcBufferWriter();
        source.Write(new byte[] { 4, 7, 9 });
        var owner = new PinOwner(source.PeekSlice(source.Length));
        var page = owner.Buffer.First;
        using var cancellation = new CancellationTokenSource();
        var message = new Message { BodyObject = owner };
        var callback = new CallbackData(new SharedCallbackData(_ => { }, NullLogger<CallbackData>.Instance,
            TimeProvider.System, TimeSpan.FromSeconds(1), false, true, null),
            new TestResponseCompletionSource(), message, CreateInstruments(services));
        callback.SubscribeForCancellation(cancellation.Token);
        cancellation.Cancel();
        Assert.False(callback.IsCompleted);
        Assert.Equal(0, owner.CompletionCount);
        Assert.Same(page, owner.Buffer.First);
        Assert.Equal(new byte[] { 4, 7, 9 }, owner.Buffer.ToArray());
        callback.OnHostShutdown();
        Assert.Equal(1, owner.CompletionCount);
        Assert.Null(owner.Buffer.First);
        message.Dispose();
    }

    private sealed class PinOwner(ArcBuffer buffer) : IInvokableArgumentOwner
    {
        internal ArcBuffer Buffer = buffer;
        private int _completed;
        internal int CompletionCount => _completed;
        public bool TryRetainArgumentResources() => throw new NotSupportedException("These callback tests do not acquire temporary uses.");
        public void ReleaseArgumentResources() => throw new NotSupportedException("These callback tests do not acquire temporary uses.");
        public void CompleteArgumentResources()
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0) return;
            Buffer.Dispose();
            Buffer = default;
        }
    }

    private sealed class ThrowingCompletionSource(Exception exception) : IResponseCompletionSource
    {
        public void Complete(Response value) => throw exception;
        public void Complete() => throw exception;
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
