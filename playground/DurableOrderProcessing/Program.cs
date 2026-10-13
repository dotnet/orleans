using Orleans.Core.Internal;
using Orleans.Dashboard;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Orleans.Journaling;
using Orleans.Journaling.Json;
using DurableOrderProcessing;
using DurableOrderProcessing.Grains;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<OrderingOptions>(builder.Configuration.GetSection("Ordering"));

builder.UseOrleans(silo => silo
    .UseLocalhostClustering()
    .UseInMemoryDurableJobs()
    .AddVolatileJournalStorage()
    .UseJsonJournalFormat(DurableOrderProcessingJsonContext.Default)
    .AddDurableTasks()
    .AddDashboard());

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapOrleansDashboard(routePrefix: "/dashboard");

var api = app.MapGroup("/api");

api.MapGet("/catalog", async (IGrainFactory grains) =>
    await Task.WhenAll(CatalogData.Items.Select(item => grains.GetGrain<ICatalogItemGrain>(item.Id).GetAsync())));

api.MapGet("/buyers/{buyerId}/basket", (string buyerId, IGrainFactory grains) =>
    grains.GetGrain<IBasketGrain>(buyerId).GetAsync());

api.MapPut("/buyers/{buyerId}/basket/{productId:int}", async (string buyerId, int productId, BasketQuantity body, IGrainFactory grains) =>
    CatalogData.Find(productId) is null
        ? Results.NotFound()
        : Results.Ok(await grains.GetGrain<IBasketGrain>(buyerId).SetQuantityAsync(productId, body.Quantity)));

api.MapPost("/buyers/{buyerId}/checkout", async (string buyerId, CheckoutRequest request, IGrainFactory grains) =>
{
    if (request.RequestId == Guid.Empty || request.Address is null
        || request.CardNumber is not { Length: >= 12 } || !request.CardNumber.All(char.IsAsciiDigit))
    {
        return Results.BadRequest("Provide a request ID, an address, and a card number of at least 12 digits.");
    }

    var orderId = await grains.GetGrain<IBasketGrain>(buyerId).CheckoutAsync(request);
    return orderId is { } id ? Results.Accepted($"/api/orders/{id}", new { orderId = id }) : Results.BadRequest("The basket is empty.");
});

api.MapGet("/buyers/{buyerId}/orders", (string buyerId, IGrainFactory grains) =>
    grains.GetGrain<IBuyerGrain>(buyerId).GetOrdersAsync());

api.MapGet("/orders/{orderId:guid}", async (Guid orderId, IGrainFactory grains) =>
    await grains.GetGrain<IOrderGrain>(orderId).GetAsync() is { } order ? Results.Ok(order) : Results.NotFound());

// Polls the order's PlaceOrder workflow without waiting.
api.MapGet("/orders/{orderId:guid}/workflow", async (Guid orderId, IGrainFactory grains) =>
{
    var workflow = grains.GetGrain<IOrderGrain>(orderId).GetDurableTask<OrderResult>(OrderGrain.PlaceOrderTaskId(orderId));
    try
    {
        var response = await workflow.GetResponseAsync(new PollingOptions { PollTimeout = TimeSpan.Zero });
        return Results.Ok(new
        {
            taskId = workflow.Id.ToString(),
            status = response.Status.ToString(),
            result = response.Status is DurableTaskStatus.CompletedSuccessfully ? response.GetResult<OrderResult>() : null,
            error = response.Exception?.Message,
        });
    }
    catch (DurableTaskNotFoundException)
    {
        // The order prunes the workflow once the basket which scheduled it has accepted its result.
        return Results.NotFound();
    }
});

// Lists the durable tasks which a grain retains, for diagnostics.
api.MapGet("/orders/{orderId:guid}/tasks", (Guid orderId, IGrainFactory grains) =>
    ListTasksAsync(grains.GetGrain<IOrderGrain>(orderId)));

api.MapGet("/buyers/{buyerId}/basket/tasks", (string buyerId, IGrainFactory grains) =>
    ListTasksAsync(grains.GetGrain<IBasketGrain>(buyerId)));

api.MapGet("/catalog/{productId:int}/tasks", (int productId, IGrainFactory grains) =>
    ListTasksAsync(grains.GetGrain<ICatalogItemGrain>(productId)));

// Deactivates an order, for observing recovery: the next activation replays its pending tasks.
api.MapPost("/orders/{orderId:guid}/deactivate", async (Guid orderId, IGrainFactory grains) =>
    await grains.GetGrain<IOrderGrain>(orderId).AsReference<IGrainManagementExtension>().DeactivateOnIdle());

// Each request runs as its own durable task, which records its decision once.
api.MapPost("/orders/{orderId:guid}/cancel", async (Guid orderId, IGrainFactory grains) =>
    await RunAsync(grains.GetGrain<IOrderGrain>(orderId).CancelAsync(), "cancel") ? Results.Ok() : Results.Conflict());

api.MapPost("/orders/{orderId:guid}/ship", async (Guid orderId, IGrainFactory grains) =>
    await RunAsync(grains.GetGrain<IOrderGrain>(orderId).ShipAsync(), "ship") ? Results.Ok() : Results.Conflict());

app.Run();

static async Task<T> RunAsync<T>(DurableTask<T> command, string name) =>
    await await command.ScheduleAsync($"{name}-{Guid.NewGuid():N}");

static async Task<object[]> ListTasksAsync(IAddressable grain)
{
    var tasks = new List<object>();
    await foreach (var (taskId, state) in grain.AsReference<IDurableTaskGrainExtension>().GetTasksAsync())
    {
        tasks.Add(new { taskId = taskId.ToString(), state.Status, state.CreatedAt, state.CompletedAt, state.Request, state.Response });
    }

    return [.. tasks];
}

internal sealed record BasketQuantity(int Quantity);
