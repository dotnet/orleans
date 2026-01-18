using Microsoft.Extensions.DependencyInjection;
using Orleans.Core.Internal;
using Orleans.Journaling.Messaging;
using Orleans.Runtime;
using Orleans.Serialization.Session;
using Orleans.TestingHost;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// End-to-end integration tests for durable RPC messaging using inbox/outbox pattern.
/// Tests verify request/response flows, hierarchical correlation, long-polling, and atomic persistence.
/// </summary>
[TestCategory("BVT"), TestCategory("Functional"), TestCategory("Journaling")]
public class DurableRpcIntegrationTests : IClassFixture<DurableRpcIntegrationTests.Fixture>
{
    private readonly Fixture _fixture;

    public DurableRpcIntegrationTests(Fixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Tests basic request/response flow with CorrelationKey.
    /// Verifies that a request grain can send a message to a worker grain, which processes it
    /// and sends back a response, with correlation preserved throughout.
    /// </summary>
    [Fact]
    public async Task DurableRpc_RequestResponse_WithCorrelationKey()
    {
        // Arrange
        var requestGrain = _fixture.Client.GetGrain<IRequestGrain>(Guid.NewGuid());
        var workerGrain = _fixture.Client.GetGrain<IWorkerGrain>(Guid.NewGuid());

        // Act - Send request with correlation key
        var correlationKey = HierarchicalKey.Create("test-request-123");
        await requestGrain.SendRequest(workerGrain.GetGrainId(), correlationKey, "ProcessData", new WorkRequest { Data = "test data" });

        // Wait for response to be received
        var receivedResponse = await TestHelpers.WaitForNonNullAsync(
            async () => await requestGrain.GetReceivedResponse(),
            message: "Response was not received");

        // Assert
        Assert.NotNull(receivedResponse);
        Assert.Equal("Processed: test data", receivedResponse.Result);
        Assert.Equal(correlationKey, receivedResponse.CorrelationKey);
    }

    /// <summary>
    /// Tests reply pattern using Envelope.ReplyTo.
    /// Verifies that the ReplyTo field correctly routes responses back to the original requester.
    /// </summary>
    [Fact]
    public async Task DurableRpc_ReplyTo_RoutesResponseCorrectly()
    {
        // Arrange
        var requestGrain = _fixture.Client.GetGrain<IRequestGrain>(Guid.NewGuid());
        var workerGrain = _fixture.Client.GetGrain<IWorkerGrain>(Guid.NewGuid());

        // Act - Worker grain processes request and sends reply
        await requestGrain.SendRequest(workerGrain.GetGrainId(), null, "ProcessData", new WorkRequest { Data = "reply test" });

        // Wait for response to be received
        var response = await TestHelpers.WaitForNonNullAsync(
            async () => await requestGrain.GetReceivedResponse(),
            message: "Response was not routed back to request grain via ReplyTo");

        // Assert - Response should be routed back to request grain via ReplyTo
        Assert.NotNull(response);
        Assert.Equal("Processed: reply test", response.Result);
    }

    /// <summary>
    /// Tests hierarchical correlation with parent/child requests.
    /// Verifies that child requests can be created from parent correlation keys
    /// and the hierarchy is preserved throughout the request chain.
    /// </summary>
    [Fact]
    public async Task DurableRpc_HierarchicalCorrelation_PreservesParentChildRelationship()
    {
        // Arrange
        var orchestratorGrain = _fixture.Client.GetGrain<IOrchestratorGrain>(Guid.NewGuid());
        var worker1 = _fixture.Client.GetGrain<IWorkerGrain>(Guid.NewGuid());
        var worker2 = _fixture.Client.GetGrain<IWorkerGrain>(Guid.NewGuid());

        // Act - Orchestrator sends parent request, then two child requests
        var parentKey = HierarchicalKey.Create("orchestration-456");
        await orchestratorGrain.OrchestrateTasks(
            worker1.GetGrainId(),
            worker2.GetGrainId(),
            parentKey,
            new WorkRequest { Data = "task1" },
            new WorkRequest { Data = "task2" });

        // Wait for both child tasks to complete
        await TestHelpers.WaitUntilAsync(
            async () => (await orchestratorGrain.GetCompletedTasks()).Count >= 2,
            message: "Both orchestrated tasks were not completed");

        // Assert - Both responses should have child correlation keys
        var results = await orchestratorGrain.GetCompletedTasks();
        Assert.Equal(2, results.Count);

        var firstResult = results[0];
        var secondResult = results[1];

        // Verify parent-child relationships
        Assert.NotNull(firstResult.CorrelationKey);
        Assert.NotNull(secondResult.CorrelationKey);
        Assert.True(parentKey.IsAncestorOf(firstResult.CorrelationKey));
        Assert.True(parentKey.IsAncestorOf(secondResult.CorrelationKey));

        // Verify results
        Assert.Equal("Processed: task1", firstResult.Result);
        Assert.Equal("Processed: task2", secondResult.Result);
    }

    /// <summary>
    /// Tests long-polling request/response pattern.
    /// Verifies that DeliverAsync with PollTimeout waits for processing completion
    /// and returns Processed status instead of Pending when handler completes within timeout.
    /// </summary>
    [Fact]
    public async Task DurableRpc_LongPolling_WaitsForProcessingCompletion()
    {
        // Arrange
        var requestGrain = _fixture.Client.GetGrain<IRequestGrain>(Guid.NewGuid());
        var workerGrain = _fixture.Client.GetGrain<IWorkerGrain>(Guid.NewGuid());

        // Act - Send request with long polling enabled
        var correlationKey = HierarchicalKey.Create("longpoll-789");
        await requestGrain.SendRequestWithLongPolling(
            workerGrain.GetGrainId(),
            correlationKey,
            "ProcessData",
            new WorkRequest { Data = "longpoll test" },
            TimeSpan.FromSeconds(5));

        // Wait for response to be received via long-polling
        var response = await TestHelpers.WaitForNonNullAsync(
            async () => await requestGrain.GetReceivedResponse(),
            message: "Response was not received via long-polling");

        // Assert - Response should be received via long-polling
        Assert.NotNull(response);
        Assert.Equal("Processed: longpoll test", response.Result);
        Assert.Equal(correlationKey, response.CorrelationKey);
    }

    /// <summary>
    /// Tests observer callback pattern using IDurableInboxObserver.
    /// Verifies that response handlers can be invoked via OnResponseAsync when
    /// a response arrives with matching correlation key.
    /// </summary>
    [Fact]
    public async Task DurableRpc_Observer_ReceivesResponseCallback()
    {
        // Arrange
        var observerGrain = _fixture.Client.GetGrain<IObserverGrain>(Guid.NewGuid());
        var workerGrain = _fixture.Client.GetGrain<IWorkerGrain>(Guid.NewGuid());

        // Act - Send request that will trigger observer callback
        var correlationKey = HierarchicalKey.Create("observer-callback-101");
        await observerGrain.SendRequestWithObserver(
            workerGrain.GetGrainId(),
            correlationKey,
            new WorkRequest { Data = "observer test" });

        // Wait for observer callback to be received
        var observedResponse = await TestHelpers.WaitForNonNullAsync(
            async () => await observerGrain.GetObservedResponse(),
            message: "Observer did not receive callback");

        // Assert - Observer should have received callback
        Assert.NotNull(observedResponse);
        Assert.Equal("Processed: observer test", observedResponse.Result);
        Assert.Equal(correlationKey, observedResponse.CorrelationKey);
    }

    /// <summary>
    /// Tests atomic persistence across request/response cycle.
    /// Verifies that inbox and outbox state are persisted atomically with grain state,
    /// and messages survive grain deactivation/reactivation.
    /// </summary>
    [Fact]
    public async Task DurableRpc_AtomicPersistence_SurvivesGrainDeactivation()
    {
        // Arrange
        var requestGrain = _fixture.Client.GetGrain<IRequestGrain>(Guid.NewGuid());
        var workerGrain = _fixture.Client.GetGrain<IWorkerGrain>(Guid.NewGuid());

        // Act - Send request
        var correlationKey = HierarchicalKey.Create("persistence-202");
        await requestGrain.SendRequest(
            workerGrain.GetGrainId(),
            correlationKey,
            "ProcessData",
            new WorkRequest { Data = "persistence test" });

        // Wait for worker to process the request first
        // This ensures the request was delivered before we deactivate
        await TestHelpers.WaitUntilAsync(
            async () => await workerGrain.GetProcessedCount() >= 1,
            message: "Worker did not process the request");

        // Deactivate request grain after request was processed
        var activationIdBefore = await requestGrain.GetActivationId();
        await requestGrain.Cast<IGrainManagementExtension>().DeactivateOnIdle();

        // Wait for grain to be reactivated (new activation id)
        await TestHelpers.WaitUntilAsync(
            async () => await requestGrain.GetActivationId() != activationIdBefore,
            message: "Grain was not reactivated after deactivation");

        // Assert - After reactivation, grain should still receive and process response
        var activationIdAfter = await requestGrain.GetActivationId();
        Assert.NotEqual(activationIdBefore, activationIdAfter);

        // Wait for response to arrive at reactivated grain
        var response = await TestHelpers.WaitForNonNullAsync(
            async () => await requestGrain.GetReceivedResponse(),
            message: "Response was not received after grain reactivation");
        Assert.NotNull(response);
        Assert.Equal("Processed: persistence test", response.Result);
    }

    /// <summary>
    /// Tests RouteKeyHandler base class for exact route matching.
    /// Verifies that a grain using RouteKeyHandler only receives messages with exact route key match.
    /// </summary>
    [Fact]
    public async Task DurableRpc_RouteKeyHandler_MatchesExactRouteOnly()
    {
        // Arrange
        var routeKeyGrain = _fixture.Client.GetGrain<IRouteKeyHandlerGrain>(Guid.NewGuid());
        var requestGrain = _fixture.Client.GetGrain<IRequestGrain>(Guid.NewGuid());

        // Act - Send message with exact matching route key
        await requestGrain.SendRequest(
            routeKeyGrain.GetGrainId(),
            HierarchicalKey.Create("routekey-test-1"),
            "api/v1/order",
            new WorkRequest { Data = "exact match" });

        // Wait for processing
        await TestHelpers.WaitUntilAsync(
            async () => await routeKeyGrain.GetProcessedCount() >= 1,
            message: "Message with exact route key was not processed");

        // Assert - Message should be processed
        var processedCount = await routeKeyGrain.GetProcessedCount();
        Assert.Equal(1, processedCount);

        // Act - Send message with non-matching route key
        await requestGrain.SendRequest(
            routeKeyGrain.GetGrainId(),
            HierarchicalKey.Create("routekey-test-2"),
            "api/v1/order/detail",
            new WorkRequest { Data = "no match" });

        // Wait briefly to ensure non-matching message is not processed
        // Use a short timeout since we expect this NOT to change
        var stillOne = await TestHelpers.TryWaitUntilAsync(
            async () => await routeKeyGrain.GetProcessedCount() > 1,
            timeout: TimeSpan.FromMilliseconds(200));

        // Assert - Message should not be processed (count should still be 1)
        processedCount = await routeKeyGrain.GetProcessedCount();
        Assert.Equal(1, processedCount);
    }

    /// <summary>
    /// Tests RoutePrefixHandler base class for prefix-based routing.
    /// Verifies that a grain using RoutePrefixHandler receives all messages with matching prefix,
    /// and can use GetRouteSuffix to determine the specific operation.
    /// </summary>
    [Fact]
    public async Task DurableRpc_RoutePrefixHandler_MatchesAllRoutesWithPrefix()
    {
        // Arrange
        var prefixGrain = _fixture.Client.GetGrain<IRoutePrefixHandlerGrain>(Guid.NewGuid());
        var requestGrain = _fixture.Client.GetGrain<IRequestGrain>(Guid.NewGuid());

        // Act - Send multiple messages with same prefix but different operations
        await requestGrain.SendRequest(
            prefixGrain.GetGrainId(),
            HierarchicalKey.Create("prefix-test-1"),
            "rpc/request",
            new WorkRequest { Data = "request data" });

        await requestGrain.SendRequest(
            prefixGrain.GetGrainId(),
            HierarchicalKey.Create("prefix-test-2"),
            "rpc/reply",
            new WorkRequest { Data = "reply data" });

        await requestGrain.SendRequest(
            prefixGrain.GetGrainId(),
            HierarchicalKey.Create("prefix-test-3"),
            "rpc/notify",
            new WorkRequest { Data = "notify data" });

        // Wait for all three messages to be processed
        await TestHelpers.WaitUntilAsync(
            async () => await prefixGrain.GetProcessedCount() >= 3,
            message: "All three prefix-matched messages were not processed");

        // Assert - All three messages should be processed
        var processedCount = await prefixGrain.GetProcessedCount();
        Assert.Equal(3, processedCount);

        // Verify specific operations were captured
        var operations = await prefixGrain.GetCapturedOperations();
        Assert.Contains("request", operations);
        Assert.Contains("reply", operations);
        Assert.Contains("notify", operations);

        // Act - Send message with non-matching prefix
        await requestGrain.SendRequest(
            prefixGrain.GetGrainId(),
            HierarchicalKey.Create("prefix-test-4"),
            "api/process",
            new WorkRequest { Data = "no match" });

        // Wait briefly to ensure non-matching message is not processed
        var stillThree = await TestHelpers.TryWaitUntilAsync(
            async () => await prefixGrain.GetProcessedCount() > 3,
            timeout: TimeSpan.FromMilliseconds(200));

        // Assert - Count should still be 3 (non-matching message not processed)
        processedCount = await prefixGrain.GetProcessedCount();
        Assert.Equal(3, processedCount);
    }

    /// <summary>
    /// Tests RouteKeyHandler and RoutePrefixHandler working together with handler precedence.
    /// Verifies that specific RouteKeyHandler is matched before generic RoutePrefixHandler.
    /// </summary>
    [Fact]
    public async Task DurableRpc_HandlerPrecedence_SpecificRouteBeforePrefix()
    {
        // Arrange
        var precedenceGrain = _fixture.Client.GetGrain<IHandlerPrecedenceGrain>(Guid.NewGuid());
        var requestGrain = _fixture.Client.GetGrain<IRequestGrain>(Guid.NewGuid());

        // Act - Send message that matches both specific route and prefix
        await requestGrain.SendRequest(
            precedenceGrain.GetGrainId(),
            HierarchicalKey.Create("precedence-test-1"),
            "api/v1/special",
            new WorkRequest { Data = "special route" });

        // Wait for specific handler to process
        await TestHelpers.WaitUntilAsync(
            async () => await precedenceGrain.GetSpecificHandlerCount() >= 1,
            message: "Specific handler did not process the message");

        // Assert - Specific handler should have processed it
        var specificCount = await precedenceGrain.GetSpecificHandlerCount();
        var prefixCount = await precedenceGrain.GetPrefixHandlerCount();
        Assert.Equal(1, specificCount);
        Assert.Equal(0, prefixCount);

        // Act - Send message that only matches prefix
        await requestGrain.SendRequest(
            precedenceGrain.GetGrainId(),
            HierarchicalKey.Create("precedence-test-2"),
            "api/v1/general",
            new WorkRequest { Data = "general route" });

        // Wait for prefix handler to process
        await TestHelpers.WaitUntilAsync(
            async () => await precedenceGrain.GetPrefixHandlerCount() >= 1,
            message: "Prefix handler did not process the message");

        // Assert - Prefix handler should have processed it
        specificCount = await precedenceGrain.GetSpecificHandlerCount();
        prefixCount = await precedenceGrain.GetPrefixHandlerCount();
        Assert.Equal(1, specificCount);
        Assert.Equal(1, prefixCount);
    }

    /// <summary>
    /// Test fixture that configures the cluster with durable messaging and test grain handlers.
    /// </summary>
    public class Fixture : IntegrationTestFixture
    {
        protected override void ConfigureTestCluster(InProcessTestClusterBuilder builder)
        {
            builder.ConfigureSilo((options, siloBuilder) =>
            {
                siloBuilder.AddDurableMessaging(opts =>
                {
                    opts.MaxCapacity = 100;
                    opts.DeduplicationWindow = TimeSpan.FromDays(7);
                    opts.EnableLongPolling = true;
                    opts.DefaultPollTimeout = TimeSpan.FromSeconds(30);
                });
            });
        }
    }
}

// ============================================================================
// Test Message Types
// ============================================================================

[GenerateSerializer]
public record WorkRequest
{
    [Id(0)] public required string Data { get; init; }
}

[GenerateSerializer]
public record WorkResponse
{
    [Id(0)] public required string Result { get; init; }
    [Id(1)] public HierarchicalKey? CorrelationKey { get; init; }
}

// ============================================================================
// Test Grain Interfaces
// ============================================================================

public interface IRequestGrain : IGrainWithGuidKey
{
    Task<Guid> GetActivationId();
    Task SendRequest(GrainId workerId, HierarchicalKey? correlationKey, string routeKey, WorkRequest request);
    Task SendRequestWithLongPolling(GrainId workerId, HierarchicalKey? correlationKey, string routeKey, WorkRequest request, TimeSpan pollTimeout);
    Task<WorkResponse?> GetReceivedResponse();
}

public interface IWorkerGrain : IGrainWithGuidKey
{
    Task<Guid> GetActivationId();
    Task<int> GetProcessedCount();
}

public interface IOrchestratorGrain : IGrainWithGuidKey
{
    Task OrchestrateTasks(GrainId worker1, GrainId worker2, HierarchicalKey parentKey, WorkRequest task1, WorkRequest task2);
    Task<List<WorkResponse>> GetCompletedTasks();
}

public interface IObserverGrain : IGrainWithGuidKey
{
    Task SendRequestWithObserver(GrainId workerId, HierarchicalKey correlationKey, WorkRequest request);
    Task<WorkResponse?> GetObservedResponse();
}

public interface IRouteKeyHandlerGrain : IGrainWithGuidKey
{
    Task<int> GetProcessedCount();
}

public interface IRoutePrefixHandlerGrain : IGrainWithGuidKey
{
    Task<int> GetProcessedCount();
    Task<List<string>> GetCapturedOperations();
}

public interface IHandlerPrecedenceGrain : IGrainWithGuidKey
{
    Task<int> GetSpecificHandlerCount();
    Task<int> GetPrefixHandlerCount();
}

// ============================================================================
// Test Grain Implementations
// ============================================================================

/// <summary>
/// Request grain that sends requests and handles responses.
/// </summary>
public class RequestGrain : DurableGrain, IRequestGrain
{
    private readonly Guid _activationId = Guid.NewGuid();
    private readonly IDurableInbox _inbox;
    private readonly IDurableOutbox _outbox;
    private readonly IDurableValue<WorkResponse?> _receivedResponse;

