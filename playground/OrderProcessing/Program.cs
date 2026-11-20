using Orleans.Dashboard;
using Orleans.Journaling;
using Orleans.Journaling.Json;
using Orleans.Messaging;
using OrderProcessing;
using OrderProcessing.Grains;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<OrderingOptions>(builder.Configuration.GetSection("Ordering"));
builder.Services.AddSingleton<OrderMessages>();

builder.UseOrleans(silo => silo
    .UseLocalhostClustering()
    .UseInMemoryDurableJobs()
    .AddVolatileJournalStorage()
    .UseJsonJournalFormat(OrderProcessingJsonContext.Default)
    .AddMessaging()
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

api.MapPost("/orders/{orderId:guid}/cancel", async (Guid orderId, IGrainFactory grains) =>
    await grains.GetGrain<IOrderGrain>(orderId).CancelAsync() ? Results.Ok() : Results.Conflict());

api.MapPost("/orders/{orderId:guid}/ship", async (Guid orderId, IGrainFactory grains) =>
    await grains.GetGrain<IOrderGrain>(orderId).ShipAsync() ? Results.Ok() : Results.Conflict());

app.Run();

internal sealed record BasketQuantity(int Quantity);
