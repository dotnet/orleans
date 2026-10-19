using System.Net;
using System.Security.Claims;
using CsCheck;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Http;
using Orleans.Runtime;
using Xunit;

namespace Orleans.DurableTasks.Http.Tests;

/// <summary>
/// Exercises the package's boundaries without a cluster: operation keys, callback addresses and redirects, recorded
/// responses, retry intervals, and endpoint conventions.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class DurableHttpBoundaryTests
{
    private static readonly DurableHttpOptions Options = new();

    [Fact]
    public void OperationKeysAreInjective()
    {
        // A small alphabet of the characters which segments escape makes equal and colliding inputs likely.
        var segment = Gen.String[Gen.Char["a:@/%"], 1, 3];
        var input = Gen.Select(segment.Array[1, 3], segment.Null(), segment);
        Gen.Select(input, input).Sample(pair =>
        {
            var (first, second) = pair;
            var equal = first.Item1.SequenceEqual(second.Item1) && first.Item2 == second.Item2 && first.Item3 == second.Item3;
            return (Key(first) == Key(second)) == equal;
        });

        static string Key((string[] Prefix, string? Caller, string Id) input)
        {
            var prefix = TaskId.CreateRoot(input.Prefix[0]);
            foreach (var segment in input.Prefix.Skip(1))
            {
                prefix = prefix.Child(segment);
            }

            return DurableOperationKeys.Complete(prefix, CreateContext(input.Caller), Options, input.Id);
        }
    }

    [Fact]
    public void AnonymousKeysDoNotReachAnAuthenticatedCallersOperation()
    {
        var prefix = TaskId.CreateRoot("orders").Child("42");

        var ada = DurableOperationKeys.Complete(prefix, CreateContext("ada"), Options, "k");

        Assert.NotEqual(ada, DurableOperationKeys.Complete(prefix, CreateContext(caller: null), Options, "ada:k"));
        Assert.NotEqual(ada, DurableOperationKeys.Complete(prefix, CreateContext(caller: null), Options, "k"));
        Assert.NotEqual(ada, DurableOperationKeys.Complete(prefix, CreateContext("grace"), Options, "k"));
    }

    [Fact]
    public void AuthenticatedCallerWithoutAScopeIsRejected()
    {
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Email, "ada@example.com")], "Test")) };

        Assert.Throws<InvalidOperationException>(() => DurableOperationKeys.Complete(TaskId.CreateRoot("orders"), context, Options, "k"));
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("::", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("::ffff:8.8.8.8", true)]
    [InlineData("64:ff9b::7f00:1", false)]
    [InlineData("2002:7f00:1::", false)]
    public void PublicAddressesAreThoseWhichDeliveriesReach(string address, bool expected) =>
        Assert.Equal(expected, DurableCallbackOptions.IsPublicAddress(IPAddress.Parse(address)));

    [Fact]
    public async Task DeliveryDoesNotConnectToAHostWhichResolvesToARefusedAddress()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await DurableCallbackConnections.ConnectAsync(new DnsEndPoint("127.0.0.1", 9), DurableCallbackOptions.IsPublicAddress, cancellationToken));
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await DurableCallbackConnections.ConnectAsync(new DnsEndPoint("localhost", 9), DurableCallbackOptions.IsPublicAddress, cancellationToken));
    }

    [Fact]
    public void CallbackClientPinsAddressesAndDoesNotFollowRedirects()
    {
        using var services = CreateCallbackServices(options => { }, builder => { });

        var handler = BuildPrimaryHandler(services);

        var sockets = Assert.IsType<SocketsHttpHandler>(handler);
        Assert.False(sockets.AllowAutoRedirect);
        Assert.NotNull(sockets.ConnectCallback);
    }

    [Fact]
    public void CallbackClientWhosePrimaryHandlerCantBeConstrainedFailsToBuild()
    {
        // An HttpClientHandler connects without a callback, so it can't apply the address check.
        using (var services = CreateCallbackServices(options => { }, builder => builder.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler())))
        {
            Assert.Throws<InvalidOperationException>(() => BuildPrimaryHandler(services));
        }

        // Another handler type can't be kept from following redirects, even when connections are left to it.
        using (var services = CreateCallbackServices(options => options.AllowAddress = null, builder => builder.ConfigurePrimaryHttpMessageHandler(() => new ForwardingHandler())))
        {
            Assert.Throws<InvalidOperationException>(() => BuildPrimaryHandler(services));
        }

        using (var services = CreateCallbackServices(options => options.AllowAddress = null, builder => builder.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler())))
        {
            Assert.False(Assert.IsType<HttpClientHandler>(BuildPrimaryHandler(services)).AllowAutoRedirect);
        }
    }

    [Fact]
    public void CallbackIdentifiersIgnoreHeadersAndHaveAKnownForm()
    {
        var callback = new DurableCallback(
            DurableCallbackProtocol.Webhook,
            GrainId.Create("orleans-durable-operation", "orders/@/k"),
            "k",
            new Uri("https://receiver.example/hooks"),
            new Dictionary<string, string> { ["authorization"] = "protected-1" },
            "https://api.example/orders/operations/k");

        var id = callback.GetCallbackId();

        // A rotated credential reaches the same relay, so the webhook-id is stable.
        Assert.Equal(id, (callback with { Headers = new Dictionary<string, string> { ["authorization"] = "protected-2" } }).GetCallbackId());
        Assert.NotEqual(id, (callback with { Url = new Uri("https://receiver.example/other") }).GetCallbackId());
        Assert.Equal(43, id.Length);
        Assert.Equal("ZSdN79svX-EbA6JJ7KrAxomJatn8zb3mKXgwNkcAqgc", id);
    }

    [Theory]
    [InlineData(null, 1, 1)]
    [InlineData(10, 1, 10)]
    [InlineData(0, 4, 4)]
    [InlineData(100_000, 1, 300)]
    public void WebhookRetriesWaitForTheLongerOfBackoffAndRetryAfterWithinTheirCap(int? retryAfterSeconds, int backoffSeconds, int expectedSeconds)
    {
        var retryAfter = retryAfterSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : (TimeSpan?)null;
        var max = TimeSpan.FromMinutes(5);

        var delay = DurableCallbackRelayGrain.GetRetryDelay(retryAfter, TimeSpan.FromSeconds(backoffSeconds), max, "callback", attemptNumber: 3);

        // Jitter adds up to a quarter, and derives from the callback and the attempt, so every replay waits the same delay.
        var expected = TimeSpan.FromSeconds(expectedSeconds);
        Assert.InRange(delay, expected, expected * 1.25);
        Assert.Equal(delay, DurableCallbackRelayGrain.GetRetryDelay(retryAfter, TimeSpan.FromSeconds(backoffSeconds), max, "callback", attemptNumber: 3));
    }

    [Theory]
    [InlineData(null, 5)]
    [InlineData("0", 5)]
    [InlineData("1", 5)]
    [InlineData("30", 30)]
    [InlineData("86400", 60)]
    public void PollsWaitForRetryAfterWithinThePollIntervals(string? retryAfter, int expectedSeconds)
    {
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (retryAfter is not null)
        {
            headers["Retry-After"] = [retryAfter];
        }

        var response = new DurableHttpResponse(HttpStatusCode.Accepted, new Uri("https://api.example/jobs/1"), headers, ReadOnlyMemory<byte>.Empty);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), DurableHttpClient.GetPollDelay(response, new DurableHttpOperationOptions()));
    }

    [Theory]
    [InlineData("https://api.example/orders", "https://api.example/operations/1", true)]
    [InlineData("https://api.example/orders", "https://API.example:443/operations/1", true)]
    [InlineData("https://api.example/orders", "http://api.example/operations/1", false)]
    [InlineData("https://api.example/orders", "https://api.example:8443/operations/1", false)]
    [InlineData("https://api.example/orders", "https://metadata.internal/operations/1", false)]
    public void ClientsFollowOperationLocationsOfTheStartsOrigin(string requestUri, string location, bool expected) =>
        Assert.Equal(expected, DurableHttpClientOptions.IsSameOrigin(new Uri(requestUri), new Uri(location)));

    [Fact]
    public void ClientsHaveTheirOwnSerializerOptions() =>
        Assert.NotSame(new DurableHttpClientOptions().SerializerOptions, new DurableHttpClientOptions().SerializerOptions);

    [Fact]
    public async Task RecordedResponsesOmitCookies()
    {
        using var message = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
        message.Headers.TryAddWithoutValidation("Set-Cookie", "session=secret");
        message.Headers.TryAddWithoutValidation("ETag", "\"1\"");

        var response = await DurableHttpResponse.ReadAsync(message, TestContext.Current.CancellationToken);

        Assert.False(response.Headers.ContainsKey("Set-Cookie"));
        Assert.Equal("\"1\"", response.GetHeader("ETag"));
    }

    [Theory]
    [InlineData("r")]
    [InlineData("s")]
    public void OperationWorkRoundTripsThroughItsJournalText(string kind)
    {
        var work = new DurableOperationWork(
            new DurableTaskAddress(GrainId.Create("durable-http-test", "a:b/c"), TaskId.CreateRoot("orders").Child("@ada").Child("k")),
            IsSubscription: kind == "s");

        Assert.Equal(work, DurableOperationWork.Parse(work.Format()));
        Assert.Same(DurableOperationWork.NotStarted, DurableOperationWork.Parse(DurableOperationWork.NotStarted.Format()));
    }

    [Fact]
    public void StartsAreNotMappedToSafeMethods()
    {
        var app = CreateApp();
        app.MapGroup("/orders/{orderId}").WithDurableOperations("orders")
            .MapGet("/start", static (string orderId, IGrainFactory grains) => grains.GetGrain<IDurableHttpTestGrain>(orderId).EchoAsync("hello"));

        Assert.Throws<InvalidOperationException>(() => ((IEndpointRouteBuilder)app).DataSources.SelectMany(static source => source.Endpoints).ToList());
    }

    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData("{name}")]
    [InlineData("*")]
    public void SignalsWithNamesWhichArentRouteSegmentsAreRejected(string name)
    {
        var app = CreateApp();
        var group = app.MapGroup("/orders/{orderId}").WithDurableOperations("orders");

        Assert.ThrowsAny<ArgumentException>(() => group.MapDurableSignal(new DurableSignal<string>(name)));
    }

    private static WebApplication CreateApp()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton<IGrainFactory>(_ => throw new InvalidOperationException("The test maps endpoints only."));
        builder.Services.AddDurableHttp();
        return builder.Build();
    }

    private static DefaultHttpContext CreateContext(string? caller) => new()
    {
        User = caller is null
            ? new ClaimsPrincipal(new ClaimsIdentity())
            : new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, caller)], "Test")),
    };

    private static ServiceProvider CreateCallbackServices(Action<DurableCallbackOptions> configure, Action<IHttpClientBuilder> configureClient)
    {
        var services = new ServiceCollection();
        configureClient(services.AddDurableHttpCallbacks(configure));
        return services.BuildServiceProvider();
    }

    private static HttpMessageHandler BuildPrimaryHandler(IServiceProvider services)
    {
        var handler = services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(DurableCallbackOptions.HttpClientName);
        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler!;
        }

        return handler;
    }
    /// <summary>A primary handler of a type which callback delivery can't constrain.</summary>
    private sealed class ForwardingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}
