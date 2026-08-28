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
    public void TryCompleteResponse_DirectTarget_CompletesExactCallbackAndCleansFallback()
    {
        using var serviceProvider = CreateServiceProvider();
        var registry = new CallbackRegistry();
        var completion = new TestResponseCompletionSource();
        var callback = CreateCallback(registry, completion, CreateRequest(1), serviceProvider);
        Assert.True(registry.TryAdd(callback));
        var response = CreateResponse(callback.Message, callback);

        Assert.True(registry.TryCompleteResponse(response));

        Assert.Same(Response.Completed, completion.Response);
        Assert.Equal(1, completion.CompletionCount);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void TryCompleteResponse_SerializedResponse_UsesFallbackLookup()
    {
        using var serviceProvider = CreateServiceProvider();
        var registry = new CallbackRegistry();
        var completion = new TestResponseCompletionSource();
        var callback = CreateCallback(registry, completion, CreateRequest(2), serviceProvider);
        Assert.True(registry.TryAdd(callback));
        var response = CreateResponse(callback.Message, responseTarget: null);

        Assert.True(registry.TryCompleteResponse(response));

        Assert.Same(Response.Completed, completion.Response);
        Assert.Equal(1, completion.CompletionCount);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void TryCompleteResponse_DirectRejection_CompletesExactCallback()
    {
        using var serviceProvider = CreateServiceProvider();
        var registry = new CallbackRegistry();
        var completion = new TestResponseCompletionSource();
        var callback = CreateCallback(registry, completion, CreateRequest(7), serviceProvider);
        Assert.True(registry.TryAdd(callback));
        var response = CreateResponse(callback.Message, callback);
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
    public void TryGetResponseCallback_DirectStatus_UsesExactCallback()
    {
        using var serviceProvider = CreateServiceProvider();
        var registry = new CallbackRegistry();
        var callback = CreateCallback(
            registry,
            new TestResponseCompletionSource(),
            CreateRequest(3),
            serviceProvider);
        Assert.True(registry.TryAdd(callback));
        var status = CreateResponse(callback.Message, callback);
        status.Result = Message.ResponseTypes.Status;
        status.BodyObject = new StatusResponse(true, false, []);

        Assert.True(registry.TryGetResponseCallback(status, out var result));

        Assert.Same(callback, result);
        callback.OnHostShutdown();
    }

    [Fact]
    public void TryCompleteResponse_StaleDirectTarget_DoesNotRemoveReplacement()
    {
        using var serviceProvider = CreateServiceProvider();
        var registry = new CallbackRegistry();
        var firstCompletion = new TestResponseCompletionSource();
        var first = CreateCallback(registry, firstCompletion, CreateRequest(4), serviceProvider);
        Assert.True(registry.TryAdd(first));
        first.OnTimeout();

        var replacementCompletion = new TestResponseCompletionSource();
        var replacement = CreateCallback(registry, replacementCompletion, CreateRequest(4), serviceProvider);
        Assert.True(registry.TryAdd(replacement));
        var staleResponse = CreateResponse(first.Message, first);

        Assert.False(registry.TryCompleteResponse(staleResponse));
        Assert.Equal(1, registry.Count);
        Assert.True(registry.TryCompleteResponse(CreateResponse(replacement.Message, responseTarget: null)));

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
        Assert.True(registry.TryAdd(callback));
        var response = CreateResponse(callback.Message, callback);

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
        Assert.True(registry.TryAdd(callback));
        callback.SubscribeForCancellation(cancellation.Token);
        var response = CreateResponse(callback.Message, callback);

        Parallel.Invoke(
            cancellation.Cancel,
            () => registry.TryCompleteResponse(response));

        Assert.Equal(1, completion.CompletionCount);
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

    private static Message CreateResponse(Message request, CallbackData? responseTarget) => new()
    {
        Direction = Message.Directions.Response,
        Result = Message.ResponseTypes.Success,
        Id = request.Id,
        TargetGrain = request.SendingGrain,
        SendingGrain = request.TargetGrain,
        BodyObject = Response.Completed,
        ResponseTarget = responseTarget,
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
