using Orleans.TestingHost.Utils;
using Xunit;

namespace Orleans.TestingHost.Tests;

[TestCategory("BVT")]
public class TestingUtilsTests
{
    [Fact]
    public async Task WaitUntilAsync_UntypedSingleParameterLambdaBindsLegacyOverload()
    {
        await TestingUtils.WaitUntilAsync(_ => Task.FromResult(true), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task WaitUntilSucceededAsync_ReturnsImmediatelyOnSuccess()
    {
        var calls = 0;

        var result = await TestingUtils.WaitUntilSucceededAsync(
            _ =>
            {
                calls++;
                return Task.FromResult(true);
            },
            TimeSpan.FromSeconds(1));

        Assert.True(result);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task WaitUntilSucceededAsync_RetriesUntilSuccess()
    {
        var calls = 0;

        var result = await TestingUtils.WaitUntilSucceededAsync(
            _ => Task.FromResult(++calls == 3),
            TimeSpan.FromSeconds(1),
            TimeSpan.Zero);

        Assert.True(result);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task WaitUntilSucceededAsync_TimesOutWhileWaitingToRetry()
    {
        var calls = 0;

        var result = await TestingUtils.WaitUntilSucceededAsync(
            _ =>
            {
                calls++;
                return Task.FromResult(false);
            },
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromSeconds(1));

        Assert.False(result);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task WaitUntilSucceededAsync_LongPredicateCrossingDeadlineDoesNotSucceed()
    {
        var predicateSettled = false;

        var result = await TestingUtils.WaitUntilSucceededAsync(
            async _ =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100));
                predicateSettled = true;
                return true;
            },
            TimeSpan.FromMilliseconds(25));

        Assert.False(result);
        Assert.True(predicateSettled);
    }

    [Fact]
    public async Task WaitUntilAsync_LegacyOverloadDoesNotInvokePredicateAfterDeadline()
    {
        var calls = 0;

        var exception = await Assert.ThrowsAsync<TimeoutException>(() => TestingUtils.WaitUntilAsync(
            async (bool _) =>
            {
                calls++;
                await Task.Delay(TimeSpan.FromMilliseconds(100));
                return false;
            },
            TimeSpan.FromMilliseconds(25),
            TimeSpan.FromSeconds(1)));

        Assert.Equal(1, calls);
        Assert.Contains(nameof(TestingUtilsTests), exception.Message);
        Assert.Contains("not invoked again after the deadline", exception.Message);
    }

    [Fact]
    public async Task WaitUntilAsync_LegacyOverloadInvokesFinalAttemptBeforeDeadline()
    {
        var calls = 0;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => TestingUtils.WaitUntilAsync(
            (bool lastTry) =>
            {
                calls++;
                if (lastTry)
                {
                    throw new InvalidOperationException("Expected legacy detailed failure");
                }

                return Task.FromResult(false);
            },
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1)));

        Assert.Equal("Expected legacy detailed failure", exception.Message);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task WaitUntilAsync_PropagatesDeadlineCancellationAndReportsPredicateExpression()
    {
        var calls = 0;

        var exception = await Assert.ThrowsAsync<TimeoutException>(() => TestingUtils.WaitUntilAsync(
            async (_, cancellationToken) =>
            {
                calls++;
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return false;
            },
            TimeSpan.FromMilliseconds(25)));

        Assert.Equal(1, calls);
        Assert.Contains("async (_, cancellationToken) =>", exception.Message);
    }

    [Fact]
    public async Task WaitUntilAsync_InvokesFinalAttemptBeforeDeadline()
    {
        var calls = 0;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => TestingUtils.WaitUntilAsync(
            (lastTry, _) =>
            {
                calls++;
                if (lastTry)
                {
                    throw new InvalidOperationException("Expected detailed failure");
                }

                return Task.FromResult(false);
            },
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1)));

        Assert.Equal("Expected detailed failure", exception.Message);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task WaitUntilSucceededAsync_PropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TestingUtils.WaitUntilSucceededAsync(
            async token =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return false;
            },
            TimeSpan.FromSeconds(10),
            cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task WaitUntilSucceededAsync_PropagatesPredicateException()
    {
        var exception = new InvalidOperationException("Expected failure");

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => TestingUtils.WaitUntilSucceededAsync(
            _ => Task.FromException<bool>(exception),
            TimeSpan.FromSeconds(1)));

        Assert.Same(exception, actual);
    }
}
