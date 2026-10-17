using System.Diagnostics;

namespace Orleans.Journaling;

/// <summary>
/// Stops the code which runs within a scope from changing journaled state once the fence is closed.
/// </summary>
/// <remarks>
/// <para>
/// A host enters a fence around code whose journaled state changes it must stop at a point in time, such as a durable
/// task's attempt, which its host cancels when the attempt's changes can no longer be recorded. The fence follows the
/// code's asynchronous flow. Once the host closes it, every journaled state change made within it throws the exception
/// which the fence creates, before the change reaches memory, so the change can't commit with a later journal write.
/// A host closes the fence on the scheduler which runs the fenced code, so a synchronous run of that code observes one
/// state of the fence.
/// </para>
/// <para>
/// Each journaled state calls <see cref="ThrowIfClosed"/> before it changes memory: <see cref="IDurableValue{T}"/>,
/// the durable collections, <see cref="IDurableTaskCompletionSource{T}"/>, journaled <see cref="IPersistentState{T}"/>, and
/// journal writes requested through <see cref="IDurableStateManager.WriteStateAsync"/>. The host's own bookkeeping, which
/// runs within the fenced flow on the code's behalf, runs within <see cref="Suspend"/>. Changes made in place to a mutable
/// object which a journaled state holds don't pass through the state, so the fence can't stop them.
/// </para>
/// </remarks>
/// <param name="createException">Creates the exception which a change throws once the fence is closed, from <paramref name="state"/>.</param>
/// <param name="state">The state which <paramref name="createException"/> receives.</param>
internal sealed class JournaledStateMutationFence(Func<object?, Exception> createException, object? state)
{
    private static readonly AsyncLocal<JournaledStateMutationFence?> Current = new();

    // Whether any flow has entered a fence, so grains which never do pay one field read per change.
    private static volatile bool _entered;

    private volatile bool _closed;

    /// <summary>Gets whether the fence is closed, so changes within it throw.</summary>
    public bool IsClosed => _closed;

    /// <summary>Closes the fence: from now on, journaled state changes within it throw.</summary>
    public void Close() => _closed = true;

    /// <summary>Makes <paramref name="fence"/> apply to the calling flow until the returned scope is disposed.</summary>
    public static Scope Enter(JournaledStateMutationFence fence)
    {
        ArgumentNullException.ThrowIfNull(fence);
        _entered = true;
        var previous = Current.Value;
        Current.Value = fence;
        return new(previous);
    }

    /// <summary>Lifts the fence from the calling flow until the returned scope is disposed.</summary>
    public static Scope Suspend()
    {
        var previous = Current.Value;
        if (previous is not null)
        {
            Current.Value = null;
        }

        return new(previous);
    }

    /// <summary>Throws the fence's exception when the calling flow is within a closed fence.</summary>
    public static void ThrowIfClosed()
    {
        if (_entered && Current.Value is { IsClosed: true } fence)
        {
            throw fence.CreateException();
        }
    }

    private Exception CreateException()
    {
        var exception = createException(state);
        Debug.Assert(exception is not null, "The fence creates the exception which a change throws.");
        return exception;
    }

    /// <summary>Restores the fence which applied to the flow before <see cref="Enter"/> or <see cref="Suspend"/>.</summary>
    public readonly struct Scope(JournaledStateMutationFence? previous) : IDisposable
    {
        /// <inheritdoc/>
        public void Dispose()
        {
            if (!ReferenceEquals(Current.Value, previous))
            {
                Current.Value = previous;
            }
        }
    }
}
