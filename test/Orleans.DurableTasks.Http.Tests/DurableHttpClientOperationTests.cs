using System.Net.Http.Json;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Http;
using Xunit;

namespace Orleans.DurableTasks.Http.Tests;

/// <summary>Exercises <see cref="IDurableHttpClient"/>: durable tasks which start, observe, signal, and cancel HTTP operations.</summary>
[Collection(DurableHttpCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class DurableHttpClientOperationTests(DurableHttpFixture fixture)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData("echo", DurableHttpWaitMode.Poll)]
    [InlineData("echo-later", DurableHttpWaitMode.Poll)]
    [InlineData("echo-later", DurableHttpWaitMode.Events)]
    public async Task TaskReturnsTheResultOfAnOperationWhichCompletesNowOrLater(string operation, DurableHttpWaitMode mode)
    {
        var response = await RunAsync($"/orders/{Guid.NewGuid():N}/{operation}", "hello", mode);

        Assert.Equal("hello", response.GetResult<string>());
    }

    [Fact]
    public async Task TaskReadsSignalsAndAwaitsTheEventsOfAnOperation()
    {
        var response = await CallAsync(caller => caller.ApproveRemoteOperationAsync(Guid.NewGuid().ToString("N"), "ada"));

        Assert.Equal("Running: widget approved by ada", response.GetResult<string>());
    }

    [Fact]
    public async Task TaskCancelsAnOperationWhichThenReportsItsCancellation()
    {
        var response = await CallAsync(caller => caller.CancelRemoteOperationAsync(Guid.NewGuid().ToString("N"), "the buyer withdrew"));

        Assert.Equal("Running: Canceled Canceled widget was canceled: the buyer withdrew", response.GetResult<string>());
    }

    [Fact]
    public async Task SendRecordsResponsesAndKeysUnsafeRequestsByTheirStep()
    {
        var response = await CallAsync(static caller => caller.SendRemoteRequestsAsync());

        var keys = response.GetResult<string[]>()!;
        Assert.Equal(43, keys[0].Length);
        Assert.Equal(string.Empty, keys[1]);
    }

    [Fact]
    public async Task WatchStreamsAnOperationsStatusesUntilItIsTerminal()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/orders/{Guid.NewGuid():N}/approval") { Content = JsonContent.Create(new ApprovalRequest("widget")) };
        request.Headers.Add(DurableHttpHeaders.IdempotencyKey, Guid.NewGuid().ToString());
        using var started = await fixture.Http.SendAsync(request, cancellationToken);
        var location = new Uri(Assert.Single(started.Headers.GetValues(DurableHttpHeaders.OperationLocation)));

        await using var statuses = fixture.DurableHttp.WatchOperationAsync(location, cancellationToken).GetAsyncEnumerator(cancellationToken);
        Assert.True(await statuses.MoveNextAsync().AsTask().WaitAsync(Timeout, cancellationToken));
        Assert.Equal(DurableOperationStatus.Running, statuses.Current.Status);

        using (var signal = await fixture.Http.PostAsJsonAsync($"{location}/signals/approved", "ada", cancellationToken))
        {
            signal.EnsureSuccessStatusCode();
        }

        Assert.True(await statuses.MoveNextAsync().AsTask().WaitAsync(Timeout, cancellationToken));
        Assert.Equal(DurableOperationStatus.Succeeded, statuses.Current.Status);
        Assert.Equal("widget approved by ada", statuses.Current.Result?.GetString());
        Assert.False(await statuses.MoveNextAsync().AsTask().WaitAsync(Timeout, cancellationToken));
    }

    [Fact]
    public async Task TaskFailsWithTheErrorOfAFailedOperation()
    {
        var response = await RunAsync($"/orders/{Guid.NewGuid():N}/fail-later", "widget", DurableHttpWaitMode.Poll);

        var exception = Assert.IsType<DurableHttpOperationException>(response.Exception);
        Assert.Equal(DurableOperationStatus.Failed, exception.Status);
        Assert.Equal(nameof(InvalidOperationException), exception.Code);
        Assert.Equal("widget failed.", exception.Message);
    }

    [Fact]
    public async Task TaskPollsAStatusMonitorWhichReportsOtherRunningStatuses()
    {
        var response = await RunAsync("/arm/jobs", "arm", DurableHttpWaitMode.Poll);

        Assert.Equal("arm", response.GetResult<string>());
    }

    [Fact]
    public async Task TaskPollsTheStatusMonitorOfACreatedResourceWhichItStillProvisions()
    {
        var response = await RunAsync("/arm/resources", "created", DurableHttpWaitMode.Poll);

        Assert.Equal("created", response.GetResult<string>());
    }

    [Fact]
    public async Task WatchReconnectsAfterItsConnectionDropsWithTheLastEventsIdentifier()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid().ToString("N");
        var stream = DurableHttpFixture.DroppedStreams.GetOrAdd(id, static _ => new());

        await using var statuses = fixture.DurableHttp.WatchOperationAsync(new Uri($"http://localhost/dropped/{id}"), cancellationToken).GetAsyncEnumerator(cancellationToken);
        Assert.True(await statuses.MoveNextAsync().AsTask().WaitAsync(Timeout, cancellationToken));
        Assert.Equal(DurableOperationStatus.Running, statuses.Current.Status);

        stream.Drop.SetResult();
        Assert.True(await statuses.MoveNextAsync().AsTask().WaitAsync(Timeout, cancellationToken));
        Assert.Equal(DurableOperationStatus.Succeeded, statuses.Current.Status);
        Assert.Equal("done", statuses.Current.Result?.GetString());
        Assert.False(await statuses.MoveNextAsync().AsTask().WaitAsync(Timeout, cancellationToken));
        Assert.Equal("Running", stream.LastEventId);
    }

    [Fact]
    public async Task TaskPollsALocationUntilItReturnsTheResult()
    {
        var response = await RunAsync("/jobs", "job", DurableHttpWaitMode.Events);

        Assert.Equal("job", response.GetResult<string>());
    }

    [Fact]
    public async Task TaskFailsWhenAResponseReportsNeitherAResultNorAnOperation()
    {
        var response = await RunAsync($"/orders/{Guid.NewGuid():N}/missing", "hello", DurableHttpWaitMode.Poll);

        var exception = Assert.IsType<HttpRequestException>(response.Exception);
        Assert.Contains("returned 404", exception.Message, StringComparison.Ordinal);
    }

    private Task<DurableTaskResponse> RunAsync(string path, string value, DurableHttpWaitMode mode) =>
        CallAsync(caller => caller.RunRemoteOperationAsync(path, value, mode));

    private async Task<DurableTaskResponse> CallAsync<T>(Func<IDurableHttpTestGrain, DurableTask<T>> call)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var caller = fixture.Client.GetGrain<IDurableHttpTestGrain>($"caller-{Guid.NewGuid():N}");
        var scheduled = await call(caller).WithId("call").ScheduleAsync(cancellationToken);
        return await scheduled.GetResponseAsync(cancellationToken).WaitAsync(Timeout, cancellationToken);
    }
}
