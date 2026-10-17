using System.Net.Http.Json;
using System.Text.Json;
using DurableOrderProcessing;
using DurableOrderProcessing.Grains;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableTasks.Http;
using Orleans.Hosting;
using Orleans.TestingHost;
using Xunit;

namespace DurableOrderProcessing.Tests;

/// <summary>
/// Hosts the playground: an in-process cluster which runs the order workflows, and a test server which serves the API
/// and the simulated payment gateway. The silo calls the gateway on the test server through the payment client.
/// </summary>
public sealed class OrderProcessingFixture : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly InProcessTestCluster _cluster;
    private WebApplication? _app;
    private TestServer? _server;

    public OrderProcessingFixture()
    {
        var builder = new InProcessTestClusterBuilder();
        builder.ConfigureClient(client => client.AddDurableTasks());
        builder.ConfigureSilo((_, silo) =>
        {
            silo.AddOrderProcessing(payments => payments
                .ConfigureHttpClient(static client => client.BaseAddress = new Uri("http://localhost/gateway/"))
                .ConfigurePrimaryHttpMessageHandler(() => new NetworkHandler(Server.CreateHandler())));

            // Orders validate at once and wait for a payment until a test cancels it.
            silo.Services.Configure<OrderingOptions>(static options =>
            {
                options.GracePeriod = TimeSpan.Zero;
                options.PaymentTimeout = TimeSpan.FromMinutes(5);
                options.PaymentPollInterval = TimeSpan.FromMilliseconds(100);
            });
        });
        _cluster = builder.Build();
    }

    public HttpClient Http { get; private set; } = null!;

    public IClusterClient Client => _cluster.Client;

    public PaymentGateway Gateway => _app!.Services.GetRequiredService<PaymentGateway>();

    private TestServer Server => _server ?? throw new InvalidOperationException("The test server has not started.");

    public async ValueTask InitializeAsync()
    {
        await _cluster.DeployAsync();

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IGrainFactory>(_cluster.Client);
        builder.Services.AddOrderProcessingApi();
        _app = builder.Build();
        _app.MapOrderProcessing();
        await _app.StartAsync();
        _server = _app.GetTestServer();
        Http = _app.GetTestClient();
    }

    /// <summary>Fills the buyer's basket with one unit of each product.</summary>
    public async Task FillBasketAsync(string buyerId, params int[] productIds)
    {
        foreach (var productId in productIds)
        {
            using var response = await Http.PutAsJsonAsync($"/api/buyers/{buyerId}/basket/{productId}", new { quantity = 1 }, TestContext.Current.CancellationToken);
            response.EnsureSuccessStatusCode();
        }
    }

    /// <summary>Sends a checkout of the buyer's basket as the order which <paramref name="orderId"/> names.</summary>
    public Task<HttpResponseMessage> CheckoutAsync(string buyerId, Guid orderId, string cardNumber = "4111111111111111", bool loseResponse = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders") { Content = JsonContent.Create(Checkout(buyerId, cardNumber)) };
        request.Headers.Add(DurableHttpHeaders.IdempotencyKey, orderId.ToString());
        if (loseResponse)
        {
            request.Headers.Add(OrderProcessingApplication.SimulateLostResponseHeader, "true");
        }

        return Http.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public static CheckoutRequest Checkout(string buyerId, string cardNumber) =>
        new(buyerId, new Address("15703 NE 61st Ct", "Redmond", "WA", "U.S.", "98052"), cardNumber);

    /// <summary>Waits for the order's history to reach <paramref name="status"/>, and returns that snapshot.</summary>
    public async Task<OrderDetails> WaitForStatusAsync(Guid orderId, OrderStatus status)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(Timeout);
        OrderDetails? last = null;
        try
        {
            await foreach (var snapshot in Client.GetGrain<IOrderGrain>(orderId).WatchAsync(timeout.Token).WithCancellation(timeout.Token))
            {
                last = snapshot;
                if (snapshot.Order.Status == status)
                {
                    return snapshot;
                }
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Order {orderId} didn't reach {status} within {Timeout}; its last status is {last?.Order.Status.ToString() ?? "none"}.");
        }

        throw new InvalidOperationException($"Order {orderId} ended as {last?.Order.Status} without reaching {status}.");
    }

    /// <summary>Long-polls the order's operation until it is terminal, and returns its status monitor.</summary>
    public async Task<JsonElement> WaitForOperationAsync(Guid orderId)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/orders/operations/{orderId}");
            request.Headers.Add("Prefer", "wait=30");
            using var response = await Http.SendAsync(request, cancellationToken).WaitAsync(Timeout, cancellationToken);
            response.EnsureSuccessStatusCode();
            var operation = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            if (operation.GetProperty("status").GetString() is "Succeeded" or "Failed" or "Canceled")
            {
                return operation;
            }
        }
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

    /// <summary>
    /// Reports a request which the test server's application aborted, which the test server fails with an
    /// <see cref="OperationCanceledException"/> or an <see cref="IOException"/>, as an <see cref="HttpRequestException"/>,
    /// as the sockets handler reports a reset connection, so the payment client's resilience handler retries it.
    /// </summary>
    private sealed class NetworkHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try
            {
                return await base.SendAsync(request, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                throw new HttpRequestException("The connection was reset.", exception);
            }
        }
    }
}

[CollectionDefinition(Name)]
public sealed class OrderProcessingCollection : ICollectionFixture<OrderProcessingFixture>
{
    public const string Name = "OrderProcessing";
}
