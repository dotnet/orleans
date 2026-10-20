using System.Text.Json.Serialization;
using Orleans.Runtime;

namespace Orleans.DurableTasks.Runtime;

/// <summary>
/// An invocation which this grain's outbox holds by reference, which the runtime journals until the outbox retires it.
/// </summary>
/// <param name="Target">The grain on which the invocation starts a task.</param>
/// <param name="Payload">The encoded invocation message.</param>
/// <param name="RecordId">
/// The wire identifier of the task which records the start and its outcome: a call's own task, or a detached task's
/// subscription record. A root task which an ordinary grain method posted has none. A retirement which names an earlier
/// run of the record's tree, which was deleted, changes nothing.
/// </param>
/// <param name="SentAt">
/// When the runtime first staged the invocation, on the Durable Jobs clock. The receiver admits the invocation until
/// Messaging's <c>DeduplicationWindow</c> has elapsed since then.
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
    [property: Id(2), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    TaskId? RecordId,
    [property: Id(3)] DateTimeOffset SentAt);
