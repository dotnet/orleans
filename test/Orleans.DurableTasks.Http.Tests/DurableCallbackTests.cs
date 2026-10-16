using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Orleans.DurableTasks.Http;
using Xunit;

namespace Orleans.DurableTasks.Http.Tests;

/// <summary>Exercises reliable callbacks: webhooks of the group convention and Nexus completion callbacks.</summary>
[Collection(DurableHttpCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class DurableCallbackTests(DurableHttpFixture fixture)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private HttpClient Http => fixture.Http;

    [Fact]
    public async Task WebhookDeliversTheTerminalStatusMonitorWithStandardWebhookHeaders()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var name = $"webhook-{Guid.NewGuid():N}";
        var delivered = DurableHttpFixture.Callbacks.ExpectAsync(name);
        var key = Guid.NewGuid().ToString();
        using var started = await StartWithWebhookAsync($"/orders/{Guid.NewGuid():N}/approval", key, name, cancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var location = Assert.Single(started.Headers.GetValues(DurableHttpHeaders.OperationLocation));

        // A retried start registers the same callback, which delivers once.
        using (var retry = await StartWithWebhookAsync(new Uri(location).AbsolutePath[..^$"/operations/{key}".Length] + "/approval", key, name, cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        }

        using (var signal = await Http.PostAsJsonAsync($"{location}/signals/approved", "ada", cancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, signal.StatusCode);
        }

        var callback = await delivered.WaitAsync(Timeout, cancellationToken);
        var operation = JsonSerializer.Deserialize<DurableOperation>(callback.Body)!;
        Assert.Equal(key, operation.Id);
        Assert.Equal(DurableOperationStatus.Succeeded, operation.Status);
        Assert.Equal("widget approved by ada", operation.Result?.GetString());
        Assert.Equal("Bearer receiver-token", callback.Headers["Authorization"]);
        Assert.Equal(location, callback.Headers[DurableHttpHeaders.OperationLocation]);
        var timestamp = long.Parse(callback.Headers["webhook-timestamp"], CultureInfo.InvariantCulture);
        Assert.Equal(
            TestWebhookSigner.Expected(callback.Headers["webhook-id"], timestamp, System.Text.Encoding.UTF8.GetByteCount(callback.Body)),
            callback.Headers["webhook-signature"]);
        Assert.Equal(1, DurableHttpFixture.Callbacks.GetAttempts(name));
    }

    [Fact]
    public async Task WebhookIsRetriedUntilTheReceiverAcceptsIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var name = $"flaky-{Guid.NewGuid():N}";
        var delivered = DurableHttpFixture.Callbacks.ExpectAsync(name);
        using var started = await StartWithWebhookAsync($"/orders/{Guid.NewGuid():N}/echo-later", Guid.NewGuid().ToString(), name, cancellationToken, new EchoRequest("hello"));
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);

        var callback = await delivered.WaitAsync(Timeout, cancellationToken);

        Assert.Equal("hello", JsonSerializer.Deserialize<DurableOperation>(callback.Body)!.Result?.GetString());
        Assert.Equal(2, DurableHttpFixture.Callbacks.GetAttempts(name));
    }

    [Theory]
    [InlineData("slow")]
    [InlineData("moved")]
    public async Task WebhookIsRetriedAfterATimeoutOrARedirectWhichItDoesNotFollow(string kind)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var name = $"{kind}-{Guid.NewGuid():N}";
        var delivered = DurableHttpFixture.Callbacks.ExpectAsync(name);
        using var started = await StartWithWebhookAsync($"/orders/{Guid.NewGuid():N}/echo-later", Guid.NewGuid().ToString(), name, cancellationToken, new EchoRequest("hello"));
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);

        var callback = await delivered.WaitAsync(Timeout, cancellationToken);

        Assert.Equal("hello", JsonSerializer.Deserialize<DurableOperation>(callback.Body)!.Result?.GetString());
        Assert.Equal(2, DurableHttpFixture.Callbacks.GetAttempts(name));
        Assert.Equal(0, DurableHttpFixture.Callbacks.GetAttempts($"redirected-{name}"));
    }

    [Fact]
    public async Task WebhookOfAnOperationWithoutAResultReportsItsSuccess()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var name = $"void-{Guid.NewGuid():N}";
        var delivered = DurableHttpFixture.Callbacks.ExpectAsync(name);
        var key = Guid.NewGuid().ToString();
        using var started = await StartWithWebhookAsync($"/orders/{Guid.NewGuid():N}/complete-later", key, name, cancellationToken, new EchoRequest("hello"));
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);

        var callback = await delivered.WaitAsync(Timeout, cancellationToken);

        Assert.Equal(new DurableOperation(key, DurableOperationStatus.Succeeded), JsonSerializer.Deserialize<DurableOperation>(callback.Body));
    }

    [Fact]
    public async Task NexusCallbackDeliversTheOutcomeWithNexusHeaders()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var name = $"nexus-{Guid.NewGuid():N}";
        var delivered = DurableHttpFixture.Callbacks.ExpectAsync(name);
        using var start = new HttpRequestMessage(HttpMethod.Post, $"/nexus/orders.v1/approval?callback={Uri.EscapeDataString($"http://localhost/callbacks/{name}")}")
        {
            Content = JsonContent.Create(new NexusApprovalInput(Guid.NewGuid().ToString("N"), "widget")),
        };
        start.Headers.Add("Nexus-Request-Id", Guid.NewGuid().ToString());
        start.Headers.Add("Nexus-Callback-Token", "caller-token");
        using var started = await Http.SendAsync(start, cancellationToken);
        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        var token = (await started.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("token").GetString()!;

        using (var cancel = new HttpRequestMessage(HttpMethod.Post, "/nexus/orders.v1/approval/cancel"))
        {
            cancel.Headers.Add("Nexus-Operation-Token", token);
            using var canceled = await Http.SendAsync(cancel, cancellationToken);
            Assert.Equal(HttpStatusCode.Accepted, canceled.StatusCode);
        }

        var callback = await delivered.WaitAsync(Timeout, cancellationToken);
        Assert.Equal("caller-token", callback.Headers["Token"]);
        Assert.Equal(token, callback.Headers["Nexus-Operation-Token"]);
        Assert.Equal("canceled", callback.Headers["Nexus-Operation-State"]);
        Assert.False(callback.Headers.ContainsKey("Nexus-Operation-Start-Time"));
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$", callback.Headers["Nexus-Operation-Close-Time"]);
        var failure = JsonSerializer.Deserialize<JsonElement>(callback.Body);
        Assert.Equal("nexus.OperationError", failure.GetProperty("metadata").GetProperty("type").GetString());
        Assert.Equal("canceled", failure.GetProperty("details").GetProperty("state").GetString());
    }

    [Theory]
    [InlineData("http://example.com/callbacks/x")]
    [InlineData("http://localhost/elsewhere")]
    [InlineData("/callbacks/relative")]
    [InlineData("ftp://localhost/callbacks/x")]
    public async Task CallbacksWhichTheApplicationDoesNotAcceptAreRejected(string url)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using (var request = new HttpRequestMessage(HttpMethod.Post, $"/orders/{Guid.NewGuid():N}/echo") { Content = JsonContent.Create(new EchoRequest("hello")) })
        {
            request.Headers.Add(DurableHttpHeaders.IdempotencyKey, Guid.NewGuid().ToString());
            request.Headers.Add(DurableHttpHeaders.OperationCallback, url);
            using var response = await Http.SendAsync(request, cancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        }

        using var nexus = new HttpRequestMessage(HttpMethod.Post, $"/nexus/orders.v1/approval?callback={Uri.EscapeDataString(url)}")
        {
            Content = JsonContent.Create(new NexusApprovalInput(Guid.NewGuid().ToString("N"), "widget")),
        };
        using var refused = await Http.SendAsync(nexus, cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var failure = await refused.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        Assert.Equal("BAD_REQUEST", failure.GetProperty("details").GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("Host")]
    [InlineData("Transfer-Encoding")]
    [InlineData("Proxy-Authorization")]
    [InlineData("Content-Type")]
    [InlineData("webhook-signature")]
    [InlineData("Nexus-Operation-State")]
    public async Task CallbackHeadersWhichAddressTheConnectionOrWhichDeliveriesSetAreRejected(string header)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using (var request = new HttpRequestMessage(HttpMethod.Post, $"/orders/{Guid.NewGuid():N}/echo") { Content = JsonContent.Create(new EchoRequest("hello")) })
        {
            request.Headers.Add(DurableHttpHeaders.IdempotencyKey, Guid.NewGuid().ToString());
            request.Headers.Add(DurableHttpHeaders.OperationCallback, $"http://localhost/callbacks/{Guid.NewGuid():N}");
            request.Headers.TryAddWithoutValidation($"{DurableHttpHeaders.OperationCallbackHeaderPrefix}{header}", "internal.example");
            using var response = await Http.SendAsync(request, cancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        using var nexus = new HttpRequestMessage(HttpMethod.Post, $"/nexus/orders.v1/approval?callback={Uri.EscapeDataString($"http://localhost/callbacks/{Guid.NewGuid():N}")}")
        {
            Content = JsonContent.Create(new NexusApprovalInput(Guid.NewGuid().ToString("N"), "widget")),
        };
        nexus.Headers.TryAddWithoutValidation($"Nexus-Callback-{header}", "internal.example");
        using var refused = await Http.SendAsync(nexus, cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    private Task<HttpResponseMessage> StartWithWebhookAsync(string path, string key, string receiver, CancellationToken cancellationToken, object? body = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body ?? new ApprovalRequest("widget")) };
        request.Headers.Add(DurableHttpHeaders.IdempotencyKey, key);
        request.Headers.Add(DurableHttpHeaders.OperationCallback, $"http://localhost/callbacks/{receiver}");
        request.Headers.TryAddWithoutValidation($"{DurableHttpHeaders.OperationCallbackHeaderPrefix}Authorization", "Bearer receiver-token");
        return Http.SendAsync(request, cancellationToken);
    }
}
