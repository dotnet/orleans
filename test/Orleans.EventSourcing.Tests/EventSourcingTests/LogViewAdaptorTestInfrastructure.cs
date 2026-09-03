using System.Collections;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Orleans.EventSourcing;
using Orleans.EventSourcing.Common;
using Orleans.EventSourcing.CustomStorage;
using Orleans.Runtime;
using Xunit;

namespace Tester.EventSourcingTests;

internal sealed class TestLogView
{
    public TestLogView()
    {
    }

    public TestLogView(IEnumerable<string> entries) => Entries.AddRange(entries);

    public List<string> Entries { get; } = [];

    public TestLogView Copy() => new(Entries);
}

internal sealed record TestLogEntry(string Value);

internal sealed class TestSubmissionEntry : SubmissionEntry<TestLogEntry>;

internal class RecordingLogViewAdaptorHost : ILogViewAdaptorHost<TestLogView, TestLogEntry>
{
    private readonly object _lock = new();
    private bool _throwOnNextConfirmedChange;
    private bool _throwOnNextUpdate;

    public List<(bool Tentative, bool Confirmed)> ViewChanges { get; } = [];

    public List<ConnectionIssue> ConnectionIssues { get; } = [];

    public List<ConnectionIssue> ResolvedConnectionIssues { get; } = [];

    public void ThrowOnNextConfirmedChange() => _throwOnNextConfirmedChange = true;

    public void ThrowOnNextUpdate() => _throwOnNextUpdate = true;

    public virtual void UpdateView(TestLogView view, TestLogEntry entry)
    {
        if (_throwOnNextUpdate)
        {
            _throwOnNextUpdate = false;
            throw new InvalidOperationException("view update failed");
        }

        view.Entries.Add(entry.Value);
    }

    public void OnViewChanged(bool tentative, bool confirmed)
    {
        lock (_lock)
        {
            ViewChanges.Add((tentative, confirmed));
        }

        if (confirmed && _throwOnNextConfirmedChange)
        {
            _throwOnNextConfirmedChange = false;
            throw new InvalidOperationException("view-change callback failed");
        }
    }

    public void OnConnectionIssue(ConnectionIssue issue)
    {
        lock (_lock)
        {
            ConnectionIssues.Add(issue);
        }
    }

    public void OnConnectionIssueResolved(ConnectionIssue issue)
    {
        lock (_lock)
        {
            ResolvedConnectionIssues.Add(issue);
        }
    }
}

internal enum CustomStorageApplyBehavior
{
    Success,
    Conflict,
    ThrowProtocolTransportException,
}

internal sealed class DeterministicCustomStorageHost
    : RecordingLogViewAdaptorHost, ICustomStorageInterface<TestLogView, TestLogEntry>
{
    private TestLogView _state = new();
    private int _version;

    public CustomStorageApplyBehavior NextApplyBehavior { get; set; }

    public int ApplyCount { get; private set; }

    public int ReadCount { get; private set; }

    public int ClearCount { get; private set; }

    public int LastExpectedVersion { get; private set; }

    public IReadOnlyList<string> LastUpdates { get; private set; } = [];

    public TestLogView StoredState => _state.Copy();

    public int StoredVersion => _version;

    public void SetStoredState(IEnumerable<string> entries, int version)
    {
        _state = new TestLogView(entries);
        _version = version;
    }

    public Task<KeyValuePair<int, TestLogView>> ReadStateFromStorage()
    {
        ReadCount++;
        return Task.FromResult(new KeyValuePair<int, TestLogView>(_version, _state.Copy()));
    }

    public Task<bool> ApplyUpdatesToStorage(IReadOnlyList<TestLogEntry> updates, int expectedVersion)
    {
        ApplyCount++;
        LastExpectedVersion = expectedVersion;
        LastUpdates = updates.Select(entry => entry.Value).ToArray();

        var behavior = NextApplyBehavior;
        NextApplyBehavior = CustomStorageApplyBehavior.Success;
        if (behavior == CustomStorageApplyBehavior.Conflict || expectedVersion != _version)
        {
            return Task.FromResult(false);
        }

        if (behavior == CustomStorageApplyBehavior.ThrowProtocolTransportException)
        {
            throw new ProtocolTransportException(
                "forwarded custom storage failure",
                new InvalidOperationException("custom storage unavailable"));
        }

        foreach (var update in updates)
        {
            _state.Entries.Add(update.Value);
        }

        _version += updates.Count;
        return Task.FromResult(true);
    }

    public Task ClearStoredState()
    {
        ClearCount++;
        _state = new TestLogView();
        _version = 0;
        return Task.CompletedTask;
    }
}

