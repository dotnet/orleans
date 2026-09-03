using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Orleans.Reminders.Cosmos;
using Polly;
using Xunit;

namespace Tester.Cosmos.Reminders;

public class CosmosReadRetryPolicyTests
{
    [Fact]
    public async Task Pipeline_RequestTimeoutThenSuccess_RetriesRead()
    {
        var exception = CreateCosmosException(HttpStatusCode.RequestTimeout);
        var attempts = 0;
        var pipeline = CreatePipeline();

        var result = await pipeline.ExecuteAsync(
            _ =>
            {
                attempts++;
                return attempts == 1
                    ? ValueTask.FromException<int>(exception)
                    : ValueTask.FromResult(42);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(42, result);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Pipeline_PersistentRequestTimeout_StopsAfterMaximumRetries()
    {
        var exception = CreateCosmosException(HttpStatusCode.RequestTimeout);
        var attempts = 0;
        var pipeline = CreatePipeline();

        var actual = await Assert.ThrowsAsync<CosmosException>(() =>
            pipeline.ExecuteAsync<int>(
                _ =>
                {
                    attempts++;
                    return ValueTask.FromException<int>(exception);
                },
                TestContext.Current.CancellationToken).AsTask());

        Assert.Same(exception, actual);
        Assert.Equal(CosmosReadRetryPolicy.MaxRetryAttempts + 1, attempts);
    }

    [Fact]
    public async Task Pipeline_PersistentRequestTimeout_UsesDefaultLinearBackoff()
    {
        var exception = CreateCosmosException(HttpStatusCode.RequestTimeout);
        var timeProvider = new FakeTimeProvider();
        var attemptTimes = new List<DateTimeOffset>();
        var attempts = Enumerable.Range(0, CosmosReadRetryPolicy.MaxRetryAttempts + 1)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        var pipeline = CosmosReadRetryPolicy.CreatePipeline(
            NullLogger<CosmosReminderTable>.Instance,
            timeProvider);

        var execution = pipeline.ExecuteAsync<int>(
            _ =>
            {
                var attempt = attemptTimes.Count;
                attemptTimes.Add(timeProvider.GetUtcNow());
                attempts[attempt].SetResult();
                return ValueTask.FromException<int>(exception);
            },
            TestContext.Current.CancellationToken).AsTask();

        await attempts[0].Task.WaitAsync(TestContext.Current.CancellationToken);
        timeProvider.Advance(TimeSpan.FromMilliseconds(99));
        Assert.False(attempts[1].Task.IsCompleted);
        timeProvider.Advance(TimeSpan.FromMilliseconds(1));
        await attempts[1].Task.WaitAsync(TestContext.Current.CancellationToken);

        timeProvider.Advance(TimeSpan.FromMilliseconds(199));
        Assert.False(attempts[2].Task.IsCompleted);
        timeProvider.Advance(TimeSpan.FromMilliseconds(1));
        await attempts[2].Task.WaitAsync(TestContext.Current.CancellationToken);

        var actual = await Assert.ThrowsAsync<CosmosException>(() => execution);

        Assert.Same(exception, actual);
        Assert.Equal(
            [TimeSpan.Zero, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(300)],
            attemptTimes.Select(time => time - attemptTimes[0]));
    }

    [Fact]
    public async Task Pipeline_NonTimeoutFailure_DoesNotRetry()
    {
        var exception = CreateCosmosException(HttpStatusCode.InternalServerError);
        var attempts = 0;
        var pipeline = CreatePipeline();

        var actual = await Assert.ThrowsAsync<CosmosException>(() =>
            pipeline.ExecuteAsync<int>(
                _ =>
                {
                    attempts++;
                    return ValueTask.FromException<int>(exception);
                },
                TestContext.Current.CancellationToken).AsTask());

        Assert.Same(exception, actual);
        Assert.Equal(1, attempts);
    }

    private static ResiliencePipeline CreatePipeline() =>
        CosmosReadRetryPolicy.CreatePipeline(
            NullLogger<CosmosReminderTable>.Instance,
            TimeProvider.System,
            TimeSpan.Zero);

    private static CosmosException CreateCosmosException(HttpStatusCode statusCode) =>
        new("Test failure", statusCode, 0, "test-activity", 0);
}
