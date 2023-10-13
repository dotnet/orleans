using System.Threading;

namespace Orleans.Runtime.Internal;

/// <summary>
/// Suppresses the flow of <see cref="ExecutionContext"/> until it is disposed.
/// </summary>
/// <remarks>
/// Note that this is a ref-struct to avoid it being used in an async method.
/// </remarks>
public ref struct ExecutionContextSuppressor
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

    /// <summary>
    /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
    /// </summary>
    public readonly void Dispose()
    {
        if (_restoreFlow)
        {
            ExecutionContext.RestoreFlow();
        }
    }
}