internal sealed class RecordingProtocolServices : ILogConsistencyProtocolServices
{
    private readonly object _lock = new();

    public GrainId GrainId { get; } = GrainId.Create("primary-adaptor-test", "1");

    public string MyClusterId => "test-cluster";

    public List<(string Callback, string Where, Exception Exception)> UserCodeExceptions { get; } = [];

    public List<string> ProtocolErrors { get; } = [];

    public T DeepCopy<T>(T value) => value switch
    {
        TestLogView view => (T)(object)view.Copy(),
        TestLogEntry entry => (T)(object)new TestLogEntry(entry.Value),
        _ => value,
    };

    public void ProtocolError(string msg, bool throwexception)
    {
        lock (_lock)
        {
            ProtocolErrors.Add(msg);
        }
    }

    public void CaughtException(string where, Exception e)
    {
        lock (_lock)
        {
            ProtocolErrors.Add($"{where}: {e.Message}");
        }
    }

    public void CaughtUserCodeException(string callback, string where, Exception e)
    {
        lock (_lock)
        {
            UserCodeExceptions.Add((callback, where, e));
        }
    }

    public void Log(LogLevel level, string format, params object[] args)
    {
    }
}

internal sealed class ControlledOperation<T>
{
    private readonly TaskCompletionSource<bool> _started =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<T> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Started => _started.Task;

    public Task<T> Completion => _completion.Task;

    public void Complete(T result) => _completion.TrySetResult(result);

    public void Fail(Exception exception) => _completion.TrySetException(exception);

    internal void SignalStarted() => _started.TrySetResult(true);
}

internal sealed record PrimaryReadResult(TestLogView View, int Version);