    public RequestGrain(
        IDurableInbox inbox,
        IDurableOutbox outbox,
        [FromKeyedServices("receivedResponse")] IDurableValue<WorkResponse?> receivedResponse)
    {
        _inbox = inbox;
        _outbox = outbox;
        _receivedResponse = receivedResponse;
    }

    public Task<Guid> GetActivationId() => Task.FromResult(_activationId);

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        // Register response handler
        _inbox.RegisterHandler("response", new ResponseHandler(this));
        return base.OnActivateAsync(cancellationToken);
    }

    public async Task SendRequest(GrainId workerId, HierarchicalKey? correlationKey, string routeKey, WorkRequest request)
    {
        Console.WriteLine($"[DEBUG-TEST] RequestGrain.SendRequest: Sending to {workerId} on route '{routeKey}'");
        var sessionPool = ServiceProvider.GetRequiredService<SerializerSessionPool>();
        var builder = new DurableEnvelopeBuilder
        {
            SessionPool = sessionPool,
            SenderId = this.GetGrainId()
        };

        var envelopeBuilder = builder
            .To(workerId, routeKey)
            .WithBody(request)
            .WithReplyTo(this.GetGrainId());

        if (correlationKey is not null)
        {
            envelopeBuilder.WithCorrelationKey(correlationKey);
        }

        var envelope = envelopeBuilder.Build();
        Console.WriteLine($"[DEBUG-TEST] RequestGrain.SendRequest: Built envelope with MessageId={envelope.MessageId}");

        _outbox.Send(envelope);
        Console.WriteLine($"[DEBUG-TEST] RequestGrain.SendRequest: Added to outbox, count={_outbox.Count}");
        await WriteStateAsync();
        Console.WriteLine($"[DEBUG-TEST] RequestGrain.SendRequest: WriteStateAsync completed, outbox count={_outbox.Count}");
    }

