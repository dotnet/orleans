using System.Net;
using Microsoft.Azure.Cosmos;
using Orleans.Reminders.Cosmos;
using Xunit;

namespace Tester.Cosmos.Reminders;

public class CosmosReadRetryPolicyTests
{
    [Fact]
    public async Task ExecuteAsync_RequestTimeoutThenSuccess_RetriesRead()
    {
        var exception = CreateCosmosException(HttpStatusCode.RequestTimeout);
        var attempts = 0;
        var retries = new List<(CosmosException Exception, int Retry, TimeSpan Delay)>();

        var result = await CosmosReadRetryPolicy.ExecuteAsync(
            () =>
            {
                attempts++;
                return attempts == 1 ? Task.FromException<int>(exception) : Task.FromResult(42);
            },
            (exception, retry, delay) => retries.Add((exception, retry, delay)),
            static _ => Task.CompletedTask);

        Assert.Equal(42, result);
        Assert.Equal(2, attempts);
        var retry = Assert.Single(retries);
        Assert.Same(exception, retry.Exception);
        Assert.Equal(1, retry.Retry);
        Assert.Equal(TimeSpan.FromMilliseconds(100), retry.Delay);
    }

    [Fact]
    public async Task ExecuteAsync_PersistentRequestTimeout_StopsAfterMaximumRetries()
    {
        var exception = CreateCosmosException(HttpStatusCode.RequestTimeout);
        var attempts = 0;
        var retryDelays = new List<TimeSpan>();

        var actual = await Assert.ThrowsAsync<CosmosException>(() =>
            CosmosReadRetryPolicy.ExecuteAsync<int>(
                () =>
                {
                    attempts++;
                    return Task.FromException<int>(exception);
                },
                (_, _, delay) => retryDelays.Add(delay),
                static _ => Task.CompletedTask));

        Assert.Same(exception, actual);
        Assert.Equal(CosmosReadRetryPolicy.MaxRetries + 1, attempts);
        Assert.Equal(
            [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200)],
            retryDelays);
    }

    [Fact]
    public async Task ExecuteAsync_NonTimeoutFailure_DoesNotRetry()
    {
        var exception = CreateCosmosException(HttpStatusCode.InternalServerError);
        var attempts = 0;
        var retryCount = 0;

        var actual = await Assert.ThrowsAsync<CosmosException>(() =>
            CosmosReadRetryPolicy.ExecuteAsync<int>(
                () =>
                {
                    attempts++;
                    return Task.FromException<int>(exception);
                },
                (_, _, _) => retryCount++,
                static _ => Task.CompletedTask));

        Assert.Same(exception, actual);
        Assert.Equal(1, attempts);
        Assert.Equal(0, retryCount);
    }

    private static CosmosException CreateCosmosException(HttpStatusCode statusCode) =>
        new("Test failure", statusCode, 0, "test-activity", 0);
}
