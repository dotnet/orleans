#nullable enable
using System.Collections.Generic;
using Orleans.DurableTasks;
using Orleans.Runtime;

namespace Microsoft.Orleans.DurableTasks.Tests;

internal sealed partial class RecordingDurableTaskMessageTransport
{
    public List<(GrainId Sender, GrainId Target, TaskId TaskId, string Name, DurableTaskResponse Value, string PayloadFingerprint)> Signals { get; } = [];

    public void SendSignal(GrainId sender, GrainId target, TaskId taskId, string name, DurableTaskResponse value, string payloadFingerprint) =>
        Signals.Add((sender, target, taskId, name, value, payloadFingerprint));
}
