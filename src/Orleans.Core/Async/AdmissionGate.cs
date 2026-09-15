using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Internal;

/// <summary>
/// Admits concurrent operations until permanently closed, then asynchronously waits for admitted operations to finish.
/// </summary>
/// <remarks>
/// All gate methods support concurrent callers. Store each <see cref="TryEnter"/> result in a <c>using</c>
/// local and check <see cref="Admission.Entered"/> before starting work. Each admitted token has a single
/// owner which disposes it exactly once. The gate supports up to <see cref="int.MaxValue"/> outstanding entry attempts.
/// </remarks>
internal sealed class AdmissionGate
{
    private const int Closed = int.MinValue;
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // The sign bit closes admission; the remaining bits count entry attempts until rejected or completed.
    // Updating both in one atomic state orders every admission against closure.
    private int _state;

    /// <summary>
    /// Attempts to admit an operation, ordered atomically against <see cref="Close"/>.
    /// </summary>
    /// <returns>
    /// A token whose <see cref="Admission.Entered"/> property indicates whether the operation was admitted.
    /// Dispose the token when the operation finishes, preferably with a <c>using</c> declaration.
    /// </returns>
    public Admission TryEnter()
    {
        if (Interlocked.Increment(ref _state) > 0)
        {
            return new(this);
        }

        // A rejected attempt still owns a count and may be the last one to release it.
        Exit();
        return default;
    }

    private void Exit()
    {
        if (Interlocked.Decrement(ref _state) == Closed)
        {
            _drained.TrySetResult();
        }
    }

    /// <summary>
    /// Permanently closes admission before returning, and completes the returned task once all admitted tokens have been disposed.
    /// </summary>
    /// <returns>
    /// The shared drain task, also returned by subsequent calls. Pending continuations run asynchronously.
    /// </returns>
    public Task Close()
    {
        if (Interlocked.Or(ref _state, Closed) == 0)
        {
            _drained.TrySetResult();
        }

        return _drained.Task;
    }

    /// <summary>
    /// Represents scoped ownership of an admission. The default token represents a rejected entry and is safe to dispose.
    /// </summary>
    /// <remarks>
    /// Keep the token in a single <c>using</c> local. Dispose an admitted token exactly once across all copies.
    /// </remarks>
    public readonly struct Admission : IDisposable
    {
        private readonly AdmissionGate? _gate;

        internal Admission(AdmissionGate gate) => _gate = gate;

        /// <summary>
        /// Gets whether the operation was admitted.
        /// </summary>
        public bool Entered => _gate is not null;

        /// <summary>
        /// Releases this token's admission when its operation finishes.
        /// </summary>
        public void Dispose() => _gate?.Exit();
    }
}