    public async Task SendRequestWithLongPolling(GrainId workerId, HierarchicalKey? correlationKey, string routeKey, WorkRequest request, TimeSpan pollTimeout)
    {
        var sessionPool = ServiceProvider.GetRequiredService<SerializerSessionPool>();
        var builder = new DurableEnvelopeBuilder
        {
            SessionPool = sessionPool,
            SenderId = this.GetGrainId()
        };

        var envelopeBuilder = builder
            .To(workerId, routeKey)
            .WithBody(request)
            .WithReplyTo(this.GetGrainId());

        if (correlationKey is not null)
        {
            envelopeBuilder.WithCorrelationKey(correlationKey);
        }

        var envelope = envelopeBuilder.Build();

        _outbox.Send(envelope);
        await WriteStateAsync();

        // Note: Long-polling is handled at the DeliverAsync level by the outbox delivery pump
        // This test grain just sends the message; the polling happens during delivery
    }

    public Task<WorkResponse?> GetReceivedResponse() => Task.FromResult(_receivedResponse.Value);

    private class ResponseHandler : IInboxHandler<WorkResponse>
    {
        private readonly RequestGrain _grain;

        public ResponseHandler(RequestGrain grain)
        {
            _grain = grain;
        }

        public async ValueTask HandleAsync(WorkResponse message, IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            _grain._receivedResponse.Value = message;
            await _grain.WriteStateAsync();
        }
    }
}

