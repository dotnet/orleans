using System;
using System.Threading;

namespace Orleans.Runtime;

/// <summary>
/// Keeps calls initiated in this scope pending after caller cancellation until a response or another
/// terminal runtime outcome, including the configured response timeout or host shutdown.
/// </summary>
/// <remarks>
/// Enclose creation of the proxy task in <see cref="Enter"/> and dispose the scope before awaiting an
/// independently bounded local wait. Cancellation still signals the callee. Pending requests describe
/// the caller's observable lifetime; remote execution can continue after a runtime timeout.
/// The scope flows with the execution context and restores the previous value when disposed in lexical
/// order. The invocation boundary captures and suppresses it before running outgoing filters, so each
/// filter's additional calls select their own policy.
/// </remarks>
internal readonly struct CancellationAcknowledgementScope : IDisposable
{
    private static readonly AsyncLocal<bool> Current = new();
    private readonly bool _previous;

    private CancellationAcknowledgementScope(bool enabled)
    {
        _previous = Current.Value;
        if (_previous != enabled)
        {
            Current.Value = enabled;
        }
    }

    /// <summary>Enables cancellation acknowledgement waiting for calls initiated in this scope.</summary>
    public static CancellationAcknowledgementScope Enter() => new(enabled: true);

    /// <summary>Captures the caller's policy and clears it for invocation infrastructure.</summary>
    internal static CancellationAcknowledgementScope Capture() => new(enabled: false);

    internal bool WaitForAcknowledgement => _previous;

    public void Dispose()
    {
        if (Current.Value != _previous)
        {
            Current.Value = _previous;
        }
    }
}
