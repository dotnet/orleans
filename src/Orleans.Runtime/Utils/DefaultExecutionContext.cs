using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime;

/// <summary>
/// Provides an <see cref="ExecutionContext"/> which contains no ambient state.
/// </summary>
internal static class DefaultExecutionContext
{
    public static ExecutionContext Instance { get; } = CaptureDefault();

    internal static ExecutionContext CaptureDefault()
    {
        var completion = new TaskCompletionSource<ExecutionContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        ThreadPool.UnsafeQueueUserWorkItem(
            static completion =>
            {
                try
                {
                    completion.SetResult(
                        ExecutionContext.Capture()
                            ?? throw new InvalidOperationException("Could not capture the default execution context."));
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
                }
            },
            completion,
            preferLocal: false);

        return completion.Task.GetAwaiter().GetResult();
    }
}
