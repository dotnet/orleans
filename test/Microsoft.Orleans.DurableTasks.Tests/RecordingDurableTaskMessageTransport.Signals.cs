#nullable enable
using System;
using System.Collections.Generic;
using Orleans.DurableTasks;
using Orleans.Runtime;

namespace Microsoft.Orleans.DurableTasks.Tests;

internal sealed partial class RecordingDurableTaskMessageTransport
{
    public List<(GrainId Sender, GrainId Target, TaskId TaskId, string Name, DurableTaskResponse Value, string PayloadFingerprint, Guid Incarnation)> Signals { get; } = [];

    public void SendSignal(
        GrainId sender,
        GrainId target,
        TaskId taskId,
        string name,
        DurableTaskResponse value,
        string payloadFingerprint,
        Guid incarnation)
    {
        Signals.Add((sender, target, taskId, name, value, payloadFingerprint, incarnation));
        StateChanges.Notify();
    }
}
