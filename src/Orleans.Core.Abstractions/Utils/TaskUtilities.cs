using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;

namespace Orleans.Internal;

internal static class TaskUtilities
{
    /// <summary>
    /// Waits for every supplied task, propagating a single failure directly or multiple failures as an <see cref="AggregateException"/>.
    /// </summary>
    internal static Task WhenAllWithAggregateException(IEnumerable<Task> tasks)
    {
        var combined = Task.WhenAll(tasks);
        return combined.IsCompletedSuccessfully ? combined : AwaitCompletion(combined);

        static async Task AwaitCompletion(Task task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch when (task.Exception is { InnerExceptions.Count: > 1 } exception)
            {
                ExceptionDispatchInfo.Throw(exception);
            }
        }
    }
}
