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
}