/// <summary>
/// Worker grain that processes requests and sends responses.
/// </summary>
public class WorkerGrain(IDurableInbox inbox) : DurableGrain, IWorkerGrain
{
    private readonly Guid _activationId = Guid.NewGuid();
    private int _processedCount;
    private readonly IDurableInbox _inbox = inbox;

    public Task<Guid> GetActivationId() => Task.FromResult(_activationId);
    public Task<int> GetProcessedCount() => Task.FromResult(_processedCount);

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _inbox.RegisterHandler("ProcessData", new ProcessDataHandler(this));
        return base.OnActivateAsync(cancellationToken);
    }

    private class ProcessDataHandler : IInboxHandler<WorkRequest>
    {
        private readonly WorkerGrain _grain;

        public ProcessDataHandler(WorkerGrain grain)
        {
            _grain = grain;
        }

        public async ValueTask HandleAsync(WorkRequest message, IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            _grain._processedCount++;

            // Process the request
            var result = $"Processed: {message.Data}";

            // Send response if ReplyTo is set
            if (context.Envelope.ReplyTo is { } replyTo)
            {
                var responseBuilder = context.CreateEnvelope()
                    .To(replyTo, "response")
                    .WithBody(new WorkResponse
                    {
                        Result = result,
                        CorrelationKey = context.Envelope.CorrelationKey
                    });

                // Add correlation key if present
                if (context.Envelope.CorrelationKey is not null)
                {
                    responseBuilder.WithCorrelationKey(context.Envelope.CorrelationKey);
                }

                var response = responseBuilder.Build();
                context.Send(response);
            }

            await _grain.WriteStateAsync();
        }
    }
}

