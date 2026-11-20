namespace Orleans.DurableTasks;

/// <summary>
/// The rejection codes with which a grain dead-letters a durable task invocation which it rejects permanently, and which
/// the starter's record reports.
/// </summary>
/// <remarks>
/// A start rejected with <see cref="Conflict"/> fails with <see cref="DurableTaskConflictException"/>. A start rejected with
/// any other code, or without one, fails with <see cref="DurableTaskRejectedException"/>, whose
/// <see cref="DurableTaskRejectedException.Code"/> reports the code. A grain which fails to accept an invocation for
/// another reason, such as one which doesn't decode, leaves it to the sender's retries.
/// </remarks>
public static class DurableTaskRejectionCodes
{
    /// <summary>The task identifier already identifies another request, or a task of another caller.</summary>
    public const string Conflict = "durable-rpc/conflict";

    /// <summary>
    /// The invocation arrived after the grain's admission window: Messaging's <c>DeduplicationWindow</c> since its sender
    /// first staged it.
    /// </summary>
    /// <remarks>
    /// The grain admits a late redelivery of a task which it still stores, so this code means it doesn't store the task
    /// now. An earlier delivery may have run it, and its retention may have ended since, so the outcome is unknown.
    /// </remarks>
    public const string Expired = "durable-rpc/expired";
}
