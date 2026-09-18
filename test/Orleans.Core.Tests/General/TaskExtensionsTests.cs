using System.Runtime.CompilerServices;
using Xunit;

namespace UnitTests.UtilsTests;

[TestCategory("Utils"), TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
public class TaskExtensionsTests
{
    [Fact]
    public void WhenAll_EmptyInputCompletesSynchronously()
    {
        var result = PublicOrleansTaskExtensions.WhenAllWithAggregateException([]);

        Assert.True(result.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WhenAll_AwaitsEverySuccessfulTask()
    {
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = PublicOrleansTaskExtensions.WhenAllWithAggregateException([first.Task, second.Task]);

        first.SetResult();
        Assert.False(result.IsCompleted);
        second.SetResult();
        await result.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WhenAll_SingleFailurePreservesExceptionAndStackAfterOtherTasksComplete()
    {
        var failure = new InvalidOperationException("Original failure.");
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = PublicOrleansTaskExtensions.WhenAllWithAggregateException([ThrowFromOriginalSite(failure), pending.Task]);

        Assert.False(result.IsCompleted);
        pending.SetResult();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => result.WaitAsync(TestContext.Current.CancellationToken));

        Assert.Same(failure, exception);
        Assert.Contains(nameof(ThrowFromOriginalSite), exception.StackTrace);
        Assert.True(result.IsFaulted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenAll_MultipleFailuresPreserveEveryException(bool alreadyCompleted)
    {
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstFailure = new InvalidOperationException("First failure.");
        var secondFailure = new UnauthorizedAccessException("Second failure.");
        var result = PublicOrleansTaskExtensions.WhenAllWithAggregateException(alreadyCompleted
            ? [ThrowFromOriginalSite(firstFailure), ThrowFromOriginalSite(secondFailure)]
            : [first.Task, second.Task]);
        if (!alreadyCompleted)
        {
            second.SetException(secondFailure);
            Assert.False(result.IsCompleted);
            first.SetException(firstFailure);
        }

        var exception = await Assert.ThrowsAsync<AggregateException>(() => result.WaitAsync(TestContext.Current.CancellationToken));

        Assert.Equal(2, exception.InnerExceptions.Count);
        Assert.Contains(firstFailure, exception.InnerExceptions);
        Assert.Contains(secondFailure, exception.InnerExceptions);
        if (alreadyCompleted)
        {
            Assert.All(exception.InnerExceptions, failure => Assert.Contains(nameof(ThrowFromOriginalSite), failure.StackTrace));
        }

        Assert.True(result.IsFaulted);
    }

    [Fact]
    public async Task WhenAll_SingleAggregateFailurePreservesOriginalAggregate()
    {
        var failure = new AggregateException(new InvalidOperationException(), new UnauthorizedAccessException());
        var result = PublicOrleansTaskExtensions.WhenAllWithAggregateException([ThrowFromOriginalSite(failure), Task.CompletedTask]);

        var exception = await Assert.ThrowsAsync<AggregateException>(() => result.WaitAsync(TestContext.Current.CancellationToken));

        Assert.Same(failure, exception);
        Assert.Contains(nameof(ThrowFromOriginalSite), exception.StackTrace);
        Assert.Equal(2, exception.InnerExceptions.Count);
    }

    [Fact]
    public async Task WhenAll_CancellationPreservesTokenAndWaitsForOtherTasks()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = PublicOrleansTaskExtensions.WhenAllWithAggregateException([Task.FromCanceled(cancellation.Token), pending.Task]);

        Assert.False(result.IsCompleted);
        pending.SetResult();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => result.WaitAsync(TestContext.Current.CancellationToken));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.True(result.IsCanceled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenAll_FailuresTakePrecedenceOverCancellation(bool multipleFailures)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var first = new InvalidOperationException("First failure.");
        var second = new UnauthorizedAccessException("Second failure.");
        var result = PublicOrleansTaskExtensions.WhenAllWithAggregateException(
            [Task.FromCanceled(cancellation.Token), Task.FromException(first), multipleFailures ? Task.FromException(second) : Task.CompletedTask]);

        if (multipleFailures)
        {
            var exception = await Assert.ThrowsAsync<AggregateException>(() => result.WaitAsync(TestContext.Current.CancellationToken));
            Assert.Equal(2, exception.InnerExceptions.Count);
            Assert.Contains(first, exception.InnerExceptions);
            Assert.Contains(second, exception.InnerExceptions);
        }
        else
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => result.WaitAsync(TestContext.Current.CancellationToken));
            Assert.Same(first, exception);
        }

        Assert.True(result.IsFaulted);
    }

    [Fact]
    public void WhenAll_EnumeratesInputOnce()
    {
        var enumerations = 0;
        var result = PublicOrleansTaskExtensions.WhenAllWithAggregateException(Tasks());

        Assert.True(result.IsCompletedSuccessfully);
        Assert.Equal(1, enumerations);

        IEnumerable<Task> Tasks()
        {
            enumerations++;
            yield return Task.CompletedTask;
            yield return Task.CompletedTask;
        }
    }

    [Fact]
    public void WhenAll_RejectsInvalidInputSynchronously()
    {
        var nullInput = Assert.Throws<ArgumentNullException>(() =>
        {
            _ = PublicOrleansTaskExtensions.WhenAllWithAggregateException(null!);
        });
        Assert.Equal("tasks", nullInput.ParamName);

        var nullElement = Assert.Throws<ArgumentException>(() =>
        {
            _ = PublicOrleansTaskExtensions.WhenAllWithAggregateException([Task.CompletedTask, null!]);
        });
        Assert.Equal("tasks", nullElement.ParamName);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task ThrowFromOriginalSite(Exception exception)
    {
        await Task.CompletedTask;
        throw exception;
    }
}
