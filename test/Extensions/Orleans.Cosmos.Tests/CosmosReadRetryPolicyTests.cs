using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;
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