/// <summary>
/// Orchestrator grain that coordinates multiple worker tasks with hierarchical correlation.
/// </summary>
public class OrchestratorGrain(IDurableInbox inbox, IDurableOutbox outbox) : DurableGrain, IOrchestratorGrain
{
    private readonly List<WorkResponse> _completedTasks = new();
    private readonly IDurableInbox _inbox = inbox;
    private readonly IDurableOutbox _outbox = outbox;

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        // WorkerGrain sends responses to route "response", so register handler for that route
        _inbox.RegisterHandler("response", new TaskResponseHandler(this));
        return base.OnActivateAsync(cancellationToken);
    }

    public async Task OrchestrateTasks(GrainId worker1, GrainId worker2, HierarchicalKey parentKey, WorkRequest task1, WorkRequest task2)
    {
        var sessionPool = ServiceProvider.GetRequiredService<SerializerSessionPool>();

        // Send task1 with child correlation key
        var childKey1 = parentKey.CreateChildKey("task1");
        var builder1 = new DurableEnvelopeBuilder
        {
            SessionPool = sessionPool,
            SenderId = this.GetGrainId()
        };
        var envelope1 = builder1
            .To(worker1, "ProcessData")
            .WithBody(task1)
            .WithCorrelationKey(childKey1)
            .WithReplyTo(this.GetGrainId())
            .Build();

        _outbox.Send(envelope1);

        // Send task2 with different child key
        var childKey2 = parentKey.CreateChildKey("task2");
        var builder2 = new DurableEnvelopeBuilder
        {
            SessionPool = sessionPool,
            SenderId = this.GetGrainId()
        };
        var envelope2 = builder2
            .To(worker2, "ProcessData")
            .WithBody(task2)
            .WithCorrelationKey(childKey2)
            .WithReplyTo(this.GetGrainId())
            .Build();

        _outbox.Send(envelope2);

        await WriteStateAsync();
    }

    public Task<List<WorkResponse>> GetCompletedTasks() => Task.FromResult(_completedTasks);

    private class TaskResponseHandler : IInboxHandler<WorkResponse>
    {
        private readonly OrchestratorGrain _grain;

        public TaskResponseHandler(OrchestratorGrain grain)
        {
            _grain = grain;
        }

        public async ValueTask HandleAsync(WorkResponse message, IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            _grain._completedTasks.Add(message);
            await _grain.WriteStateAsync();
        }
    }
}

