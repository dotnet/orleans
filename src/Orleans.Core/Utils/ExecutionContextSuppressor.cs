using System;
using System.Threading;

namespace Orleans.Runtime.Internal;

/// <summary>
/// Suppresses the flow of <see cref="ExecutionContext"/> until it is disposed.
/// </summary>
public struct ExecutionContextSuppressor : IDisposable
{
    private readonly bool _restoreFlow;

    /// <summary>
    /// Initializes a new <see cref="ExecutionContextSuppressor"/> instance.
    /// </summary>
    public ExecutionContextSuppressor()
    {
        if (!ExecutionContext.IsFlowSuppressed())
        {
            ExecutionContext.SuppressFlow();
            _restoreFlow = true;
        }
        else
        {
            _restoreFlow = false;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_restoreFlow)
        {
            ExecutionContext.RestoreFlow();
        }
    }
}
