using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Runtime;
using Orleans.Serialization.Invocation;
using Xunit;

namespace Tester;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public class CallbackRegistryTests
{
    private static readonly Action<CallbackData, object?> EmptyVisitor = static (_, _) => { };

    [Fact]
    public void GetStripeIndex_OverflowAndStride_DistributesCorrelationIds()
    {
        var start = long.MaxValue - (CallbackRegistry.StripeCount / 2);
        var consecutiveStripes = Enumerable.Range(0, CallbackRegistry.StripeCount)
            .Select(offset => new CorrelationId(unchecked(start + offset)))
            .Select(CallbackRegistry.GetStripeIndex)
            .Distinct()
            .Count();
        var stridedStripes = Enumerable.Range(0, CallbackRegistry.StripeCount)
            .Select(offset => new CorrelationId(offset * CallbackRegistry.StripeCount))
            .Select(CallbackRegistry.GetStripeIndex)
            .Distinct()
            .Count();

        Assert.True(consecutiveStripes > CallbackRegistry.StripeCount / 2);
        Assert.True(stridedStripes > CallbackRegistry.StripeCount / 2);
    }

    [Fact]
    public void RegisterGetAndRemove_CallbackIdentityIsPreserved()
    {
        using var serviceProvider = CreateServiceProvider();
        var registry = new CallbackRegistry();
        var callback = CreateCallback(registry, new TestResponseCompletionSource(), CreateRequest(42), serviceProvider);
        var duplicate = CreateCallback(registry, new TestResponseCompletionSource(), CreateRequest(42), serviceProvider);

        registry.Register(callback);
        Assert.Throws<InvalidOperationException>(() => registry.Register(duplicate));
        Assert.True(registry.TryGetResponseCallback(CreateResponse(callback.Message), out var found));
        Assert.Same(callback, found);
        Assert.False(registry.TryRemove(duplicate));
        Assert.True(registry.TryRemove(callback));
        Assert.False(registry.TryGetResponseCallback(CreateResponse(callback.Message), out _));
    }

    [Fact]
    public void ConcurrentOperations_CountAndCallbacksRemainExact()
    {
        const int count = 10_000;
        using var serviceProvider = CreateServiceProvider();
        var registry = new CallbackRegistry();
        var shared = CreateSharedCallbackData(callback => registry.TryRemove(callback));
        var instruments = CreateInstruments(serviceProvider);
        var callbacks = new CallbackData[count];

        Parallel.For(0, count, index =>
        {
            var callback = CreateCallback(
                new TestResponseCompletionSource(),
                CreateRequest(index),
                shared,
                instruments);
            callbacks[index] = callback;
            registry.Register(callback);
            Assert.True(registry.TryGetResponseCallback(CreateResponse(callback.Message), out var found));
            Assert.Same(callback, found);
        });

        Assert.Equal(count, registry.GetRunningRequestCountForTest(default));

        Parallel.ForEach(callbacks, callback => Assert.True(registry.TryRemove(callback)));

        Assert.Equal(0, registry.GetRunningRequestCountForTest(default));
    }

    [Fact]
    public void ForEach_SnapshotAllowsCallbacksToRemoveThemselves()
    {
        using var serviceProvider = CreateServiceProvider();
        var registry = new CallbackRegistry();
        for (var index = 0; index < 32; index++)
        {
            registry.Register(
                CreateCallback(registry, new TestResponseCompletionSource(), CreateRequest(index), serviceProvider));
        }

        registry.ForEach(registry, static (callback, registry) => Assert.True(registry.TryRemove(callback)));

        Assert.Equal(0, registry.GetRunningRequestCountForTest(default));
    }