internal sealed class TestPrimaryBasedLogViewAdaptor
    : PrimaryBasedLogViewAdaptor<TestLogView, TestLogEntry, TestSubmissionEntry>
{
    private readonly object _stateLock = new();
    private readonly ConcurrentQueue<ControlledOperation<PrimaryReadResult>> _reads = new();
    private readonly ConcurrentQueue<ControlledOperation<int>> _writes = new();
    private readonly ConcurrentQueue<ControlledOperation<bool>> _clears = new();
    private TestLogView _confirmed = new();
    private int _confirmedVersion;
    private int _activePrimaryOperations;
    private int _maximumConcurrentPrimaryOperations;

    public TestPrimaryBasedLogViewAdaptor(
        RecordingLogViewAdaptorHost host,
        TestLogView initialState,
        RecordingProtocolServices services)
        : base(host, initialState, services)
    {
    }

    public List<string> OperationLog { get; } = [];

    public List<(string Type, int Version)> NotificationTrace { get; } = [];

    public int ReadCount { get; private set; }

    public int WriteCount { get; private set; }

    public int ClearCount { get; private set; }

    public int MaximumConcurrentPrimaryOperations => Volatile.Read(ref _maximumConcurrentPrimaryOperations);

    public ControlledOperation<PrimaryReadResult> QueueRead(TestLogView view, int version)
    {
        var operation = new ControlledOperation<PrimaryReadResult>();
        _reads.Enqueue(operation);
        return operation;
    }

    public ControlledOperation<int> QueueWrite()
    {
        var operation = new ControlledOperation<int>();
        _writes.Enqueue(operation);
        return operation;
    }

    public ControlledOperation<bool> QueueClear()
    {
        var operation = new ControlledOperation<bool>();
        _clears.Enqueue(operation);
        return operation;
    }

    protected override void InitializeConfirmedView(TestLogView initialstate)
    {
        lock (_stateLock)
        {
            _confirmed = initialstate.Copy();
            _confirmedVersion = 0;
        }
    }

    protected override TestLogView LastConfirmedView()
    {
        lock (_stateLock)
        {
            return _confirmed;
        }
    }

    protected override int GetConfirmedVersion()
    {
        lock (_stateLock)
        {
            return _confirmedVersion;
        }
    }

    protected override async Task ReadAsync()
    {
        if (!_reads.TryDequeue(out var operation))
        {
            throw new InvalidOperationException("No read operation was queued.");
        }

        EnterPrimaryOperation("read:start");
        ReadCount++;
        operation.SignalStarted();
        try
        {
            var result = await operation.Completion;
            lock (_stateLock)
            {
                _confirmed = result.View.Copy();
                _confirmedVersion = result.Version;
            }
        }
        finally
        {
            ExitPrimaryOperation("read:end");
        }
    }

    protected override async Task<int> WriteAsync()
    {
        if (!_writes.TryDequeue(out var operation))
        {
            throw new InvalidOperationException("No write operation was queued.");
        }

        var updates = GetCurrentBatchOfUpdates();
        EnterPrimaryOperation("write:start");
        WriteCount++;
        operation.SignalStarted();
        try
        {
            var count = await operation.Completion;
            lock (_stateLock)
            {
                for (var index = 0; index < count; index++)
                {
                    _confirmed.Entries.Add(updates[index].Entry!.Value);
                }

                _confirmedVersion += count;
            }

            return count;
        }
        finally
        {
            ExitPrimaryOperation("write:end");
        }
    }

    protected override TestSubmissionEntry MakeSubmissionEntry(TestLogEntry entry) => new() { Entry = entry };

    protected override async Task ClearPrimaryLogAsync(CancellationToken cancellationToken)
    {
        if (!_clears.TryDequeue(out var operation))
        {
            throw new InvalidOperationException("No clear operation was queued.");
        }

        EnterPrimaryOperation("clear:start");
        ClearCount++;
        operation.SignalStarted();
        try
        {
            await operation.Completion.WaitAsync(cancellationToken);
        }
        finally
        {
            ExitPrimaryOperation("clear:end");
        }
    }

    protected override void OnNotificationReceived(INotificationMessage payload)
    {
        NotificationTrace.Add((payload.GetType().Name, payload.Version));
        base.OnNotificationReceived(payload);
    }

    private void EnterPrimaryOperation(string operation)
    {
        lock (OperationLog)
        {
            OperationLog.Add(operation);
        }

        var active = Interlocked.Increment(ref _activePrimaryOperations);
        int observed;
        while (active > (observed = Volatile.Read(ref _maximumConcurrentPrimaryOperations)))
        {
            if (Interlocked.CompareExchange(ref _maximumConcurrentPrimaryOperations, active, observed) == observed)
            {
                break;
            }
        }
    }

    private void ExitPrimaryOperation(string operation)
    {
        Interlocked.Decrement(ref _activePrimaryOperations);
        lock (OperationLog)
        {
            OperationLog.Add(operation);
        }
    }
}

internal sealed class SingleUseEnumerable<T>(IEnumerable<T> values) : IEnumerable<T>
{
    public int EnumerationCount { get; private set; }

    public IEnumerator<T> GetEnumerator()
    {
        EnumerationCount++;
        if (EnumerationCount != 1)
        {
            throw new InvalidOperationException("The enumerable was enumerated more than once.");
        }

        return values.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal static class TestPhase
{
    private static readonly TimeSpan WatchdogTimeout = TimeSpan.FromSeconds(15);

    public static async Task Await(Task task, string phase)
    {
        try
        {
            await task.WaitAsync(WatchdogTimeout, TestContext.Current.CancellationToken);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"Timed out waiting for phase '{phase}'.", exception);
        }
    }

    public static async Task<T> Await<T>(Task<T> task, string phase)
    {
        try
        {
            return await task.WaitAsync(WatchdogTimeout, TestContext.Current.CancellationToken);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"Timed out waiting for phase '{phase}'.", exception);
        }
    }
}
