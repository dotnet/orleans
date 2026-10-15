using System.Text.Json.Serialization;
using Orleans.DurableTasks.Storage;
using Orleans.Runtime;

namespace Orleans.DurableTasks.Runtime;

/// <summary>
/// An invocation which this grain's outbox holds by reference, which the runtime journals until the outbox retires it.
/// </summary>
/// <param name="Target">The grain on which the invocation starts a task.</param>
/// <param name="Payload">The encoded invocation message.</param>
/// <param name="RecordId">
/// The task which records the start and its outcome: a call's own task, or a detached task's subscription record. A root
/// task which an ordinary grain method posted has none.
/// </param>
/// <remarks>
/// The table of pending invocations is separate from the execution trees, so an invocation survives the deletion of the
/// tree which started it.
/// </remarks>
[GenerateSerializer, Immutable]
[Alias("Orleans.DurableTasks.Runtime.PendingInvocation")]
internal sealed record PendingInvocation(
    [property: Id(0)] GrainId Target,
    [property: Id(1)] byte[] Payload,
    [property: Id(2), JsonConverter(typeof(TaskIdJsonConverter)), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    TaskId? RecordId);
