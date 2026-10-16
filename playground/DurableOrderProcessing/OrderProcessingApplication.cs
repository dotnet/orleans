using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Orleans.Core.Internal;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Http;
using Orleans.DurableTasks.Protocol;
using Orleans.Journaling;
using Orleans.Journaling.Json;
using DurableOrderProcessing.Grains;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace DurableOrderProcessing;

// Composes the playground: the silo's durable tasks and payment client, and the HTTP API which serves them.
public static class OrderProcessingApplication
{
    // The scope of order operations, which POST /api/orders starts and the Nexus service orders.v1 shares, so an order's
    // operation key is orders/{orderId} through either protocol.
    public const string OrdersScope = "orders";

    // A request with this header is handled normally, then its connection is reset instead of sending the response, as
    // a network failure after the server handled the request would.
    public const string SimulateLostResponseHeader = "Simulate-Lost-Response";

    // Configures a silo which runs the order workflows, and the client with which they call the payment gateway.
    public static ISiloBuilder AddOrderProcessing(this ISiloBuilder silo, Action<IHttpClientBuilder> configurePayments)
    {
        silo.UseInMemoryDurableJobs()
            .AddVolatileJournalStorage()
            .UseJsonJournalFormat(DurableOrderProcessingJsonContext.Default)
            .AddDurableTasks();

        // Requests of the payment client carry Idempotency-Keys which derive from their steps. The resilience handler
        // follows the key handler, so its retries carry the same key, which the gateway deduplicates.
        var payments = silo.Services.AddDurableHttpClient(OrderGrain.PaymentsClient);
        payments.AddStandardResilienceHandler(options => options.Retry.Delay = TimeSpan.FromMilliseconds(200));
        configurePayments(payments);

        // Delivers the webhooks which order starts register with an Operation-Callback header.
        silo.Services.Configure<DurableHttpOptions>(ConfigureOperations);
        silo.Services.AddDurableHttpCallbacks();
        return silo;
    }

    // Registers the services of the HTTP API, including the simulated payment gateway.
    public static IServiceCollection AddOrderProcessingApi(this IServiceCollection services)
    {
        services.AddDurableHttp(ConfigureOperations);
        services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<PaymentGateway>();
        return services;
    }

