namespace Orleans.Journaling;

/// <summary>
/// Identifies a journal persistence operation.
/// </summary>
public enum JournaledStateOperation
{
    /// <summary>Appends pending journal entries.</summary>
    Write,

    /// <summary>Replaces storage with a snapshot.</summary>
    Snapshot,

    /// <summary>Deletes storage and resets registered states.</summary>
    Delete
}

/// <summary>
/// Participates in the prerequisites and completion of actual journal operations.
/// </summary>
/// <remarks>
/// Ordinary before hooks run in list order on the owner's logical execution context, outside its lock.
/// An optional <see cref="IJournaledStateCaptureHook"/> runs last immediately before capture or deletion.
/// Prerequisites must cover changes staged during asynchronous preparation. After hooks run after
/// storage acknowledgement and state acknowledgement or reset, including successful writes which
/// produce no storage bytes. Hooks retain operation-local data across these boundaries and keep later
/// pending changes separate. Recursive operations on the same journal owner are rejected.
/// </remarks>
public interface IJournaledStateHook
{
    /// <summary>
    /// Establishes prerequisites before capture or storage deletion.
    /// </summary>
    /// <param name="operation">The operation about to execute.</param>
    /// <param name="cancellationToken">The token for the owned operation's lifetime.</param>
    /// <returns>A completion representing the prerequisite work.</returns>
    /// <remarks>
    /// Failure reports <see cref="JournaledStatePreCommitException"/> and retains pending changes
    /// for an explicit retry. All staged changes remain safe to commit. Full deletion requires the
    /// owner to stop admission and drain feature operations before queuing deletion.
    /// </remarks>
    ValueTask BeforeOperationAsync(JournaledStateOperation operation, CancellationToken cancellationToken) => default;

    /// <summary>
    /// Completes post-persistence work after the operation succeeds.
    /// </summary>
    /// <param name="operation">The completed operation.</param>
    /// <param name="cancellationToken">The token for the owned operation's lifetime.</param>
    /// <returns>A completion representing post-persistence work.</returns>
    /// <remarks>
    /// Every after hook is invoked even when an earlier after hook fails. Failures are surfaced as
    /// <see cref="JournaledStatePostCommitException"/> and leave the successfully persisted owner usable.
    /// Durable feature state supplies recovery for interrupted post-persistence work.
    /// </remarks>
    ValueTask AfterOperationAsync(JournaledStateOperation operation, CancellationToken cancellationToken) => default;
}
