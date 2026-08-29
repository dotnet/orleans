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
    [Fact]
    public void TryCompleteResponse_CompletesCallbackAndRemovesRegistration()
    {
        using var serviceProvider = CreateServiceProvider();
        var registry = new CallbackRegistry();
        var completion = new TestResponseCompletionSource();
        var callback = CreateCallback(registry, completion, CreateRequest(1), serviceProvider);
        Assert.True(registry.TryRegister(callback));
        var response = CreateResponse(callback.Message);

        Assert.True(registry.TryCompleteResponse(response));

        Assert.Same(Response.Completed, completion.Response);
        Assert.Equal(1, completion.CompletionCount);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void TryCompleteResponse_RejectionCompletesExactCallback()
    {
        using var serviceProvider = CreateServiceProvider();
        var registry = new CallbackRegistry();
        var completion = new TestResponseCompletionSource();
        var callback = CreateCallback(registry, completion, CreateRequest(7), serviceProvider);
        Assert.True(registry.TryRegister(callback));
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
        Assert.Equal(0, registry.Count);
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
        Assert.True(registry.TryRegister(callback));
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
        Assert.True(registry.TryRegister(first));
        Assert.True(registry.TryRemove(first));

        var replacementCompletion = new TestResponseCompletionSource();
        var replacement = CreateCallback(registry, replacementCompletion, CreateRequest(4), serviceProvider);
        Assert.True(registry.TryRegister(replacement));
        first.OnTimeout();

        Assert.Equal(1, registry.Count);
        Assert.True(registry.TryCompleteResponse(CreateResponse(replacement.Message)));

        Assert.IsType<TimeoutException>(firstCompletion.Response.Exception);
        Assert.Same(Response.Completed, replacementCompletion.Response);
        Assert.Equal(1, firstCompletion.CompletionCount);
        Assert.Equal(1, replacementCompletion.CompletionCount);
        Assert.Equal(0, registry.Count);
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
        Assert.True(registry.TryRegister(callback));
        var response = CreateResponse(callback.Message);

        Parallel.Invoke(
            () => CompleteTerminal(callback, race),
            () => registry.TryCompleteResponse(response));

        Assert.Equal(1, completion.CompletionCount);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void TryCompleteResponse_CancellationRace_CompletesExactlyOnce()
    {
        using var serviceProvider = CreateServiceProvider();
        using var cancellation = new CancellationTokenSource();
        var registry = new CallbackRegistry();
        var completion = new TestResponseCompletionSource();
        var callback = CreateCallback(registry, completion, CreateRequest(6), serviceProvider);
        Assert.True(registry.TryRegister(callback));
        callback.SubscribeForCancellation(cancellation.Token);
        var response = CreateResponse(callback.Message);

        Parallel.Invoke(
            cancellation.Cancel,
            () => registry.TryCompleteResponse(response));

        Assert.Equal(1, completion.CompletionCount);
        Assert.Equal(0, registry.Count);
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
            Assert.True(registry.TryRegister(callbacks[index]));
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
        Assert.Equal(0, registry.Count);
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
            Assert.True(registry.TryRegister(callbacks[index]));
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
        Assert.Equal(0, registry.Count);

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
        Assert.True(registry.TryRegister(first));
        var duplicateRequest = CreateRequest(8);
        duplicateRequest.SendingGrain = GrainId.Create("callback-caller", "2");
        var duplicate = CreateCallback(
            registry,
            new TestResponseCompletionSource(),
            duplicateRequest,
            serviceProvider);

        Assert.Throws<InvalidOperationException>(() => registry.TryRegister(duplicate));

        first.OnHostShutdown();
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void TryRegister_AfterClose_DoesNotPublishCallback()
    {
        using var serviceProvider = CreateServiceProvider();
        var registry = new CallbackRegistry();
        registry.Close();
        var callback = CreateCallback(
            registry,
            new TestResponseCompletionSource(),
            CreateRequest(9),
            serviceProvider);

        Assert.False(registry.TryRegister(callback));

        Assert.Equal(0, registry.Count);
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
    {
        var shared = new SharedCallbackData(
            callback => registry.TryRemove(callback),
            NullLogger<CallbackData>.Instance,
            responseTimeout: TimeSpan.FromMinutes(1),
            cancelOnTimeout: false,
            waitForCancellationAcknowledgement: false,
            cancellationManager: null);
        var instruments = new ApplicationRequestInstruments(
            new OrleansInstruments(serviceProvider.GetRequiredService<IMeterFactory>()));
        return new CallbackData(shared, completion, request, instruments);
    }

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
