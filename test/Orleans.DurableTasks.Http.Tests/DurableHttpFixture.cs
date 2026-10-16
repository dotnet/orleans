using System.Collections.Concurrent;
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
    public const string ApprovalsScope = "approvals";
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
            siloBuilder.Services.AddDurableHttpCallbacks(static options => options.InitialRetryInterval = TimeSpan.FromMilliseconds(100))
                .ConfigurePrimaryHttpMessageHandler(() => new RedirectingHandler(Server.CreateHandler()))
                .ConfigureHttpClient(static client => client.Timeout = TimeSpan.FromMilliseconds(500));
            siloBuilder.Services.Configure<DurableHttpOptions>(ConfigureOperations);
            siloBuilder.Services.AddSingleton<IDurableWebhookSigner, TestWebhookSigner>();
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
        // Receives callbacks, which tests await through Callbacks.
        _app.MapPost("/callbacks/{name}", static async (string name, HttpRequest request) =>
            await Callbacks.ReceiveAsync(name, request));

        // Approvals through Nexus, whose request identifiers name operations of the 'approvals' scope, which the
        // '/approvals' group also serves, so its routes stream and signal them.
        var nexus = _app.MapGroup("/nexus").MapNexusService("orders.v1", scope: ApprovalsScope);
        nexus.MapNexusOperation("approval", static (NexusApprovalInput input, IGrainFactory grains) =>
            grains.GetGrain<IDurableHttpTestGrain>(input.OrderId).WaitForApprovalAsync(input.Item));
        _app.MapGroup("/approvals").WithDurableOperations(ApprovalsScope).MapDurableSignal(DurableHttpTestGrain.Approved);

        await _app.StartAsync();
        _server = _app.GetTestServer();
        Http = _app.GetTestClient();
    }

    /// <summary>Gets the callbacks which the test server received.</summary>
    public static CallbackReceiver Callbacks { get; } = new();

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

        // Accepts callbacks to the test server's receiver only.
        options.AllowCallback = static (_, url) => url.Host == "localhost" && url.AbsolutePath.StartsWith("/callbacks/", StringComparison.Ordinal);
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

/// <summary>Signs webhooks with a value which tests recompute.</summary>
public sealed class TestWebhookSigner : IDurableWebhookSigner
{
    public static string Expected(string webhookId, long timestamp, int bodyLength) => $"v1,test:{webhookId}:{timestamp}:{bodyLength}";

    public string? Sign(DurableCallback callback, string webhookId, long timestamp, ReadOnlySpan<byte> body) =>
        Expected(webhookId, timestamp, body.Length);
}

/// <summary>Records the callbacks which the test server receives, and lets tests await them by name.</summary>
/// <remarks>
/// A receiver whose name starts with <c>flaky-</c> answers its first attempt with <c>503 Service Unavailable</c>, one
/// whose name starts with <c>slow-</c> answers its first attempt after the sender's timeout, and one whose name starts with
/// <c>moved-</c> redirects its first attempt to another receiver; the sender retries each.
/// </remarks>
public sealed class CallbackReceiver
{
    private readonly ConcurrentDictionary<string, Receiver> _receivers = new();

    /// <summary>Returns a task which completes with the first callback which <paramref name="name"/> accepts.</summary>
    /// <remarks>Arm the wait before triggering the callback.</remarks>
    public Task<ReceivedCallback> ExpectAsync(string name) => _receivers.GetOrAdd(name, static _ => new()).Accepted.Task;

    /// <summary>Returns how many attempts <paramref name="name"/> has received.</summary>
    public int GetAttempts(string name) => _receivers.TryGetValue(name, out var receiver) ? receiver.Attempts : 0;

    internal async Task<IResult> ReceiveAsync(string name, HttpRequest request)
    {
        var receiver = _receivers.GetOrAdd(name, static _ => new());
        if (Interlocked.Increment(ref receiver.Attempts) == 1)
        {
            if (name.StartsWith("flaky-", StringComparison.Ordinal))
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            if (name.StartsWith("slow-", StringComparison.Ordinal))
            {
                // Outlasts the callback client's timeout; the request's abort ends the wait.
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, request.HttpContext.RequestAborted).ContinueWith(static _ => { });
                return Results.Empty;
            }

            if (name.StartsWith("moved-", StringComparison.Ordinal))
            {
                return Results.Redirect($"/callbacks/redirected-{name}", permanent: false, preserveMethod: true);
            }
        }

        using var reader = new StreamReader(request.Body);
        var body = await reader.ReadToEndAsync();
        var headers = request.Headers.ToDictionary(static header => header.Key, static header => header.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        receiver.Accepted.TrySetResult(new(headers, body));
        return Results.Ok();
    }

    private sealed class Receiver
    {
        public readonly TaskCompletionSource<ReceivedCallback> Accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Attempts;
    }
}

public sealed record ReceivedCallback(IReadOnlyDictionary<string, string> Headers, string Body);

/// <summary>
/// Follows redirects like an <see cref="HttpClientHandler"/> with <see cref="HttpClientHandler.AllowAutoRedirect"/>, over
/// the test server, which doesn't follow them, so tests observe whether callback delivery disables them.
/// </summary>
public sealed class RedirectingHandler(HttpMessageHandler inner) : HttpClientHandler
{
    private readonly HttpMessageInvoker _inner = new(inner);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await _inner.SendAsync(request, cancellationToken);
        if (AllowAutoRedirect && response.Headers.Location is { } location && (int)response.StatusCode is 307 or 308)
        {
            using var redirected = new HttpRequestMessage(request.Method, new Uri(request.RequestUri!, location)) { Content = request.Content };
            response.Dispose();
            return await _inner.SendAsync(redirected, cancellationToken);
        }

        return response;
    }
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
