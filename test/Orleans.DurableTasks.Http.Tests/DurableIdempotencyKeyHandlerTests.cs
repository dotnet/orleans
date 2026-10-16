using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Http;
using Orleans.Runtime;
using Xunit;

namespace Orleans.DurableTasks.Http.Tests;

/// <summary>Exercises the idempotency keys which durable tasks send with their HTTP requests.</summary>
[Collection(DurableHttpCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class DurableIdempotencyKeyHandlerTests(DurableHttpFixture fixture)
{
    [Fact]
    public async Task DurableTaskSendsAKeyWhichIdentifiesItsStepWithUnsafeRequests()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var grain = fixture.Client.GetGrain<IDurableHttpTestGrain>(Guid.NewGuid().ToString("N"));

        var scheduled = await grain.SendIdempotencyKeyAsync().WithId("send-key").ScheduleAsync(cancellationToken);
        var result = (await scheduled.GetResponseAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken)).GetResult<string[]>();

        Assert.Equal("send-key/send", result[0]);
        Assert.Equal(
            DurableIdempotencyKeyHandler.CreateKey(fixture.ServiceId, grain.GetGrainId(), TaskId.Parse(result[0])),
            result[1]);

        // A GET is idempotent by definition, so it carries no key.
        Assert.Equal(string.Empty, result[2]);
    }

    [Fact]
    public void KeysDistinguishServicesGrainsAndTasks()
    {
        var grain = GrainId.Create("durable-http-test", "a");
        var task = TaskId.CreateRoot("root").Child("send");
        var key = DurableIdempotencyKeyHandler.CreateKey("service", grain, task);

        Assert.Equal(43, key.Length);
        Assert.Equal(key, DurableIdempotencyKeyHandler.CreateKey("service", grain, task));
        Assert.NotEqual(key, DurableIdempotencyKeyHandler.CreateKey("other-service", grain, task));
        Assert.NotEqual(key, DurableIdempotencyKeyHandler.CreateKey("service", GrainId.Create("durable-http-test", "b"), task));
        Assert.NotEqual(key, DurableIdempotencyKeyHandler.CreateKey("service", grain, TaskId.CreateRoot("root").Child("send$2")));
    }

    [Fact]
    public async Task RequestsOutsideDurableTasksAndRequestsWithKeysAreUnchanged()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var options = Options.Create(new ClusterOptions { ServiceId = fixture.ServiceId });
        using var client = new HttpClient(new DurableIdempotencyKeyHandler(new NoGrainContextAccessor(), options) { InnerHandler = fixture.Server.CreateHandler() })
        {
            BaseAddress = new Uri("http://localhost/"),
        };

        using (var outside = await client.PostAsync("/echo-idempotency-key", content: null, cancellationToken))
        {
            Assert.Equal(string.Empty, await outside.Content.ReadAsStringAsync(cancellationToken));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/echo-idempotency-key");
        request.Headers.Add(DurableHttpHeaders.IdempotencyKey, "chosen");
        using var response = await client.SendAsync(request, cancellationToken);
        Assert.Equal("chosen", await response.Content.ReadAsStringAsync(cancellationToken));
    }

    [Fact]
    public void DefaultErrorsDescribeOnlyTheOutcome()
    {
        Assert.Equal(
            new DurableOperationError("Failed", "The operation failed."),
            DurableHttpOptions.DefaultCreateError(new InvalidOperationException("Grain 'order/42' on silo 10.0.0.4 timed out.")));
        Assert.Equal(
            new DurableOperationError("Canceled", "The operation was canceled."),
            DurableHttpOptions.DefaultCreateError(new OperationCanceledException("internal detail")));
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("wait=5", 5)]
    [InlineData("respond-async, wait=7", 7)]
    [InlineData("return=minimal; foo=bar, WAIT = 3", 3)]
    [InlineData("wait=\"4\"", 4)]
    [InlineData("wait=600", 60)]
    [InlineData("wait=-1", 0)]
    [InlineData("wait=soon", 0)]
    [InlineData("respond-async", 0)]
    public void PreferWaitIsCappedAndUnparsablePreferencesAreIgnored(string? prefer, int expectedSeconds)
    {
        var headers = new HeaderDictionary();
        if (prefer is not null)
        {
            headers["Prefer"] = prefer;
        }

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), DurableHttpRequestHeaders.GetWait(headers, TimeSpan.FromMinutes(1)));
    }

    [Theory]
    [InlineData("abc", "abc")]
    [InlineData("\"abc\"", "abc")]
    [InlineData("\"a\\\"b\\\\c\"", "a\"b\\c")]
    [InlineData("  8e03978e-40d5-43e8-bc93-6894a57f9324 ", "8e03978e-40d5-43e8-bc93-6894a57f9324")]
    public void IdempotencyKeysAreBareOrStructuredFieldStrings(string header, string expected)
    {
        var headers = new HeaderDictionary { [DurableHttpHeaders.IdempotencyKey] = header };

        Assert.True(DurableHttpRequestHeaders.TryGetIdempotencyKey(headers, out var key, out _));
        Assert.Equal(expected, key);
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"a\"b\"")]
    [InlineData("\"a\\b\"")]
    [InlineData("k\u00e9y")]
    public void InvalidIdempotencyKeysAreReported(string header)
    {
        var headers = new HeaderDictionary { [DurableHttpHeaders.IdempotencyKey] = header };

        Assert.False(DurableHttpRequestHeaders.TryGetIdempotencyKey(headers, out var key, out var error));
        Assert.Null(key);
        Assert.NotNull(error);
    }

    /// <summary>Represents code which runs outside every grain, which requests outside durable tasks never consult.</summary>
    private sealed class NoGrainContextAccessor : IGrainContextAccessor
    {
        public IGrainContext GrainContext => throw new InvalidOperationException("Requests outside durable tasks don't identify a grain.");
    }
}