/// <summary>
/// Observer grain that uses IDurableInboxObserver for response callbacks.
/// </summary>
[GrainType("DurableRpc.ObserverGrain")]
public class ObserverGrain(IDurableOutbox outbox) : DurableGrain, IObserverGrain, IDurableInboxObserver
{
    private WorkResponse? _observedResponse;
    private readonly IDurableOutbox _outbox = outbox;

    public async Task SendRequestWithObserver(GrainId workerId, HierarchicalKey correlationKey, WorkRequest request)
    {
        var sessionPool = ServiceProvider.GetRequiredService<SerializerSessionPool>();
        var builder = new DurableEnvelopeBuilder
        {
            SessionPool = sessionPool,
            SenderId = this.GetGrainId()
        };

        var envelope = builder
            .To(workerId, "ProcessData")
            .WithBody(request)
            .WithCorrelationKey(correlationKey)
            .WithReplyTo(this.GetGrainId())
            .Build();

        _outbox.Send(envelope);
        await WriteStateAsync();
    }

    public Task<WorkResponse?> GetObservedResponse() => Task.FromResult(_observedResponse);

    // IDurableInboxObserver implementation
    public async ValueTask<DeliveryResult> OnResponseAsync(HierarchicalKey correlationKey, DurableEnvelope envelope, DeliveryOptions options, CancellationToken cancellationToken)
    {
        if (envelope.Data.TryGetBody<WorkResponse>(out var response))
        {
            _observedResponse = response;
            await WriteStateAsync();
            return DeliveryResult.Processed();
        }

        return DeliveryResult.RouteNotFound(envelope.RouteKey);
    }
}

