using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Http;
using Orleans.DurableTasks.Protocol;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.TestingHost;
using Xunit;

namespace Orleans.DurableTasks.Http.Tests;

/// <summary>
/// Hosts an in-process cluster which runs durable tasks, and an ASP.NET Core test server which exposes them as HTTP
/// operations. Silos call the test server through an HTTP client which adds durable idempotency keys.
/// </summary>
public sealed class DurableHttpFixture : IAsyncLifetime
{
    public const string TestUserHeader = "X-Test-User";
    public const string ForbiddenUser = "mallory";
    public const string ApprovalRootId = "approval";

    private readonly InProcessTestCluster _cluster;
    private TestServer? _server;
    private WebApplication? _app;

    public DurableHttpFixture()
    {
        // The silos share one journal store, so a grain which reactivates on another silo recovers its state.
        var journals = new VolatileJournalStorageProvider(Options.Create(new JournaledStateManagerOptions()));
        var builder = new InProcessTestClusterBuilder();
        builder.ConfigureClient(clientBuilder => clientBuilder.AddDurableTasks());
        builder.ConfigureSilo((_, siloBuilder) =>
        {
            siloBuilder.AddJournaling();
            siloBuilder.Services.AddSingleton<IJournalStorageProvider>(journals);
            siloBuilder.Services.AddSingleton<IJournalStorageCatalog>(journals);
            siloBuilder.UseInMemoryDurableJobs();
            siloBuilder.AddDurableTasks();
            siloBuilder.AddIncomingGrainCallFilter<StartFailureFilter>();
        });
        _cluster = builder.Build();
    }

    public HttpClient Http { get; private set; } = null!;

    public IClusterClient Client => _cluster.Client;

    public TestServer Server => _server ?? throw new InvalidOperationException("The test server has not started.");

    /// <summary>Gets the test server's endpoints.</summary>
    public IReadOnlyList<Microsoft.AspNetCore.Http.Endpoint> Endpoints =>
        _app!.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints;

    public async ValueTask InitializeAsync()
    {
        await _cluster.DeployAsync();

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IGrainFactory>(_cluster.Client);
        builder.Services.AddDurableHttp(ConfigureOperations);

        _app = builder.Build();

        // Authenticates the user which a request names, so tests exercise per-user operation scopes and authorization.
        _app.Use((context, next) =>
        {
            if (context.Request.Headers[TestUserHeader] is [{ } user])
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user)], authenticationType: "Test"));
            }

            return next(context);
        });
        // Each order's group starts operations which run its grain's durable methods. An endpoint filter authorizes every
        // request of the group, including those which observe, cancel, or signal its operations: 'mallory' may access no order.
        var orders = _app.MapGroup("/orders/{orderId}")
            .WithDurableOperations()
            .AddEndpointFilter(static (context, next) =>
                context.HttpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value == ForbiddenUser
                    ? ValueTask.FromResult<object?>(Results.Problem(statusCode: StatusCodes.Status403Forbidden))
                    : next(context));
        orders.MapPost("/echo", static (string orderId, EchoRequest request, IGrainFactory grains) =>
            grains.GetGrain<IDurableHttpTestGrain>(orderId).EchoAsync(request.Value));
        orders.MapPost("/approval", static (string orderId, ApprovalRequest request, IGrainFactory grains) =>
            grains.GetGrain<IDurableHttpTestGrain>(orderId).WaitForApprovalAsync(request.Item));
        orders.MapPost("/echo-later", static (string orderId, EchoRequest request, IGrainFactory grains) =>
            grains.GetGrain<IDurableHttpTestGrain>(orderId).EchoLaterAsync(request.Value));
        orders.MapPost("/complete-later", static (string orderId, EchoRequest request, IGrainFactory grains) =>
            grains.GetGrain<IDurableHttpTestGrain>(orderId).CompleteLaterAsync(request.Value));
        orders.MapPost("/fail-later", static (string orderId, EchoRequest request, IGrainFactory grains) =>
            grains.GetGrain<IDurableHttpTestGrain>(orderId).FailLaterAsync(request.Value));

        // Subscribes to the order's approval, which another caller started, rather than starting work.
        orders.MapPost("/approval/watch", static (string orderId, IGrainFactory grains) =>
            grains.GetGrain<IDurableHttpTestGrain>(orderId).GetDurableTask<string>(ApprovalRootId));
        orders.MapGet("/summary", static (string orderId) => $"order {orderId}");
        orders.MapPost("/shipment", static (string orderId, ApprovalRequest request, IGrainFactory grains) =>
            grains.GetGrain<IDurableHttpTestGrain>(orderId).WaitForShipmentAsync(request.Item));
        orders.MapDurableSignal(DurableHttpTestGrain.Approved);
        orders.MapDurableSignal(DurableHttpTestGrain.Shipped);

        await _app.StartAsync();
        _server = _app.GetTestServer();
        Http = _app.GetTestClient();
    }

    private static void ConfigureOperations(DurableHttpOptions options)
    {
        // The test grain's exceptions describe outcomes for clients.
        options.CreateError = static exception => exception switch
        {
            OperationCanceledException => new("Canceled", exception.Message),
            InvalidOperationException => new(nameof(InvalidOperationException), exception.Message),
            _ => DurableHttpOptions.DefaultCreateError(exception),
        };

        // Scopes the operations of authenticated users to the user.
        options.MapOperationId = static (context, operationId) =>
            context.User.FindFirst(ClaimTypes.NameIdentifier) is { } user ? $"{user.Value}:{operationId}" : operationId;
    }

    public async ValueTask DisposeAsync()
    {
        Http?.Dispose();
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

        await _cluster.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class DurableHttpCollection : ICollectionFixture<DurableHttpFixture>
{
    public const string Name = "DurableHttp";
}

/// <summary>
/// Fails the starts of operations whose keys contain <c>rejected-</c> or <c>undelivered-</c>, as an operation host which
/// rejects an invocation or doesn't receive it fails <c>ScheduleAsync</c>.
/// </summary>
public sealed class StartFailureFilter : IIncomingGrainCallFilter
{
    public Task Invoke(IIncomingGrainCallContext context)
    {
        if (context.InterfaceMethod.Name == nameof(IDurableTaskServer.ScheduleAsync)
            && context.TargetId.Key.ToString() is var key
            && context.Request.GetArgument(0) is TaskId taskId)
        {
            if (key.Contains("rejected-", StringComparison.Ordinal))
            {
                throw new DurableTaskRejectedException(taskId, context.TargetId, "test", "The order is closed.");
            }

            if (key.Contains("undelivered-", StringComparison.Ordinal))
            {
                throw new DurableTaskDeliveryException(taskId, context.TargetId, "The outbox retry timeout elapsed.");
            }
        }

        return context.Invoke();
    }
}
