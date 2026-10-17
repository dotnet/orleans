using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DurableOrderProcessing.Grains;
using Orleans.DurableTasks.Http;
using Xunit;

namespace DurableOrderProcessing.Tests;

/// <summary>
/// Exercises the playground's end-to-end idempotency: each checkout's Idempotency-Key is its order's ID, so retries of a
/// checkout reach one order, and each payment step's derived key reaches one authorization at the gateway.
/// </summary>
[Collection(OrderProcessingCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class OrderProcessingTests(OrderProcessingFixture fixture)
{
    private HttpClient Http => fixture.Http;

    [Fact]
    public async Task ConcurrentCheckoutsWithOneKeyPlaceOneOrderAndAuthorizeOnePayment()
    {
        var buyerId = NewBuyer();
        var orderId = Guid.NewGuid();
        await fixture.FillBasketAsync(buyerId, 1);

        // A double click sends the checkout twice, and a retry a third time, all with the order's key.
        var responses = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => fixture.CheckoutAsync(buyerId, orderId)));

        foreach (var response in responses)
        {
            using (response)
            {
                Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
                Assert.Equal($"http://localhost/api/orders/operations/{orderId}", Assert.Single(response.Headers.GetValues(DurableHttpHeaders.OperationLocation)));
            }
        }

        await fixture.WaitForStatusAsync(orderId, OrderStatus.Paid);
        Assert.Equal(orderId, Assert.Single(await fixture.Client.GetGrain<IBuyerGrain>(buyerId).GetOrdersAsync()).OrderId);
        Assert.Empty(await fixture.Client.GetGrain<IBasketGrain>(buyerId).GetAsync());
        Assert.Equal(1, Assert.Single(fixture.Gateway.List(), entry => entry.OrderId == orderId).Requests);
    }

    [Fact]
    public async Task CheckoutWhoseResponseIsLostReachesItsOrderWhenItIsRetried()
    {
        var buyerId = NewBuyer();
        var orderId = Guid.NewGuid();
        await fixture.FillBasketAsync(buyerId, 2);

        // The server places the order, then the connection resets before the response arrives, which the test server
        // reports as an aborted request.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.CheckoutAsync(buyerId, orderId, loseResponse: true));

        using var retry = await fixture.CheckoutAsync(buyerId, orderId);
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        var paid = await fixture.WaitForStatusAsync(orderId, OrderStatus.Paid);
        Assert.Equal(2, Assert.Single(paid.Order.Lines).ProductId);
        Assert.Single(await fixture.Client.GetGrain<IBuyerGrain>(buyerId).GetOrdersAsync());
        Assert.Single(fixture.Gateway.List(), entry => entry.OrderId == orderId);
    }

    [Fact]
    public async Task PaymentRequestWhoseResponseIsLostIsRetriedWithItsKeyAndAuthorizesOnce()
    {
        var buyerId = NewBuyer();
        var orderId = Guid.NewGuid();
        await fixture.FillBasketAsync(buyerId, 3);

        // The gateway drops its first response for a card ending in 0004, and the payment client retries the request.
        using var started = await fixture.CheckoutAsync(buyerId, orderId, cardNumber: "4111111111110004");
        await fixture.WaitForStatusAsync(orderId, OrderStatus.Paid);

        var authorization = Assert.Single(fixture.Gateway.List(), entry => entry.OrderId == orderId);
        Assert.Equal(2, authorization.Requests);
        Assert.Equal("Succeeded", authorization.Status);
    }

    [Fact]
    public async Task GatewayDeduplicatesAuthorizationsByIdempotencyKey()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var request = new PaymentAuthorizationRequest(Guid.NewGuid(), 10m, "1111");
        var key = Guid.NewGuid().ToString();

        using (var unkeyed = await Http.PostAsJsonAsync("/gateway/authorizations", request, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.BadRequest, unkeyed.StatusCode);
        }

        using var first = await AuthorizeAsync(request, key);
        using var replayed = await AuthorizeAsync(request, key);
        using var conflict = await AuthorizeAsync(request with { Amount = 20m }, key);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.False(first.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(HttpStatusCode.Accepted, replayed.StatusCode);
        Assert.Equal("true", Assert.Single(replayed.Headers.GetValues("Idempotent-Replayed")));
        Assert.Equal(first.Headers.GetValues(DurableHttpHeaders.OperationLocation), replayed.Headers.GetValues(DurableHttpHeaders.OperationLocation));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, conflict.StatusCode);
        Assert.Equal(2, Assert.Single(fixture.Gateway.List(), entry => entry.OrderId == request.OrderId).Requests);

        Task<HttpResponseMessage> AuthorizeAsync(PaymentAuthorizationRequest body, string idempotencyKey)
        {
            var message = new HttpRequestMessage(HttpMethod.Post, "/gateway/authorizations") { Content = JsonContent.Create(body) };
            message.Headers.Add(PaymentGateway.IdempotencyKeyHeader, idempotencyKey);
            return Http.SendAsync(message, cancellationToken);
        }
    }

    [Fact]
    public async Task CancellingAnOrderDuringPaymentVoidsItsAuthorizationAndReleasesItsStock()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var buyerId = NewBuyer();
        var orderId = Guid.NewGuid();
        var available = (await fixture.Client.GetGrain<ICatalogItemGrain>(10).GetAsync())!.AvailableStock;
        await fixture.FillBasketAsync(buyerId, 10);

        // The gateway never decides for a card ending in 0003, so the order waits for its payment.
        using var started = await fixture.CheckoutAsync(buyerId, orderId, cardNumber: "4111111111110003");
        await fixture.WaitForStatusAsync(orderId, OrderStatus.StockConfirmed);
        using (var cancel = await Http.PostAsJsonAsync($"/api/orders/operations/{orderId}:cancel", new DurableOperationCancelRequest("Cancelled by the buyer."), cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
        }

        var operation = await fixture.WaitForOperationAsync(orderId);
        Assert.Equal("Succeeded", operation.GetProperty("status").GetString());
        Assert.Equal("Cancelled", operation.GetProperty("result").GetProperty("status").GetString());
        Assert.Equal("Cancelled by the buyer.", operation.GetProperty("result").GetProperty("description").GetString());
        Assert.Equal("Canceled", Assert.Single(fixture.Gateway.List(), entry => entry.OrderId == orderId).Status);
        var item = (await fixture.Client.GetGrain<ICatalogItemGrain>(10).GetAsync())!;
        Assert.Equal(available, item.AvailableStock);
        Assert.Equal(0, item.ReservedStock);
    }

    [Fact]
    public async Task ShippedSignalIsIdempotentAndCompletesTheOrder()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var buyerId = NewBuyer();
        var orderId = Guid.NewGuid();
        await fixture.FillBasketAsync(buyerId, 6);
        using var started = await fixture.CheckoutAsync(buyerId, orderId);
        await fixture.WaitForStatusAsync(orderId, OrderStatus.Paid);

        var shipment = new Shipment(Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NoContent, await ShipAsync(shipment));
        Assert.Equal(HttpStatusCode.NoContent, await ShipAsync(shipment));
        Assert.Equal(HttpStatusCode.Conflict, await ShipAsync(new Shipment(Guid.NewGuid())));

        var operation = await fixture.WaitForOperationAsync(orderId);
        Assert.Equal("Shipped", operation.GetProperty("result").GetProperty("status").GetString());

        async Task<HttpStatusCode> ShipAsync(Shipment value)
        {
            using var response = await Http.PostAsJsonAsync($"/api/orders/operations/{orderId}/signals/shipped", value, cancellationToken);
            return response.StatusCode;
        }
    }

    [Fact]
    public async Task PaidOrderShipsAfterACancellationRequest()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var buyerId = NewBuyer();
        var orderId = Guid.NewGuid();
        await fixture.FillBasketAsync(buyerId, 1);
        using var started = await fixture.CheckoutAsync(buyerId, orderId);
        await fixture.WaitForStatusAsync(orderId, OrderStatus.Paid);

        using (var cancel = await Http.PostAsJsonAsync($"/api/orders/operations/{orderId}:cancel", new DurableOperationCancelRequest("Too late."), cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
        }

        using (var ship = await Http.PostAsJsonAsync($"/api/orders/operations/{orderId}/signals/shipped", new Shipment(Guid.NewGuid()), cancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, ship.StatusCode);
        }

        var operation = await fixture.WaitForOperationAsync(orderId);
        Assert.Equal("Succeeded", operation.GetProperty("status").GetString());
        Assert.Equal("Shipped", operation.GetProperty("result").GetProperty("status").GetString());
        Assert.Equal("Succeeded", Assert.Single(fixture.Gateway.List(), entry => entry.OrderId == orderId).Status);
    }

    [Fact]
    public async Task PaymentWhichTheGatewayFailsCancelsTheOrderAndReleasesItsStock()
    {
        var buyerId = NewBuyer();
        var orderId = Guid.NewGuid();
        var available = (await fixture.Client.GetGrain<ICatalogItemGrain>(12).GetAsync())!.AvailableStock;
        await fixture.FillBasketAsync(buyerId, 12);

        // The gateway answers 503 Service Unavailable for a card ending in 0005, after the resilience handler's retries.
        using var started = await fixture.CheckoutAsync(buyerId, orderId, cardNumber: "4111111111110005");
        var operation = await fixture.WaitForOperationAsync(orderId);

        Assert.Equal("Cancelled", operation.GetProperty("result").GetProperty("status").GetString());
        Assert.Equal("Payment authorization failed.", operation.GetProperty("result").GetProperty("description").GetString());
        Assert.DoesNotContain(fixture.Gateway.List(), entry => entry.OrderId == orderId);
        var item = (await fixture.Client.GetGrain<ICatalogItemGrain>(12).GetAsync())!;
        Assert.Equal(available, item.AvailableStock);
        Assert.Equal(0, item.ReservedStock);
    }

    [Fact]
    public async Task DeclinedPaymentCancelsTheOrder()
    {
        var buyerId = NewBuyer();
        var orderId = Guid.NewGuid();
        await fixture.FillBasketAsync(buyerId, 11);

        using var started = await fixture.CheckoutAsync(buyerId, orderId, cardNumber: "4111111111110002");
        var operation = await fixture.WaitForOperationAsync(orderId);

        Assert.Equal("Cancelled", operation.GetProperty("result").GetProperty("status").GetString());
        Assert.Equal("The card was declined.", operation.GetProperty("result").GetProperty("description").GetString());
    }

    [Fact]
    public async Task CheckoutOfAnEmptyBasketFailsWithItsReason()
    {
        var orderId = Guid.NewGuid();

        using var response = await fixture.CheckoutAsync(NewBuyer(), orderId);
        var operation = await fixture.WaitForOperationAsync(orderId);

        Assert.Equal("Failed", operation.GetProperty("status").GetString());
        Assert.Equal("Rejected", operation.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("The basket is empty.", operation.GetProperty("error").GetProperty("message").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-uuid")]
    [InlineData("UPPER")]
    public async Task CheckoutKeyIsTheOrdersCanonicalId(string? key)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders") { Content = JsonContent.Create(OrderProcessingFixture.Checkout(NewBuyer(), "4111111111111111")) };
        if (key is not null)
        {
            request.Headers.Add(DurableHttpHeaders.IdempotencyKey, key == "UPPER" ? Guid.NewGuid().ToString().ToUpperInvariant() : key);
        }

        using var response = await Http.SendAsync(request, cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task NexusRequestAndCheckoutWithTheSameIdReachOneOrder()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var buyerId = NewBuyer();
        var orderId = Guid.NewGuid();
        await fixture.FillBasketAsync(buyerId, 8);

        using var nexus = new HttpRequestMessage(HttpMethod.Post, "/nexus/orders.v1/place-order") { Content = JsonContent.Create(OrderProcessingFixture.Checkout(buyerId, "4111111111111111")) };
        nexus.Headers.Add("Nexus-Request-Id", orderId.ToString());
        using var started = await Http.SendAsync(nexus, cancellationToken);
        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        Assert.Equal(orderId.ToString(), (await started.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("token").GetString());

        using var checkout = await fixture.CheckoutAsync(buyerId, orderId);
        Assert.Equal(HttpStatusCode.Accepted, checkout.StatusCode);
        await fixture.WaitForStatusAsync(orderId, OrderStatus.Paid);
        Assert.Single(await fixture.Client.GetGrain<IBuyerGrain>(buyerId).GetOrdersAsync());
    }

    private static string NewBuyer() => $"buyer-{Guid.NewGuid():N}";
}