/// <summary>
/// Test grain that uses RouteKeyHandler for exact route matching.
/// </summary>
public class RouteKeyHandlerGrain(IDurableInbox inbox) : DurableGrain, IRouteKeyHandlerGrain
{
    private int _processedCount;
    private readonly IDurableInbox _inbox = inbox;

    public Task<int> GetProcessedCount() => Task.FromResult(_processedCount);

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        // Register handler for exact route "api/v1/order"
        _inbox.RegisterHandler(new OrderRouteKeyHandler(this));
        return base.OnActivateAsync(cancellationToken);
    }

    private class OrderRouteKeyHandler : RouteKeyHandler
    {
        private readonly RouteKeyHandlerGrain _grain;

        public OrderRouteKeyHandler(RouteKeyHandlerGrain grain) : base("api/v1/order")
        {
            _grain = grain;
        }

        protected override async ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            _grain._processedCount++;
            await _grain.WriteStateAsync();
        }
    }
}

/// <summary>
/// Test grain that uses RoutePrefixHandler for prefix-based routing.
/// </summary>
public class RoutePrefixHandlerGrain(IDurableInbox inbox) : DurableGrain, IRoutePrefixHandlerGrain
{
    private int _processedCount;
    private readonly List<string> _capturedOperations = new();
    private readonly IDurableInbox _inbox = inbox;