    public static void ConfigureOperations(DurableHttpOptions options)
    {
        // Every order start names its order, so a retry reaches the order which the first attempt placed.
        options.RequireIdempotencyKey = true;

        // A workflow which rejects its request, such as an order of an empty basket, describes why.
        options.CreateError = static exception => exception is InvalidOperationException
            ? new("Rejected", exception.Message)
            : DurableHttpOptions.DefaultCreateError(exception);

        // Accepts webhooks to this application's receiver only.
        options.AllowCallback = static (_, url) => url.IsLoopback && url.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.Ordinal);
    }

    public static WebApplication MapOrderProcessing(this WebApplication app)
    {
        app.Use(SimulateLostResponseAsync);

        var api = app.MapGroup("/api");

        api.MapGet("/catalog", async (IGrainFactory grains) =>
            await Task.WhenAll(CatalogData.Items.Select(item => grains.GetGrain<ICatalogItemGrain>(item.Id).GetAsync())));

        api.MapGet("/buyers/{buyerId}/basket", (string buyerId, IGrainFactory grains) =>
            grains.GetGrain<IBasketGrain>(buyerId).GetAsync());

        // Sets a line's quantity. A PUT of an absolute quantity is idempotent, so a repeated or retried update needs no key.
        api.MapPut("/buyers/{buyerId}/basket/{productId:int}", async (string buyerId, int productId, BasketQuantity body, IGrainFactory grains) =>
            CatalogData.Find(productId) is null
                ? Results.NotFound()
                : Results.Ok(await grains.GetGrain<IBasketGrain>(buyerId).SetQuantityAsync(productId, body.Quantity)));

        api.MapGet("/buyers/{buyerId}/orders", (string buyerId, IGrainFactory grains) =>
            grains.GetGrain<IBuyerGrain>(buyerId).GetOrdersAsync());

        // POST /api/orders checks out the buyer's basket as the order which its Idempotency-Key names, and the group
        // serves each order's operation at /api/orders/operations/{orderId}: its status monitor, with long polling, its
        // events, its cancellation, and its shipped signal.
        var orders = api.MapGroup("/orders").WithDurableOperations(OrdersScope);
        orders.MapPost("/", (CheckoutRequest request, [FromHeader(Name = DurableHttpHeaders.IdempotencyKey)] Guid orderId, IGrainFactory grains) =>
                grains.GetGrain<IOrderGrain>(orderId).PlaceOrderAsync(ToPlaceOrder(request)))
            .AddEndpointFilter(ValidateCheckout(DurableHttpHeaders.IdempotencyKey));
        orders.MapDurableSignal(OrderGrain.Shipped);

        orders.MapGet("/{orderId:guid}", async (Guid orderId, IGrainFactory grains) =>
            await grains.GetGrain<IOrderGrain>(orderId).GetAsync() is { } order ? Results.Ok(order) : Results.NotFound());

        // Streams the order's history as server-sent events: each committed snapshot, from submission until the order
        // ships or is cancelled. The stream waits for the first snapshot, so a client can watch an order as soon as its
        // checkout is accepted.
        orders.MapGet("/{orderId:guid}/events", (Guid orderId, IGrainFactory grains, HttpContext context) =>
            StreamHistoryAsync(context, grains.GetGrain<IOrderGrain>(orderId)));

        // Lists the durable tasks which a grain retains, for diagnostics.
        orders.MapGet("/{orderId:guid}/tasks", (Guid orderId, IGrainFactory grains) => ListTasksAsync(grains.GetGrain<IOrderGrain>(orderId)));
        api.MapGet("/buyers/{buyerId}/tasks", (string buyerId, IGrainFactory grains) => ListTasksAsync(grains.GetGrain<IBuyerGrain>(buyerId)));
        api.MapGet("/catalog/{productId:int}/tasks", (int productId, IGrainFactory grains) => ListTasksAsync(grains.GetGrain<ICatalogItemGrain>(productId)));

        // Deactivates an order, for observing recovery: the next activation replays its pending tasks.
        orders.MapPost("/{orderId:guid}/deactivate", async (Guid orderId, IGrainFactory grains) =>
            await grains.GetGrain<IOrderGrain>(orderId).AsReference<IGrainManagementExtension>().DeactivateOnIdle());

        // Receives the webhooks which order starts register with an Operation-Callback header.
        api.MapPost("/webhooks/orders", (DurableOperation operation, ILoggerFactory loggers) =>
        {
            loggers.CreateLogger("DurableOrderProcessing.Webhooks").LogInformation(
                "Order {OrderId} is {Status}: {Outcome}", operation.Id, operation.Status, (object?)operation.Result ?? operation.Error);
            return Results.Ok();
        });

        // Temporal workflows place orders through the Nexus service orders.v1. The Nexus request ID is the order's ID, and
        // the service shares the orders scope, so its operations are the orders which /api/orders/operations serves.
        app.MapGroup("/nexus")
            .MapNexusService("orders.v1", scope: OrdersScope)
            .MapNexusOperation("place-order", (CheckoutRequest input, [FromHeader(Name = NexusRequestIdHeader)] Guid orderId, IGrainFactory grains) =>
                grains.GetGrain<IOrderGrain>(orderId).PlaceOrderAsync(ToPlaceOrder(input)))
            .AddEndpointFilter(ValidateCheckout(NexusRequestIdHeader));

        app.Services.GetRequiredService<PaymentGateway>().Map(app);
        return app;
    }

    private const string NexusRequestIdHeader = "Nexus-Request-Id";

    private static PlaceOrder ToPlaceOrder(CheckoutRequest request) => new(request.BuyerId, request.Address, request.CardNumber[^4..]);

    // Validates a checkout before its operation starts. The header which names the order is its ID in canonical form, a
    // lowercase UUID, so every request for the order names the same operation.
    private static Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>> ValidateCheckout(string header) =>
        (context, next) =>
        {
            if (context.Arguments.OfType<CheckoutRequest>().FirstOrDefault() is not { } request)
            {
                return next(context);
            }

            var errors = new Dictionary<string, string[]>();
            if (context.Arguments.OfType<Guid>().Single() is var orderId
                && context.HttpContext.Request.Headers[header].ToString() != orderId.ToString())
            {
                errors[header] = [$"The {header} of an order is its ID, a lowercase UUID such as '{Guid.Empty}'."];
            }

            if (string.IsNullOrWhiteSpace(request.BuyerId))
            {
                errors["buyerId"] = ["A buyer is required."];
            }

            if (request.Address is null)
            {
                errors["address"] = ["An address is required."];
            }

            if (request.CardNumber is not { Length: >= 12 } || !request.CardNumber.All(char.IsAsciiDigit))
            {
                errors["cardNumber"] = ["A card number has at least 12 digits."];
            }

            return errors.Count == 0 ? next(context) : ValueTask.FromResult<object?>(Results.ValidationProblem(errors));
        };

    private static async Task SimulateLostResponseAsync(HttpContext context, RequestDelegate next)
    {
        if (!context.Request.Headers.ContainsKey(SimulateLostResponseHeader))
        {
            await next(context);
            return;
        }

        // The response is written to a discarded stream, so the server never sends it, and the reset follows.
        context.Response.Body = Stream.Null;
        await next(context);
        context.Abort();
    }

    private static async Task StreamHistoryAsync(HttpContext context, IOrderGrain order)
    {
        var json = context.RequestServices.GetRequiredService<IOptions<HttpJsonOptions>>().Value.SerializerOptions;
        var response = context.Response;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        await response.Body.FlushAsync(context.RequestAborted);
        await foreach (var snapshot in WatchOrderAsync(order, context.RequestAborted))
        {
            await response.WriteAsync($"data: {JsonSerializer.Serialize(snapshot, json)}\n\n", context.RequestAborted);
            await response.Body.FlushAsync(context.RequestAborted);
        }
    }

    // Watches an order across its activations. A deactivation aborts the order's enumeration; the next activation streams
    // the history from the start again, so the watch skips the snapshots which it has already delivered.
    private static async IAsyncEnumerable<OrderDetails> WatchOrderAsync(IOrderGrain order, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var delivered = 0;
        while (true)
        {
            var position = 0;
            await using var snapshots = order.WatchAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                try
                {
                    if (!await snapshots.MoveNextAsync())
                    {
                        yield break;
                    }
                }
                catch (EnumerationAbortedException)
                {
                    break;
                }

                if (position++ == delivered)
                {
                    delivered++;
                    yield return snapshots.Current;
                }
            }
        }
    }

    private static async Task<object[]> ListTasksAsync(IAddressable grain)
    {
        var tasks = new List<object>();
        await foreach (var (taskId, state) in grain.AsReference<IDurableTaskGrainExtension>().GetTasksAsync())
        {
            tasks.Add(new { taskId = taskId.ToString(), state.Status, state.CreatedAt, state.CompletedAt, state.DueTime, state.Request, state.RemoteTarget, state.Response });
        }

        return [.. tasks];
    }
}

internal sealed record BasketQuantity(int Quantity);
