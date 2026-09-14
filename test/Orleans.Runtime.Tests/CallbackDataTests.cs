using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Threading;
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
    [InlineData(null, false)]
    [InlineData("unknown", false)]
    [InlineData("normal-orders", false)]
    [InlineData(null, true)]
    [InlineData("unknown", true)]
    [InlineData("normal-orders", true)]
    public void CancellationAndTimeoutMetricsDistinguishUnavailableFromNamedUnknown(string? typeName, bool timeout)
    {
        using var provider = CreateServiceProvider();
        var meter = new OrleansInstruments(provider.GetRequiredService<IMeterFactory>());
        var instruments = new ApplicationRequestInstruments(meter);
        var samples = new ConcurrentQueue<(Instrument Instrument, long Value, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (ReferenceEquals(instrument.Meter, meter.Meter)
                && instrument.Name is InstrumentNames.APP_REQUESTS_CANCELED or InstrumentNames.APP_REQUESTS_TIMED_OUT)
            {
                owner.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => samples.Enqueue((instrument, value, tags.ToArray())));
        listener.Start();
        var clock = new FakeTimeProvider();
        var completion = new TestResponseCompletionSource();
        var message = new Message { TargetGrain = typeName is null ? default : GrainId.Create(typeName, "request-key") };
        var unregistered = new ConcurrentQueue<Message>();
        var shared = CreateSharedCallbackData(unregistered.Enqueue, clock, TimeSpan.FromSeconds(1));
        var callback = new CallbackData(shared, completion, message, instruments);
        using var cancellation = new CancellationTokenSource();
        callback.SubscribeForCancellation(cancellation.Token);

        if (timeout)
        {
            clock.Advance(TimeSpan.FromSeconds(1) + TimeSpan.FromTicks(1));
            Assert.True(callback.IsExpired(clock.GetTimestamp()));
            callback.OnTimeout();
        }
        else
        {
            cancellation.Cancel();
        }

        var response = completion.Response;
        Assert.True(callback.IsCompleted);
        if (timeout) Assert.IsType<TimeoutException>(response.Exception);
        else Assert.Equal(cancellation.Token, Assert.IsType<OperationCanceledException>(response.Exception).CancellationToken);
        // Losing completion paths cannot add a second metric, unregister, or replace the result.
        callback.OnTimeout();
        cancellation.Cancel();
        callback.OnHostShutdown();
        Assert.Same(response, completion.Response);
        Assert.Equal(1, completion.CompletionCount);
        Assert.Same(message, Assert.Single(unregistered));
        var sample = Assert.Single(samples);
        Assert.IsType<Counter<long>>(sample.Instrument);
        Assert.Equal(timeout ? InstrumentNames.APP_REQUESTS_TIMED_OUT : InstrumentNames.APP_REQUESTS_CANCELED, sample.Instrument.Name);
        Assert.Equal(1, sample.Value);
        Assert.Equal(typeName is null or "unknown" ? 2 : 1, sample.Tags.Length);
        Assert.Equal(typeName ?? "unknown", Assert.IsType<string>(Assert.Single(sample.Tags, tag => tag.Key == "grain_type").Value));
        if (typeName is null or "unknown")
        {
            Assert.Equal(typeName is not null, Assert.IsType<bool>(Assert.Single(sample.Tags, tag => tag.Key == "grain_type_known").Value));
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
