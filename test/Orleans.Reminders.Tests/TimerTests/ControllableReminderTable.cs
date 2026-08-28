#nullable enable

using Orleans.Runtime.ReminderService;

namespace UnitTests.TimerTests;

internal sealed class ControllableReminderTable(
    InMemoryReminderTable inner,
    ReminderTableReadController readController) : IReminderTable
{
    private int pointReadCount;

    public int PointReadCount => Volatile.Read(ref pointReadCount);

    public Task StartAsync(CancellationToken cancellationToken = default) => ((IReminderTable)inner).StartAsync(cancellationToken);

    public Task<ReminderTableData> ReadRows(GrainId grainId) => inner.ReadRows(grainId);

    public Task<ReminderTableData> ReadRows(uint begin, uint end)
        => ReadRows(begin, end, requireStrongConsistency: false);

    public async Task<ReminderTableData> ReadRows(uint begin, uint end, bool requireStrongConsistency)
    {
        var result = await inner.ReadRows(begin, end);
        result = readController.TransformRangeRead(begin, end, result, requireStrongConsistency);
        await readController.OnRangeReadAsync(begin, end);
        return result;
    }

    public async Task<ReminderEntry?> ReadRow(GrainId grainId, string reminderName)
    {
        Interlocked.Increment(ref pointReadCount);
        var result = await inner.ReadRow(grainId, reminderName);
        await readController.OnPointReadAsync(grainId, reminderName);
        return result;
    }

    public Task<string?> UpsertRow(ReminderEntry entry) => inner.UpsertRow(entry);

    public Task<bool> RemoveRow(GrainId grainId, string reminderName, string eTag) => inner.RemoveRow(grainId, reminderName, eTag);

    public Task TestOnlyClearTable() => inner.TestOnlyClearTable();

    public Task StopAsync(CancellationToken cancellationToken = default) => ((IReminderTable)inner).StopAsync(cancellationToken);
}

internal sealed class ReminderTableReadController
{
    private readonly object _lock = new();
    private readonly List<ReminderTableReadGate> _gates = [];
    private readonly List<ReminderPointReadGate> _pointReadGates = [];
    private readonly List<(GrainId GrainId, string ReminderName)> _omissions = [];
    private int strongRangeReadCount;
    private int eventualRangeReadCount;

    public int StrongRangeReadCount => Volatile.Read(ref strongRangeReadCount);

    public int EventualRangeReadCount => Volatile.Read(ref eventualRangeReadCount);

    public void OmitFromNextRangeRead(GrainId grainId, string reminderName)
    {
        lock (_lock)
        {
            _omissions.Add((grainId, reminderName));
        }
    }

    public ReminderTableReadGate BlockNextRangeRead(GrainId grainId, CancellationToken cancellationToken)
    {
        var gate = new ReminderTableReadGate(this, grainId.GetUniformHashCode(), cancellationToken);
        lock (_lock)
        {
            _gates.Add(gate);
        }

        return gate;
    }

    public ReminderPointReadGate BlockNextPointRead(
        GrainId grainId,
        string reminderName,
        CancellationToken cancellationToken)
    {
        var gate = new ReminderPointReadGate(this, grainId, reminderName, cancellationToken);
        lock (_lock)
        {
            _pointReadGates.Add(gate);
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

    internal ReminderTableData TransformRangeRead(
        uint begin,
        uint end,
        ReminderTableData result,
        bool requireStrongConsistency)
    {
        if (requireStrongConsistency)
        {
            Interlocked.Increment(ref strongRangeReadCount);
        }
        else
        {
            Interlocked.Increment(ref eventualRangeReadCount);
        }

        lock (_lock)
        {
            for (var i = 0; i < _omissions.Count; i++)
            {
                var omission = _omissions[i];
                if (!Matches(omission.GrainId.GetUniformHashCode(), begin, end))
                {
                    continue;
                }

                _omissions.RemoveAt(i);
                return new(result.Reminders.Where(entry =>
                    entry.GrainId != omission.GrainId
                    || !string.Equals(entry.ReminderName, omission.ReminderName, StringComparison.Ordinal)));
            }
        }

        return result;
    }

    internal async Task OnPointReadAsync(GrainId grainId, string reminderName)
    {
        ReminderPointReadGate? gate = null;
        lock (_lock)
        {
            for (var i = 0; i < _pointReadGates.Count; i++)
            {
                if (_pointReadGates[i].Matches(grainId, reminderName))
                {
                    gate = _pointReadGates[i];
                    _pointReadGates.RemoveAt(i);
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

    private static bool Matches(uint grainHash, uint begin, uint end)
        => begin < end
            ? grainHash > begin && grainHash <= end
            : grainHash > begin || grainHash <= end;

    internal void Remove(ReminderTableReadGate gate)
    {
        lock (_lock)
        {
            _gates.Remove(gate);
        }

    }

    internal void Remove(ReminderPointReadGate gate)
    {
        lock (_lock)
        {
            _pointReadGates.Remove(gate);
        }
    }
}

internal sealed class ReminderTableReadGate(
    ReminderTableReadController owner,
    uint grainHash,
    CancellationToken cancellationToken) : IAsyncDisposable
{
    private readonly TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    internal bool Matches(uint begin, uint end)
        => begin < end
            ? grainHash > begin && grainHash <= end
            : grainHash > begin || grainHash <= end;

    internal void MarkBlocked() => _blocked.TrySetResult();

    internal Task WaitForReleaseAsync() => _release.Task.WaitAsync(cancellationToken);

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

internal sealed class ReminderPointReadGate(
    ReminderTableReadController owner,
    GrainId grainId,
    string reminderName,
    CancellationToken cancellationToken) : IAsyncDisposable
{
    private readonly TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    internal bool Matches(GrainId candidateGrainId, string candidateReminderName)
        => grainId == candidateGrainId
            && string.Equals(reminderName, candidateReminderName, StringComparison.Ordinal);

    internal void MarkBlocked() => _blocked.TrySetResult();

    internal Task WaitForReleaseAsync() => _release.Task.WaitAsync(cancellationToken);

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
