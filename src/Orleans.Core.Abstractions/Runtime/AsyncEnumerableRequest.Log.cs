using System;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime;

internal sealed partial class AsyncEnumeratorProxy<T>
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to dispose async enumerator."
    )]
    private static partial void LogWarningFailedToDisposeAsyncEnumerator(ILogger logger, Exception exception);
}
