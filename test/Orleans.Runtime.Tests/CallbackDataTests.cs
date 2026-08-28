using System;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Runtime;
using Orleans.Serialization.Invocation;
using Xunit;

namespace Tester;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public class CallbackDataTests
{
    [Fact]
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

    [Fact]
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

    [Fact]
    public void TimeoutAndResponseRaceCompletesExactlyOnce()
    {
        using var serviceProvider = CreateServiceProvider();
        var registry = new TestCallbackRegistry(CreateInstruments(serviceProvider));
        var completion = new TestResponseCompletionSource();
        var callback = registry.Register(new CorrelationId(1), completion);
        var response = CreateResponse(callback.Message);

        Parallel.Invoke(
            callback.OnTimeout,
            () => registry.TryCompleteResponse(response));

        Assert.Equal(1, completion.CompletionCount);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void CancellationAndResponseRaceCompletesExactlyOnce()
    {
        using var serviceProvider = CreateServiceProvider();
        using var cancellation = new CancellationTokenSource();
        var registry = new TestCallbackRegistry(CreateInstruments(serviceProvider));
        var completion = new TestResponseCompletionSource();
        var callback = registry.Register(new CorrelationId(2), completion);
        callback.SubscribeForCancellation(cancellation.Token);
        var response = CreateResponse(callback.Message);

        Parallel.Invoke(
            cancellation.Cancel,
            () => registry.TryCompleteResponse(response));

        Assert.Equal(1, completion.CompletionCount);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void StaleCancellationDoesNotRemoveReplacement()
    {
        using var serviceProvider = CreateServiceProvider();
        using var cancellation = new CancellationTokenSource();
        var registry = new TestCallbackRegistry(CreateInstruments(serviceProvider));
        var id = new CorrelationId(3);
        var staleCompletion = new TestResponseCompletionSource();
        var stale = registry.Register(id, staleCompletion);
        stale.SubscribeForCancellation(cancellation.Token);
        Assert.True(registry.TryTake(id, out var removed));
        Assert.Same(stale, removed);
        var replacementCompletion = new TestResponseCompletionSource();
        var replacement = registry.Register(id, replacementCompletion);

        cancellation.Cancel();

        Assert.Same(replacement, registry.Take(id));
        replacement.DoCallback(CreateResponse(replacement.Message));
        Assert.IsType<OperationCanceledException>(staleCompletion.Response.Exception);
        Assert.Same(Response.Completed, replacementCompletion.Response);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void StaleTimeoutDoesNotRemoveReplacement()
    {
        using var serviceProvider = CreateServiceProvider();
        var registry = new TestCallbackRegistry(CreateInstruments(serviceProvider));
        var id = new CorrelationId(4);
        var staleCompletion = new TestResponseCompletionSource();
        var stale = registry.Register(id, staleCompletion);
        Assert.True(registry.TryTake(id, out var removed));
        Assert.Same(stale, removed);
        var replacementCompletion = new TestResponseCompletionSource();
        var replacement = registry.Register(id, replacementCompletion);

        stale.OnTimeout();

        Assert.Same(replacement, registry.Take(id));
        replacement.DoCallback(CreateResponse(replacement.Message));
        Assert.IsType<TimeoutException>(staleCompletion.Response.Exception);
        Assert.Same(Response.Completed, replacementCompletion.Response);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void StaleShutdownDoesNotRemoveReplacement()
    {
        using var serviceProvider = CreateServiceProvider();
        var registry = new TestCallbackRegistry(CreateInstruments(serviceProvider));
        var id = new CorrelationId(5);
        var staleCompletion = new TestResponseCompletionSource();
        var stale = registry.Register(id, staleCompletion);
        Assert.True(registry.TryTake(id, out var removed));
        Assert.Same(stale, removed);
        var replacementCompletion = new TestResponseCompletionSource();
        var replacement = registry.Register(id, replacementCompletion);

        stale.OnHostShutdown();

        Assert.Same(replacement, registry.Take(id));
        replacement.DoCallback(CreateResponse(replacement.Message));
        Assert.IsType<SiloUnavailableException>(staleCompletion.Response.Exception);
        Assert.Same(Response.Completed, replacementCompletion.Response);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void DuplicateRegistrationPreservesOriginalCallback()
    {
        using var serviceProvider = CreateServiceProvider();
        var registry = new TestCallbackRegistry(CreateInstruments(serviceProvider));
        var id = new CorrelationId(6);
        var completion = new TestResponseCompletionSource();
        var callback = registry.Register(id, completion);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            registry.Register(id, new TestResponseCompletionSource()));

        Assert.Contains(id.ToString(), exception.Message);
        Assert.Same(callback, registry.Take(id));
        callback.DoCallback(CreateResponse(callback.Message));
        Assert.Same(Response.Completed, completion.Response);
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
        Action<CallbackData> unregister,
        ApplicationRequestInstruments instruments)
    {
        var shared = new SharedCallbackData(
            logger: NullLogger<CallbackData>.Instance,
            responseTimeout: TimeSpan.FromMinutes(1),
            cancelOnTimeout: false,
            waitForCancellationAcknowledgement: false,
            cancellationManager: null);
        return new CallbackData(shared, new DelegateCallbackTarget(unregister), completion, new Message(), instruments);
    }

    private static ServiceProvider CreateServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        return services.BuildServiceProvider();
    }

    private static ApplicationRequestInstruments CreateInstruments(IServiceProvider serviceProvider) =>
        new(new OrleansInstruments(serviceProvider.GetRequiredService<IMeterFactory>()));

    private static Message CreateResponse(Message request) => new()
    {
        Direction = Message.Directions.Response,
        Id = request.Id,
        BodyObject = Response.Completed,
    };

    private sealed class TestResponseCompletionSource : IResponseCompletionSource
    {
        private Response? _response;
        private int _completionCount;

        public Response Response => Volatile.Read(ref _response)!;

        public int CompletionCount => Volatile.Read(ref _completionCount);

        public void Complete(Response value)
        {
            Interlocked.Increment(ref _completionCount);
            Interlocked.CompareExchange(ref _response, value, null);
        }

        public void Complete() => Complete(Response.Completed);
    }

    private sealed class DelegateCallbackTarget(Action<CallbackData> unregister) : ICallbackDataTarget
    {
        public void Unregister(CallbackData callback) => unregister(callback);
    }

    private sealed class TestCallbackRegistry(ApplicationRequestInstruments instruments) : ICallbackDataTarget
    {
        private readonly StripedCallbackDictionary<CallbackData> _callbacks = new();

        public int Count => _callbacks.Count;

        public CallbackData Register(CorrelationId id, IResponseCompletionSource completion)
        {
            var message = new Message { Id = id };
            var callback = new CallbackData(CreateSharedData(), this, completion, message, instruments);
            if (!_callbacks.TryAdd(id, callback))
            {
                throw new InvalidOperationException($"A callback with correlation id {id} is already registered.");
            }

            return callback;
        }

        public bool TryTake(CorrelationId id, out CallbackData? callback) =>
            _callbacks.TryRemove(id, out callback);

        public CallbackData Take(CorrelationId id)
        {
            Assert.True(_callbacks.TryRemove(id, out var callback));
            return callback;
        }

        public void TryCompleteResponse(Message response)
        {
            if (_callbacks.TryRemove(response.Id, out var callback))
            {
                callback.DoCallback(response);
            }
        }

        void ICallbackDataTarget.Unregister(CallbackData callback) =>
            _callbacks.TryRemove(callback.Message.Id, callback);
    }

    private static SharedCallbackData CreateSharedData() => new(
        logger: NullLogger<CallbackData>.Instance,
        responseTimeout: TimeSpan.FromMinutes(1),
        cancelOnTimeout: false,
        waitForCancellationAcknowledgement: false,
        cancellationManager: null);
}