    [Fact]
    public void ForEach_EmptyRegistry_DoesNotAllocate()
    {
        var registry = new CallbackRegistry();
        registry.ForEach((object?)null, EmptyVisitor);

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        registry.ForEach((object?)null, EmptyVisitor);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void GetRunningRequestCountForTest_DoesNotAllocate()
    {
        using var serviceProvider = CreateServiceProvider();
        var registry = new CallbackRegistry();
        var callback = CreateCallback(registry, new TestResponseCompletionSource(), CreateRequest(42), serviceProvider);
        registry.Register(callback);
        Assert.Equal(1, registry.GetRunningRequestCountForTest(default));

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var count = registry.GetRunningRequestCountForTest(default);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.Equal(1, count);
        Assert.Equal(0, allocated);
        Assert.True(registry.TryRemove(callback));
    }

    [Fact]
    public void TryCompleteResponse_CompletesCallbackAndRemovesRegistration()
    {
        using var serviceProvider = CreateServiceProvider();
        var registry = new CallbackRegistry();
        var completion = new TestResponseCompletionSource();
        var callback = CreateCallback(registry, completion, CreateRequest(1), serviceProvider);
        registry.Register(callback);
        var response = CreateResponse(callback.Message);

        Assert.True(registry.TryCompleteResponse(response));

        Assert.Same(Response.Completed, completion.Response);
        Assert.Equal(1, completion.CompletionCount);
        Assert.Equal(0, registry.GetRunningRequestCountForTest(default));
    }

    [Fact]
    public void TryCompleteResponse_RejectionCompletesExactCallback()
    {
        using var serviceProvider = CreateServiceProvider();
        var registry = new CallbackRegistry();
        var completion = new TestResponseCompletionSource();
        var callback = CreateCallback(registry, completion, CreateRequest(7), serviceProvider);
        registry.Register(callback);
        var response = CreateResponse(callback.Message);
        response.Result = Message.ResponseTypes.Rejection;
        response.BodyObject = new RejectionResponse
        {
            RejectionType = Message.RejectionTypes.Transient,
            RejectionInfo = "Rejected",
        };

        Assert.True(registry.TryCompleteResponse(response));

        Assert.IsType<OrleansMessageRejectionException>(completion.Response.Exception);
        Assert.Equal(1, completion.CompletionCount);
        Assert.Equal(0, registry.GetRunningRequestCountForTest(default));
    }

    [Fact]
    public void TryGetResponseCallback_StatusUsesRegisteredCallback()
    {
        using var serviceProvider = CreateServiceProvider();
        var registry = new CallbackRegistry();
        var callback = CreateCallback(
            registry,
            new TestResponseCompletionSource(),
            CreateRequest(3),
            serviceProvider);
        registry.Register(callback);
        var status = CreateResponse(callback.Message);
        status.Result = Message.ResponseTypes.Status;
        status.BodyObject = new StatusResponse(true, false, []);

        Assert.True(registry.TryGetResponseCallback(status, out var result));

        Assert.Same(callback, result);
        callback.OnHostShutdown();
    }

    [Fact]
    public void TryRemove_StaleCallbackDoesNotRemoveReplacement()
    {
        using var serviceProvider = CreateServiceProvider();
        var registry = new CallbackRegistry();
        var firstCompletion = new TestResponseCompletionSource();
        var first = CreateCallback(registry, firstCompletion, CreateRequest(4), serviceProvider);
        registry.Register(first);
        Assert.True(registry.TryRemove(first));

        var replacementCompletion = new TestResponseCompletionSource();
        var replacement = CreateCallback(registry, replacementCompletion, CreateRequest(4), serviceProvider);
        registry.Register(replacement);
        first.OnTimeout();

        Assert.Equal(1, registry.GetRunningRequestCountForTest(default));
        Assert.True(registry.TryCompleteResponse(CreateResponse(replacement.Message)));

        Assert.IsType<TimeoutException>(firstCompletion.Response.Exception);
        Assert.Same(Response.Completed, replacementCompletion.Response);
        Assert.Equal(1, firstCompletion.CompletionCount);
        Assert.Equal(1, replacementCompletion.CompletionCount);
        Assert.Equal(0, registry.GetRunningRequestCountForTest(default));
    }

    [Theory]
    [InlineData(TerminalRace.Timeout)]
    [InlineData(TerminalRace.TargetFailure)]
    [InlineData(TerminalRace.Shutdown)]
    public void TryCompleteResponse_TerminalRace_CompletesExactlyOnce(TerminalRace race)
    {
        using var serviceProvider = CreateServiceProvider();
        var registry = new CallbackRegistry();
        var completion = new TestResponseCompletionSource();
        var callback = CreateCallback(registry, completion, CreateRequest(5), serviceProvider);
        registry.Register(callback);
        var response = CreateResponse(callback.Message);

        Parallel.Invoke(
            () => CompleteTerminal(callback, race),
            () => registry.TryCompleteResponse(response));

        Assert.Equal(1, completion.CompletionCount);
        Assert.Equal(0, registry.GetRunningRequestCountForTest(default));
    }

    [Fact]
    public void TryCompleteResponse_CancellationRace_CompletesExactlyOnce()
    {
        using var serviceProvider = CreateServiceProvider();
        using var cancellation = new CancellationTokenSource();
        var registry = new CallbackRegistry();
        var completion = new TestResponseCompletionSource();
        var callback = CreateCallback(registry, completion, CreateRequest(6), serviceProvider);
        registry.Register(callback);
        callback.SubscribeForCancellation(cancellation.Token);
        var response = CreateResponse(callback.Message);

        Parallel.Invoke(
            cancellation.Cancel,
            () => registry.TryCompleteResponse(response));

        Assert.Equal(1, completion.CompletionCount);
        Assert.Equal(0, registry.GetRunningRequestCountForTest(default));
    }

    [Fact]
    public async Task TryCompleteResponse_ConcurrentTerminalCompletionReportsMatchingRegistration()
    {
        using var serviceProvider = CreateServiceProvider();
        using var unregisterEntered = new ManualResetEventSlim();
        using var releaseUnregister = new ManualResetEventSlim();
        var cancellationToken = TestContext.Current.CancellationToken;
        var registry = new CallbackRegistry();
        var completion = new TestResponseCompletionSource();
        var request = CreateRequest(10);
        var callback = CreateCallback(
            completion,
            request,
            serviceProvider,
            callback =>
            {
                unregisterEntered.Set();
                releaseUnregister.Wait(cancellationToken);
                registry.TryRemove(callback);
            });
        registry.Register(callback);
        var terminalTask = Task.Run(callback.OnTimeout, cancellationToken);
        bool found;

        try
        {
            unregisterEntered.Wait(cancellationToken);
            found = registry.TryCompleteResponse(CreateResponse(request));
        }
        finally
        {
            releaseUnregister.Set();
            await terminalTask;
        }

        Assert.True(found);
        Assert.IsType<TimeoutException>(completion.Response.Exception);
        Assert.Equal(1, completion.CompletionCount);
        Assert.Equal(0, registry.GetRunningRequestCountForTest(default));
    }

    [Theory]
    [InlineData(TerminalRace.Timeout)]
    [InlineData(TerminalRace.TargetFailure)]
    [InlineData(TerminalRace.Shutdown)]
    public void TryCompleteResponse_TerminalRaceStress_CompletesEveryCallbackExactlyOnce(TerminalRace race)
    {
        const int count = 1_000;
        using var serviceProvider = CreateServiceProvider();
        using var barrier = new Barrier(2);
        var registry = new CallbackRegistry();
        var completions = new TestResponseCompletionSource[count];
        var callbacks = new CallbackData[count];
        var responses = new Message[count];
        for (var index = 0; index < count; index++)
        {
            completions[index] = new TestResponseCompletionSource();
            callbacks[index] = CreateCallback(registry, completions[index], CreateRequest(1_000 + index), serviceProvider);
            registry.Register(callbacks[index]);
            responses[index] = CreateResponse(callbacks[index].Message);
        }

        Parallel.Invoke(
            () =>
            {
                for (var index = 0; index < count; index++)
                {
                    barrier.SignalAndWait();
                    CompleteTerminal(callbacks[index], race);
                }
            },
            () =>
            {
                for (var index = 0; index < count; index++)
                {
                    barrier.SignalAndWait();
                    registry.TryCompleteResponse(responses[index]);
                }
            });

        Assert.All(completions, completion => Assert.Equal(1, completion.CompletionCount));
        Assert.Equal(0, registry.GetRunningRequestCountForTest(default));
    }

    [Fact]
    public void TryCompleteResponse_CancellationRaceStress_CompletesEveryCallbackExactlyOnce()
    {
        const int count = 1_000;
        using var serviceProvider = CreateServiceProvider();
        using var barrier = new Barrier(2);
        var registry = new CallbackRegistry();
        var cancellations = new CancellationTokenSource[count];
        var completions = new TestResponseCompletionSource[count];
        var callbacks = new CallbackData[count];
        var responses = new Message[count];
        for (var index = 0; index < count; index++)
        {
            cancellations[index] = new CancellationTokenSource();
            completions[index] = new TestResponseCompletionSource();
            callbacks[index] = CreateCallback(registry, completions[index], CreateRequest(10_000 + index), serviceProvider);
            registry.Register(callbacks[index]);
            callbacks[index].SubscribeForCancellation(cancellations[index].Token);
            responses[index] = CreateResponse(callbacks[index].Message);
        }

        Parallel.Invoke(
            () =>
            {
                for (var index = 0; index < count; index++)
                {
                    barrier.SignalAndWait();
                    cancellations[index].Cancel();
                }
            },
            () =>
            {
                for (var index = 0; index < count; index++)
                {
                    barrier.SignalAndWait();
                    registry.TryCompleteResponse(responses[index]);
                }
            });

        Assert.All(completions, completion => Assert.Equal(1, completion.CompletionCount));
        Assert.Equal(0, registry.GetRunningRequestCountForTest(default));

        foreach (var cancellation in cancellations)
        {
            cancellation.Dispose();
        }
    }

    [Fact]
    public void TryRegister_DuplicateCorrelationIdAcrossSenders_Throws()
    {
        using var serviceProvider = CreateServiceProvider();
        var registry = new CallbackRegistry();
        var first = CreateCallback(registry, new TestResponseCompletionSource(), CreateRequest(8), serviceProvider);
        registry.Register(first);
        var duplicateRequest = CreateRequest(8);
        duplicateRequest.SendingGrain = GrainId.Create("callback-caller", "2");
        var duplicate = CreateCallback(
            registry,
            new TestResponseCompletionSource(),
            duplicateRequest,
            serviceProvider);

        Assert.Throws<InvalidOperationException>(() => registry.Register(duplicate));

        first.OnHostShutdown();
        Assert.Equal(0, registry.GetRunningRequestCountForTest(default));
    }

    private static void CompleteTerminal(CallbackData callback, TerminalRace race)
    {
        switch (race)
        {
            case TerminalRace.Timeout:
                callback.OnTimeout();
                break;
            case TerminalRace.TargetFailure:
                callback.OnTargetSiloFail();
                break;
            case TerminalRace.Shutdown:
                callback.OnHostShutdown();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(race));
        }
    }

