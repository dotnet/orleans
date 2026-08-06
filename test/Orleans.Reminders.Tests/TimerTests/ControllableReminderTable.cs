#nullable enable

using Orleans.Runtime.ReminderService;

namespace UnitTests.TimerTests;

internal sealed class ControllableReminderTable(
    InMemoryReminderTable inner,
    ReminderTableReadController readController) : IReminderTable
{
    public Task StartAsync(CancellationToken cancellationToken = default) => ((IReminderTable)inner).StartAsync(cancellationToken);

    public Task<ReminderTableData> ReadRows(GrainId grainId) => inner.ReadRows(grainId);

    public async Task<ReminderTableData> ReadRows(uint begin, uint end)
    {
        var result = await inner.ReadRows(begin, end);
        await readController.OnRangeReadAsync(begin, end);
        return result;
    }

    public Task<ReminderEntry?> ReadRow(GrainId grainId, string reminderName) => inner.ReadRow(grainId, reminderName);

    public Task<string?> UpsertRow(ReminderEntry entry) => inner.UpsertRow(entry);

    public Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag) => inner.RemoveRow(grainId, reminderName, eTag);

    public Task TestOnlyClearTable() => inner.TestOnlyClearTable();

    public Task StopAsync(CancellationToken cancellationToken = default) => ((IReminderTable)inner).StopAsync(cancellationToken);
}

internal sealed class ReminderTableReadController
{
    private readonly object _lock = new();
    private readonly List<ReminderTableReadGate> _gates = [];

    public ReminderTableReadGate BlockNextRangeRead(GrainId grainId)
    {
        var gate = new ReminderTableReadGate(this, grainId.GetUniformHashCode());
        lock (_lock)
        {
            _gates.Add(gate);
        }

        return gate;
    }

    internal async Task OnRangeReadAsync(uint begin, uint end)
    {
        ReminderTableReadGate? gate = null;
        lock (_lock)
        {
            for (var i = 0; i < _gates.Count; i++)
            {
                if (_gates[i].Matches(begin, end))
                {
                    gate = _gates[i];
                    _gates.RemoveAt(i);
                    break;
                }
            }
        }

        if (gate is not null)
        {
            gate.MarkBlocked();
            await gate.WaitForReleaseAsync();
        }
    }

    internal void Remove(ReminderTableReadGate gate)
    {
        lock (_lock)
        {
            _gates.Remove(gate);
        }
    }
}

internal sealed class ReminderTableReadGate(ReminderTableReadController owner, uint grainHash) : IAsyncDisposable
{
    private readonly TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    internal bool Matches(uint begin, uint end)
        => begin < end
            ? grainHash > begin && grainHash <= end
            : grainHash > begin || grainHash <= end;

    internal void MarkBlocked() => _blocked.TrySetResult();

    internal Task WaitForReleaseAsync() => _release.Task;

    public Task WaitUntilBlockedAsync(CancellationToken cancellationToken)
        => _blocked.Task.WaitAsync(cancellationToken);

    public void Release() => _release.TrySetResult();

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            owner.Remove(this);
            Release();
        }

        return ValueTask.CompletedTask;
    }
}
