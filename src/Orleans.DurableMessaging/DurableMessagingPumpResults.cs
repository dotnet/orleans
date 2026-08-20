using Orleans.DurableJobs;
using Orleans.Runtime;

namespace Orleans.DurableMessaging;

internal readonly record struct DurableMessagingPumpExecutionKey(string JobName, string JobId, string RunId);

internal sealed class DurableMessagingPumpResults
{
    private readonly Dictionary<DurableMessagingPumpExecutionKey, Entry> _entries = [];

    public bool TryStart(DurableMessagingPumpExecutionKey key) =>
        _entries.TryAdd(key, new Entry());

    public void Complete(DurableMessagingPumpExecutionKey key, DurableJobRunResult result) =>
        _entries[key].Result = result;

    public void Fail(DurableMessagingPumpExecutionKey key, Exception exception) =>
        _entries[key].Exception = exception;

    public bool TryTake(
        DurableMessagingPumpExecutionKey key,
        out DurableJobRunResult? result,
        out Exception? exception)
    {
        if (!_entries.TryGetValue(key, out var entry)
            || entry.Result is null && entry.Exception is null)
        {
            result = null;
            exception = null;
            return false;
        }

        _entries.Remove(key);
        result = entry.Result;
        exception = entry.Exception;
        return true;
    }

    private sealed class Entry
    {
        public DurableJobRunResult? Result { get; set; }
        public Exception? Exception { get; set; }
    }
}

internal sealed class OneShotTimerHandle
{
    private readonly object _lock = new();
    private IGrainTimer? _timer;
    private bool _completed;

    public void Attach(IGrainTimer timer)
    {
        lock (_lock)
        {
            if (_completed)
            {
                timer.Dispose();
            }
            else
            {
                _timer = timer;
            }
        }
    }

    public void Complete()
    {
        lock (_lock)
        {
            _completed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
