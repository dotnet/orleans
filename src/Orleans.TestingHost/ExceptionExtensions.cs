using System;
using System.Linq;

namespace Orleans.TestingHost;

internal static class ExceptionExtensions
{
    internal static bool IsCancellation(this Exception exception)
    {
        if (exception is OperationCanceledException)
        {
            return true;
        }

        if (exception is not AggregateException aggregate)
        {
            return false;
        }

        var innerExceptions = aggregate.Flatten().InnerExceptions;
        return innerExceptions.Count > 0
            && innerExceptions.All(static inner => inner is OperationCanceledException);
    }
}
