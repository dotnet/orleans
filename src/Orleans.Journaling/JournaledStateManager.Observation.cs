using System.Diagnostics;

namespace Orleans.Journaling;

internal partial class JournaledStateManager
{
    private JournaledStateObservers? _observers;

    // Write versions number captured journal writes. Version zero precedes this manager's first write.
    // Writes are captured and acknowledged one at a time, so at most one captured version awaits acknowledgement.
    private long _capturedWriteVersion;
    private long _committedWriteVersion;
    // A prefix flush defers state callbacks and the observation watermark until every state can be captured.
    private bool _hasDeferredWriteAcknowledgement;

    /// <summary>
    /// Gets this manager's observer hub, or <see langword="null"/> when no state has been watched.
    /// </summary>
    internal JournaledStateObservers? Observers => Volatile.Read(ref _observers);

    /// <summary>
    /// Gets the version of the most recently captured write.
    /// </summary>
    internal long CapturedWriteVersion => Volatile.Read(ref _capturedWriteVersion);

    /// <summary>
    /// Gets the version of the write which will capture changes staged now.
    /// </summary>
    internal long StagingWriteVersion => Volatile.Read(ref _capturedWriteVersion) + 1;

    /// <summary>
    /// Gets the observer hub, creating it for the first watcher.
    /// </summary>
    internal JournaledStateObservers GetOrCreateObservers()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            ThrowIfFenced();
            _shutdownCancellation.Token.ThrowIfCancellationRequested();
            if (_observers is { } observers)
            {
                return observers;
            }

            observers = new JournaledStateObservers(this, _committedWriteVersion);
            Volatile.Write(ref _observers, observers);
            return observers;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<KeyValuePair<string, IStateMachine>> GetStateMachines()
    {
        lock (_lock)
        {
            ThrowIfFenced();
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            var result = new List<KeyValuePair<string, IStateMachine>>(_states.Count);
            foreach (var (name, state) in _states)
            {
                if (state is not RetiredStateMachine)
                {
                    result.Add(new(name, state));
                }
            }

            return result;
        }
    }

    // Called under the manager lock after capturing a write's journal entries.
    // Returns the write's version, or zero when the capture omitted state-owned pending entries.
    private long OnWriteCaptured(bool capturedAllStates)
    {
        Debug.Assert(_capturedWriteVersion == _committedWriteVersion, "Each captured write is acknowledged before the next capture.");
        if (!capturedAllStates)
        {
            _hasDeferredWriteAcknowledgement = true;
            return 0;
        }

        Volatile.Write(ref _capturedWriteVersion, _capturedWriteVersion + 1);
        return _capturedWriteVersion;
    }

    // Called under the manager lock after every state has observed the write's acknowledgement.
    private void OnWriteAcknowledged(long writeVersion)
    {
        Debug.Assert(writeVersion > 0 && writeVersion == _capturedWriteVersion);
        _hasDeferredWriteAcknowledgement = false;
        _committedWriteVersion = writeVersion;
        Volatile.Read(ref _observers)?.OnWriteCommitted(writeVersion);
    }

    private void CompleteObservers(Exception? failure) => Volatile.Read(ref _observers)?.Complete(failure);
}
