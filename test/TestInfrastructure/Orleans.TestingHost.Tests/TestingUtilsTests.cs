using Microsoft.Extensions.Time.Testing;
using Orleans.TestingHost.Utils;
using Xunit;

namespace Orleans.TestingHost.Tests;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("TestingHost")]
public class TestingUtilsTests
{
    [Fact]
    public async Task WaitUntilAsync_UntypedSingleParameterLambdaBindsLegacyOverload()
    {
#pragma warning disable xUnit1051 // This test verifies binding to the legacy overload without cancellation.
        await TestingUtils.WaitUntilAsync(
            _ => Task.FromResult(true),
            TimeSpan.FromSeconds(1));
#pragma warning restore xUnit1051
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
            TimeSpan.FromSeconds(1),
            cancellationToken: TestContext.Current.CancellationToken);

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
            TimeSpan.Zero,
            TestContext.Current.CancellationToken);

        Assert.True(result);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task WaitUntilSucceededAsync_ReturnsFalseAtDeadline()
    {
        var timeProvider = new FakeTimeProvider();
        var timeout = TimeSpan.FromSeconds(1);
        var predicateInvoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var waitTask = TestingUtils.WaitUntilSucceededAsync(
            _ =>
            {
                predicateInvoked.SetResult();
                return Task.FromResult(false);
            },
            timeout,
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken,
            timeProvider);

        await predicateInvoked.Task.WaitAsync(TestContext.Current.CancellationToken);
        timeProvider.Advance(timeout);

        Assert.False(await waitTask);
    }

    [Fact]
    public async Task WaitUntilSucceededAsync_PredicateCompletingBeforeDeadlineSucceeds()
    {
        var timeProvider = new FakeTimeProvider();
        var timeout = TimeSpan.FromSeconds(1);
        var predicateStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePredicate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var waitTask = TestingUtils.WaitUntilSucceededAsync(
            async _ =>
            {
                predicateStarted.SetResult();
                await releasePredicate.Task;
                return true;
            },
            timeout,
            delayOnFail: null,
            TestContext.Current.CancellationToken,
            timeProvider);

        await predicateStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        timeProvider.Advance(timeout - TimeSpan.FromTicks(1));
        releasePredicate.SetResult();

        Assert.True(await waitTask);
    }

    [Fact]
    public async Task WaitUntilSucceededAsync_PredicateCompletingAtDeadlineDoesNotSucceed()
    {
        var timeProvider = new FakeTimeProvider();
        var timeout = TimeSpan.FromSeconds(1);
        var predicateStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePredicate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var predicateSettled = false;

        var waitTask = TestingUtils.WaitUntilSucceededAsync(
            async _ =>
            {
                predicateStarted.SetResult();
                await releasePredicate.Task;
                predicateSettled = true;
                return true;
            },
            timeout,
            delayOnFail: null,
            TestContext.Current.CancellationToken,
            timeProvider);

        await predicateStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        timeProvider.Advance(timeout);
        releasePredicate.SetResult();
        var result = await waitTask;

        Assert.False(result);
        Assert.True(predicateSettled);
    }

    [Fact]
    public async Task WaitUntilAsync_LegacyOverloadDoesNotInvokePredicateAfterDeadline()
    {
        var timeProvider = new FakeTimeProvider();
        var timeout = TimeSpan.FromSeconds(1);
        var predicateStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePredicate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

#pragma warning disable xUnit1051 // This test verifies the legacy overload's deadline behavior.
        var waitTask = TestingUtils.WaitUntilAsync(
            async (bool _) =>
            {
                calls++;
                predicateStarted.SetResult();
                await releasePredicate.Task;
                return false;
            },
            timeout,
            TimeSpan.FromSeconds(1),
            timeProvider);
#pragma warning restore xUnit1051

        await predicateStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        timeProvider.Advance(timeout);
        releasePredicate.SetResult();
        var exception = await Assert.ThrowsAsync<TimeoutException>(() => waitTask);

        Assert.Equal(1, calls);
        Assert.Contains(nameof(TestingUtilsTests), exception.Message);
        Assert.Contains("not invoked again after the deadline", exception.Message);
    }

    [Fact]
    public async Task WaitUntilAsync_LegacyOverloadInvokesFinalAttemptBeforeDeadline()
    {
        var calls = 0;

#pragma warning disable xUnit1051 // This test verifies the legacy overload's final-attempt behavior.
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
#pragma warning restore xUnit1051

        Assert.Equal("Expected legacy detailed failure", exception.Message);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task WaitUntilAsync_PropagatesDeadlineCancellationAndReportsPredicateExpression()
    {
        var timeProvider = new FakeTimeProvider();
        var timeout = TimeSpan.FromSeconds(1);
        var predicateStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        var waitTask = TestingUtils.WaitUntilAsync(
            async (_, cancellationToken) =>
            {
                calls++;
                predicateStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return false;
            },
            timeout,
            delayOnFail: null,
            TestContext.Current.CancellationToken,
            timeProvider);

        await predicateStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        timeProvider.Advance(timeout);
        var exception = await Assert.ThrowsAsync<TimeoutException>(() => waitTask);

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
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken));

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
            TimeSpan.FromSeconds(1),
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Same(exception, actual);
    }
}
