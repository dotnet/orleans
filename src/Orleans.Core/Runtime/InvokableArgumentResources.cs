using System;
using Microsoft.Extensions.Logging;
using Orleans.Serialization.Invocation;

namespace Orleans.Runtime;

// Runtime logging preserves the call's outcome and keeps draining after cleanup failures.
// Without a logger, cleanup failures propagate to the caller.
internal static partial class InvokableArgumentResources
{
    internal static void Complete(IInvokableArgumentOwner? owner, ILogger? logger)
    {
        if (owner is null)
        {
            return;
        }

        try
        {
            owner.CompleteArgumentResources();
        }
        catch (Exception exception)
        {
            if (logger is null)
            {
                throw;
            }

            LogCleanupFailure(logger, exception);
        }
    }

    internal static void Release(IInvokableArgumentOwner? owner, ILogger? logger)
    {
        if (owner is null)
        {
            return;
        }

        try
        {
            owner.ReleaseArgumentResources();
        }
        catch (Exception exception)
        {
            if (logger is null)
            {
                throw;
            }

            LogCleanupFailure(logger, exception);
        }
    }

    internal static void Dispose(IInvokable request, ILogger? logger)
    {
        try
        {
            request.Dispose();
        }
        catch (Exception exception)
        {
            if (logger is null)
            {
                throw;
            }

            LogCleanupFailure(logger, exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error releasing explicitly owned RPC argument resources")]
    private static partial void LogCleanupFailure(ILogger logger, Exception exception);
}
