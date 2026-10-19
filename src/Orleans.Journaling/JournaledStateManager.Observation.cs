using System.Diagnostics;

namespace Orleans.Journaling;

internal partial class JournaledStateManager
{
    private JournaledStateObservers? _observers;

    // Write versions number captured journal writes. Version zero precedes this manager's first write.
    // Each write captures every state, and writes are captured and acknowledged one at a time, so at most one captured
    // version awaits acknowledgement.
    private long _capturedWriteVersion;
    private long _committedWriteVersion;

    /// <summary>
    /// Gets this manager's observer hub, or <see langword="null"/> when no state has been watched.
    /// </summary>
    internal JournaledStateObservers? Observers => _observers;

    /// <summary>
    /// Gets the version of the most recently captured write.
    /// </summary>
    internal long CapturedWriteVersion => _capturedWriteVersion;

    /// <summary>
    /// Gets the version of the most recently acknowledged write.
    /// </summary>
    internal long CommittedWriteVersion => _committedWriteVersion;

    /// <summary>
    /// Gets the version of the write which will capture changes staged now.
    /// </summary>
    internal long StagingWriteVersion => _capturedWriteVersion + 1;

    /// <summary>
    /// Gets the observer hub, creating it for the first watcher.
    /// </summary>
    /// <remarks>Watching requires a recovered manager, so that the first snapshot reflects recovered state.</remarks>
    /// <exception cref="InvalidOperationException">The manager is not initialized, or a failed operation fenced it.</exception>
    internal JournaledStateObservers GetOrCreateObservers()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            ThrowIfStateOperationsUnavailable();
            return _observers ??= new JournaledStateObservers(this, _committedWriteVersion);
        }
    }

    // Called under the manager lock after every state's pending entries were captured. Returns the write's version.
    private long OnWriteCaptured()
    {
        Debug.Assert(_capturedWriteVersion == _committedWriteVersion, "Each captured write is acknowledged before the next capture.");
        var version = ++_capturedWriteVersion;
        _observers?.OnWriteCaptured(version);
        return version;
    }

    // Called under the manager lock after every state has observed the write's acknowledgement.
    private void OnWriteAcknowledged(long writeVersion)
    {
        Debug.Assert(writeVersion > 0 && writeVersion == _capturedWriteVersion);
        _committedWriteVersion = writeVersion;
        _observers?.OnWriteCommitted(writeVersion);
    }

    private void CompleteObservers(Exception? failure) => _observers?.Complete(failure);
}
