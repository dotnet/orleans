using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans;

/// <summary>
/// An alternative to <see cref="TaskCompletionSource{TResult}"/> which completes only once a specified number of signals have been received.
/// </summary>
internal sealed class MultiTaskCompletionSource
{
    private readonly TaskCompletionSource _tcs;
    private int _count;

    /// <summary>
    /// Initializes a new instance of the <see cref="MultiTaskCompletionSource"/> class.
    /// </summary>
    /// <param name="count">
    /// The number of signals which must occur before this completion source completes.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The count value is less than or equal to zero.
    /// </exception>
    public MultiTaskCompletionSource(int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(count, 0);
        _tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _count = count;
    }

    /// <summary>
    /// Gets the task which is completed when a sufficient number of signals are received.
    /// </summary>
    public Task Task => _tcs.Task;

    /// <summary>
    /// Signals this instance.
    /// </summary>
    /// <exception cref="InvalidOperationException">This method was called more times than the initially specified count argument allows.</exception>
    public void SetOneResult()
    {
        var current = Interlocked.Decrement(ref _count);
        if (current < 0)
        {
            throw new InvalidOperationException(
                "SetOneResult was called more times than initially specified by the count argument.");
        }

        if (current == 0)
        {
            _tcs.SetResult();
        }
    }
}
