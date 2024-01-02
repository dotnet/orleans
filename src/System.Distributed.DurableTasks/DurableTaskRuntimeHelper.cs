
namespace System.Distributed.DurableTasks;

public static class DurableTaskRuntimeHelper
{
    /// <summary>
    /// Invokes a durable task.
    /// </summary>
    /// <param name="task">The task.</param>
    /// <param name="context">The task context.</param>
    /// <returns>The result of invocation.</returns>
    public static ValueTask<DurableTaskResponse> RunAsync(DurableTask task, DurableExecutionContext context) => task.RunAsync(context);
    public static Task CancelAsync(DurableExecutionContext context, CancellationToken cancellationToken) => context.CancelAsync(cancellationToken);

    public static void SetCurrentContext(DurableExecutionContext? context) => DurableExecutionContext.SetCurrentContext(context);
    public static void SetCurrentContext(DurableExecutionContext? context, out DurableExecutionContext? previous) => DurableExecutionContext.SetCurrentContext(context, out previous);
}
