using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.Reminders.TestKit;

/// <summary>
/// Identifies an <see cref="IReminderTable"/> operation observed by <see cref="IdealizedReminderTable"/>.
/// </summary>
public enum ReminderTableOperationKind
{
    /// <summary><see cref="IReminderTable.StartAsync"/>.</summary>
    Start,

    /// <summary><see cref="IReminderTable.StopAsync"/>.</summary>
    Stop,

    /// <summary><see cref="IReminderTable.ReadRow"/>.</summary>
    ReadRow,

    /// <summary><see cref="IReminderTable.ReadRows(GrainId)"/>.</summary>
    ReadGrainRows,

    /// <summary><see cref="IReminderTable.ReadRows(uint, uint)"/>.</summary>
    ReadRange,

    /// <summary><see cref="IReminderTable.UpsertRow"/>.</summary>
    UpsertRow,

    /// <summary><see cref="IReminderTable.RemoveRow"/>.</summary>
    RemoveRow,

    /// <summary><see cref="IReminderTable.TestOnlyClearTable"/>.</summary>
    ClearTable
}

/// <summary>
/// Thrown by <see cref="IdealizedReminderTable"/> when the table has been marked unavailable to simulate a storage outage.
/// </summary>
[Serializable]
[GenerateSerializer]
public sealed class ReminderTableUnavailableException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ReminderTableUnavailableException"/> class.
    /// </summary>
    public ReminderTableUnavailableException() : base("The reminder table is unavailable.")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ReminderTableUnavailableException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    public ReminderTableUnavailableException(string message) : base(message)
    {
    }

    [Obsolete]
    private ReminderTableUnavailableException(SerializationInfo info, StreamingContext context) : base(info, context)
    {
    }
}

/// <summary>
/// A durable reminder record observed through <see cref="IdealizedReminderTable.Snapshot"/>.
/// </summary>
/// <param name="GrainId">The grain identifier.</param>
/// <param name="ReminderName">The reminder name.</param>
/// <param name="StartAt">The persisted start time.</param>
/// <param name="Period">The persisted period.</param>
/// <param name="ETag">The current ETag.</param>
/// <param name="PreviousETag">The ETag replaced by the most recent write, if any.</param>
/// <param name="Version">The monotonically increasing version of this record.</param>
public sealed record ReminderTableRecord(
    GrainId GrainId,
    string ReminderName,
    DateTime StartAt,
    TimeSpan Period,
    string ETag,
    string? PreviousETag,
    long Version)
{
    /// <summary>
    /// Gets the uniform hash code which determines range ownership.
    /// </summary>
    public uint UniformHashCode => GrainId.GetUniformHashCode();

    /// <inheritdoc />
    public override string ToString()
        => $"(GrainId={GrainId}, ReminderName='{ReminderName}', StartAt={StartAt:O}, Period={Period}, ETag='{ETag}', PreviousETag={(PreviousETag is null ? "<null>" : $"'{PreviousETag}'")}, Version={Version}, Hash={UniformHashCode})";
}

/// <summary>
/// One operation recorded by <see cref="IdealizedReminderTable"/>.
/// </summary>
public sealed class ReminderTableOperation
{
    internal ReminderTableOperation(
        long sequence,
        ReminderTableOperationKind kind,
        GrainId? grainId,
        string? reminderName,
        uint? begin,
        uint? end,
        string? suppliedETag,
        string? resultETag,
        bool succeeded,
        int resultCount,
        string? failure)
    {
        Sequence = sequence;
        Kind = kind;
        GrainId = grainId;
        ReminderName = reminderName;
        Begin = begin;
        End = end;
        SuppliedETag = suppliedETag;
        ResultETag = resultETag;
        Succeeded = succeeded;
        ResultCount = resultCount;
        Failure = failure;
    }

    /// <summary>Gets the monotonically increasing operation sequence number.</summary>
    public long Sequence { get; }

    /// <summary>Gets the operation kind.</summary>
    public ReminderTableOperationKind Kind { get; }

    /// <summary>Gets the grain identifier, when the operation targets one.</summary>
    public GrainId? GrainId { get; }

    /// <summary>Gets the reminder name, when the operation targets one.</summary>
    public string? ReminderName { get; }

    /// <summary>Gets the exclusive lower bound, for range reads.</summary>
    public uint? Begin { get; }

    /// <summary>Gets the inclusive upper bound, for range reads.</summary>
    public uint? End { get; }

    /// <summary>Gets the ETag supplied by the caller, for conditional operations.</summary>
    public string? SuppliedETag { get; }

    /// <summary>Gets the ETag returned to the caller, for writes.</summary>
    public string? ResultETag { get; }

    /// <summary>Gets a value indicating whether the operation completed without throwing and, for
    /// <see cref="ReminderTableOperationKind.RemoveRow"/>, removed a row.</summary>
    public bool Succeeded { get; }

    /// <summary>Gets the number of reminders returned, for reads.</summary>
    public int ResultCount { get; }

    /// <summary>Gets the failure type name when the operation threw.</summary>
    public string? Failure { get; }

    /// <inheritdoc />
    public override string ToString()
    {
        var target = GrainId is { } grainId ? $" grain={grainId}" : string.Empty;
        var name = ReminderName is null ? string.Empty : $" reminder='{ReminderName}'";
        var range = Begin is { } begin && End is { } end ? $" range=({begin}, {end}]" : string.Empty;
        var supplied = SuppliedETag is null ? string.Empty : $" supplied='{SuppliedETag}'";
        var result = ResultETag is null ? string.Empty : $" etag='{ResultETag}'";
        var failure = Failure is null ? string.Empty : $" failure={Failure}";
        return $"#{Sequence} {Kind}{target}{name}{range}{supplied}{result} succeeded={Succeeded.ToString(CultureInfo.InvariantCulture)} count={ResultCount.ToString(CultureInfo.InvariantCulture)}{failure}";
    }
}

/// <summary>
/// An awaitable gate which blocks the next matching operation until it is released.
/// </summary>
public sealed class ReminderTableOperationGate : IAsyncDisposable
{
    private readonly TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IdealizedReminderTable _owner;
    private int _disposed;

    internal ReminderTableOperationGate(IdealizedReminderTable owner, ReminderTableOperationKind kind)
    {
        _owner = owner;
        Kind = kind;
    }

    /// <summary>
    /// Gets the operation kind which this gate blocks.
    /// </summary>
    public ReminderTableOperationKind Kind { get; }

    /// <summary>
    /// Waits until a matching operation has reached the gate.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task which completes when the operation is blocked.</returns>
    public Task WaitUntilBlockedAsync(CancellationToken cancellationToken = default) => _blocked.Task.WaitAsync(cancellationToken);

    /// <summary>
    /// Releases the blocked operation.
    /// </summary>
    public void Release() => _release.TrySetResult();

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _owner.RemoveGate(this);
            Release();
        }

        return ValueTask.CompletedTask;
    }

    internal void MarkBlocked() => _blocked.TrySetResult();

    internal Task WaitForReleaseAsync(CancellationToken cancellationToken) => _release.Task.WaitAsync(cancellationToken);
}
