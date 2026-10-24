using Orleans.DurableTasks;
using Orleans.DurableTasks.Runtime;
using Orleans.DurableTasks.Storage;

namespace Benchmarks.Journaling;

/// <summary>Stores access logs in the journaled state machine which durable task grain storage uses.</summary>
internal sealed class AccessLogStorage(DurableTaskAccessLogs logs) : IDurableTaskAccessLogStorage
{
    public ReadOnlyMemory<byte> GetAccessLog(TaskId taskId) => logs.Get(taskId);

    public void AppendAccessLog(TaskId taskId, ReadOnlySpan<byte> records) => logs.Append(taskId, records);

    public int GetAccessLogLength(TaskId taskId) => logs.Get(taskId).Length;

    // The benchmarks stage an attempt's reads explicitly, with its log's Flush.
    public void AddPendingRecords(IDurableTaskPendingAccessRecords source)
    {
    }
}