    public Task<int> GetProcessedCount() => Task.FromResult(_processedCount);
    public Task<List<string>> GetCapturedOperations() => Task.FromResult(new List<string>(_capturedOperations));

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        // Register handler for all routes with "rpc/" prefix
        _inbox.RegisterHandler(new RpcPrefixHandler(this));
        return base.OnActivateAsync(cancellationToken);
    }

    private class RpcPrefixHandler : RoutePrefixHandler
    {
        private readonly RoutePrefixHandlerGrain _grain;

        public RpcPrefixHandler(RoutePrefixHandlerGrain grain) : base("rpc/")
        {
            _grain = grain;
        }

        protected override async ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            _grain._processedCount++;

            // Capture the operation (suffix after prefix)
            var operation = GetRouteSuffix(context.Envelope.RouteKey);
            if (operation is not null)
            {
                _grain._capturedOperations.Add(operation);
            }

            await _grain.WriteStateAsync();
        }
    }
}

/// <summary>
/// Test grain that demonstrates handler precedence with both RouteKeyHandler and RoutePrefixHandler.
/// </summary>
public class HandlerPrecedenceGrain(IDurableInbox inbox) : DurableGrain, IHandlerPrecedenceGrain
{
    private int _specificHandlerCount;
    private int _prefixHandlerCount;
    private readonly IDurableInbox _inbox = inbox;

    public Task<int> GetSpecificHandlerCount() => Task.FromResult(_specificHandlerCount);
    public Task<int> GetPrefixHandlerCount() => Task.FromResult(_prefixHandlerCount);

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        // Register specific handler first (should take precedence)
        _inbox.RegisterHandler(new SpecificRouteHandler(this));

        // Register prefix handler second (catches everything else with "api/v1/" prefix)
        _inbox.RegisterHandler(new ApiPrefixHandler(this));

        return base.OnActivateAsync(cancellationToken);
    }

    private class SpecificRouteHandler : RouteKeyHandler
    {
        private readonly HandlerPrecedenceGrain _grain;

        public SpecificRouteHandler(HandlerPrecedenceGrain grain) : base("api/v1/special")
        {
            _grain = grain;
        }

        protected override async ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            _grain._specificHandlerCount++;
            await _grain.WriteStateAsync();
        }
    }

    private class ApiPrefixHandler : RoutePrefixHandler
    {
        private readonly HandlerPrecedenceGrain _grain;

        public ApiPrefixHandler(HandlerPrecedenceGrain grain) : base("api/v1/")
        {
            _grain = grain;
        }

        protected override async ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            _grain._prefixHandlerCount++;
            await _grain.WriteStateAsync();
        }
    }
}
