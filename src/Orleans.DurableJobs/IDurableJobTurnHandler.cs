namespace Orleans.DurableJobs;

/// <summary>
/// Handles a durable job in bounded ordinary requests on its owning grain.
/// </summary>
/// <remarks>
/// The dispatcher waits for the entire step, including its asynchronous operations. A non-reentrant owner
/// can write its state during the step. Each step must finish within the normal Orleans response deadline.
/// Return <see cref="DurableJobRunResult.ContinueOnOwnerTurn"/> for more work on the same job attempt.
/// Delivery is at least once. The owner is responsible for idempotency and recovery across retries.
/// </remarks>
public interface IDurableJobTurnHandler
{
    /// <summary>Executes one bounded step and returns its disposition.</summary>
    ValueTask<DurableJobRunResult> ExecuteJobTurnAsync(IJobRunContext context, CancellationToken attemptCancellationToken);
}

/// <summary>
/// Adds an interleaving post-commit effect after the last ordinary step of a job.
/// </summary>
/// <remarks>
/// Completion may read committed state and notify external owners. State changes belong in
/// <see cref="IDurableJobTurnHandler.ExecuteJobTurnAsync"/>. A failed completion retries the job,
/// so the ordinary step and the completion effect must be safe to repeat.
/// </remarks>
public interface IDurableJobTurnCompletionHandler : IDurableJobTurnHandler
{
    /// <summary>Runs the post-commit effect after the last owner turn has returned.</summary>
    Task CompleteJobTurnAsync(IJobRunContext context, CancellationToken attemptCancellationToken);
}