    private static CallbackData CreateCallback(
        CallbackRegistry registry,
        IResponseCompletionSource completion,
        Message request,
        IServiceProvider serviceProvider)
        => CreateCallback(
            completion,
            request,
            serviceProvider,
            callback => registry.TryRemove(callback));

    private static CallbackData CreateCallback(
        IResponseCompletionSource completion,
        Message request,
        IServiceProvider serviceProvider,
        Action<CallbackData> unregister)
        => CreateCallback(
            completion,
            request,
            CreateSharedCallbackData(unregister),
            CreateInstruments(serviceProvider));

    private static CallbackData CreateCallback(
        IResponseCompletionSource completion,
        Message request,
        SharedCallbackData shared,
        ApplicationRequestInstruments instruments)
        => new(shared, completion, request, instruments);

    private static SharedCallbackData CreateSharedCallbackData(Action<CallbackData> unregister)
    {
        return new SharedCallbackData(
            unregister,
            NullLogger<CallbackData>.Instance,
            responseTimeout: TimeSpan.FromMinutes(1),
            cancelOnTimeout: false,
            waitForCancellationAcknowledgement: false,
            cancellationManager: null);
    }

    private static ApplicationRequestInstruments CreateInstruments(IServiceProvider serviceProvider)
        => new(new OrleansInstruments(serviceProvider.GetRequiredService<IMeterFactory>()));

    private static Message CreateRequest(long id) => new()
    {
        Id = new CorrelationId(id),
        SendingGrain = GrainId.Create("callback-caller", "1"),
        TargetGrain = GrainId.Create("callback-target", "1"),
    };

    private static Message CreateResponse(Message request) => new()
    {
        Direction = Message.Directions.Response,
        Result = Message.ResponseTypes.Success,
        Id = request.Id,
        TargetGrain = request.SendingGrain,
        SendingGrain = request.TargetGrain,
        BodyObject = Response.Completed,
    };

    private static ServiceProvider CreateServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        return services.BuildServiceProvider();
    }

    public enum TerminalRace
    {
        Timeout,
        TargetFailure,
        Shutdown,
    }

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
}
