using System.Text;
using Orleans.Runtime;

namespace Orleans.DurableTasks.Testing;

/// <summary>Describes how far a replayed execution went against its recorded history.</summary>
public enum DurableTaskReplayOutcome
{
    /// <summary>The body completed from recorded outcomes; <see cref="DurableTaskExecutionReplay.Response"/> holds its result.</summary>
    Completed,

    /// <summary>
    /// The body failed from recorded outcomes, for example because the current code throws where the recorded run didn't;
    /// <see cref="DurableTaskExecutionReplay.Response"/> holds the failure.
    /// </summary>
    Faulted,

    /// <summary>
    /// The body reached an operation which the history doesn't record, which is the end of the recorded history, and the
    /// replay stopped before starting it; see <see cref="DurableTaskExecutionReplay.NewOperation"/>.
    /// </summary>
    ReachedNewOperation,

    /// <summary>
    /// The body waits for recorded operations which the history doesn't complete, such as a pending child, a signal which
    /// hasn't arrived, or a delay; see <see cref="DurableTaskExecutionReplay.WaitingOn"/>.
    /// </summary>
    Waiting,

    /// <summary>
    /// The runtime stalled the execution, for example because it recorded a version which the current code doesn't
    /// support, or because the current code diverges from the recorded operations; see
    /// <see cref="DurableTaskExecutionReplay.Stall"/>.
    /// </summary>
    Stalled,
}

/// <summary>The replay of one execution: an invoked task, or a local step which it runs.</summary>
public sealed class DurableTaskExecutionReplay
{
    internal DurableTaskExecutionReplay(
        TaskId id,
        DurableTaskReplayOutcome outcome,
        DurableTaskResponse? response,
        DurableTaskAttemptCanceledException? stall,
        IReadOnlyList<TaskId> reached,
        IReadOnlyList<TaskId> skipped,
        TaskId? newOperation,
        string? newOperationDescription,
        IReadOnlyList<TaskId> waitingOn,
        IReadOnlyDictionary<string, int> versions)
    {
        Id = id;
        Outcome = outcome;
        Response = response;
        Stall = stall;
        Reached = reached;
        Skipped = skipped;
        NewOperation = newOperation;
        NewOperationDescription = newOperationDescription;
        WaitingOn = waitingOn;
        Versions = versions;
    }

    /// <summary>Gets the execution's task identifier.</summary>
    public TaskId Id { get; }

    /// <summary>Gets how far the execution went.</summary>
    public DurableTaskReplayOutcome Outcome { get; }

    /// <summary>
    /// Gets the body's result or failure for <see cref="DurableTaskReplayOutcome.Completed"/> and
    /// <see cref="DurableTaskReplayOutcome.Faulted"/>, which the replay didn't record; otherwise <see langword="null"/>.
    /// </summary>
    public DurableTaskResponse? Response { get; }

    /// <summary>
    /// Gets the runtime's cancellation of the stalled attempt for <see cref="DurableTaskReplayOutcome.Stalled"/>, whose
    /// <see cref="DurableTaskAttemptCanceledException.Reason"/> tells why, and whose inner exception describes it, such
    /// as a <see cref="DurableTaskVersionException"/>; otherwise <see langword="null"/>.
    /// </summary>
    public DurableTaskAttemptCanceledException? Stall { get; }

    /// <summary>Gets the recorded operations which the replay reached, in the order in which it reached them.</summary>
    public IReadOnlyList<TaskId> Reached { get; }

    /// <summary>
    /// Gets the recorded operations which the replay didn't reach, such as operations which the current code removed,
    /// renamed, or moved after the end of its replay.
    /// </summary>
    public IReadOnlyList<TaskId> Skipped { get; }

    /// <summary>
    /// Gets the operation which the history doesn't record, at which the replay stopped, for
    /// <see cref="DurableTaskReplayOutcome.ReachedNewOperation"/>; otherwise <see langword="null"/>.
    /// </summary>
    public TaskId? NewOperation { get; }

    /// <summary>Gets a description of <see cref="NewOperation"/>, such as the grain call which it would make.</summary>
    public string? NewOperationDescription { get; }

    /// <summary>Gets the recorded operations which a <see cref="DurableTaskReplayOutcome.Waiting"/> execution waits for.</summary>
    public IReadOnlyList<TaskId> WaitingOn { get; }

    /// <summary>
    /// Gets the version of each change which the execution checked with <see cref="DurableTask.GetVersion(string, int, int)"/>:
    /// a version which the history recorded, or one which the replay decided.
    /// </summary>
    public IReadOnlyDictionary<string, int> Versions { get; }

    /// <summary>
    /// Gets whether the execution replays its recorded history consistently: it completed, reached the end of its
    /// history, or waits for a recorded operation, and an execution which completed or reached the end of its history
    /// reached every recorded operation.
    /// </summary>
    public bool Succeeded => Outcome switch
    {
        DurableTaskReplayOutcome.Waiting => true,
        DurableTaskReplayOutcome.Completed or DurableTaskReplayOutcome.ReachedNewOperation => Skipped.Count == 0,
        _ => false,
    };

    /// <inheritdoc />
    public override string ToString()
    {
        var builder = new StringBuilder();
        AppendTo(builder);
        return builder.ToString();
    }

    internal void AppendTo(StringBuilder builder)
    {
        builder.Append("Execution '").Append(Id).Append("': ").Append(Outcome);
        switch (Outcome)
        {
            case DurableTaskReplayOutcome.Completed or DurableTaskReplayOutcome.Faulted:
                builder.Append(" with ").Append(Response);
                break;
            case DurableTaskReplayOutcome.Stalled when Stall is not null:
                builder.Append(" (").Append(Stall.Reason).Append("): ").Append(Stall.InnerException?.Message ?? Stall.Message);
                break;
        }

        builder.AppendLine();
        AppendIds(builder, "reached", Reached);
        AppendIds(builder, "skipped", Skipped);
        if (NewOperation is { } newOperation)
        {
            builder.Append("  new operation: ").Append(newOperation).Append(" (").Append(NewOperationDescription).AppendLine(")");
        }

        AppendIds(builder, "waiting on", WaitingOn);
        if (Versions.Count > 0)
        {
            builder.Append("  versions: ")
                .AppendJoin(", ", Versions.OrderBy(static pair => pair.Key, StringComparer.Ordinal).Select(static pair => $"{pair.Key} = {pair.Value}"))
                .AppendLine();
        }

        static void AppendIds(StringBuilder builder, string label, IReadOnlyList<TaskId> ids)
        {
            if (ids.Count > 0)
            {
                builder.Append("  ").Append(label).Append(": ").AppendJoin(", ", ids).AppendLine();
            }
        }
    }
}

/// <summary>The replay of a grain's recorded durable task history against the current code.</summary>
public sealed class DurableTaskReplayResult
{
    internal DurableTaskReplayResult(GrainId grainId, IReadOnlyList<DurableTaskExecutionReplay> executions)
    {
        GrainId = grainId;
        Executions = executions;
    }

    /// <summary>Gets the replayed grain.</summary>
    public GrainId GrainId { get; }

    /// <summary>
    /// Gets the replayed executions in the order in which they started: the grain's pending invoked tasks, and the local
    /// steps which they ran.
    /// </summary>
    public IReadOnlyList<DurableTaskExecutionReplay> Executions { get; }

    /// <summary>Gets the replay of the execution <paramref name="id"/>.</summary>
    /// <exception cref="KeyNotFoundException">The replay didn't run the execution.</exception>
    public DurableTaskExecutionReplay this[TaskId id] =>
        Executions.FirstOrDefault(execution => execution.Id == id)
        ?? throw new KeyNotFoundException($"The replay of grain '{GrainId}' didn't run execution '{id}'.{Environment.NewLine}{this}");

    /// <summary>Gets whether every execution replayed its recorded history consistently; see <see cref="DurableTaskExecutionReplay.Succeeded"/>.</summary>
    public bool Succeeded => Executions.All(static execution => execution.Succeeded);

    /// <summary>Throws unless every execution replayed its recorded history consistently.</summary>
    /// <exception cref="DurableTaskReplayException">An execution stalled, faulted, or skipped recorded operations.</exception>
    public void EnsureSucceeded()
    {
        if (!Succeeded)
        {
            throw new DurableTaskReplayException(this);
        }
    }

    /// <inheritdoc />
    public override string ToString()
    {
        var builder = new StringBuilder()
            .Append("Replay of grain '").Append(GrainId).Append("': ")
            .AppendLine(Succeeded ? "succeeded" : "failed");
        if (Executions.Count == 0)
        {
            builder.AppendLine("No pending executions.");
        }

        foreach (var execution in Executions)
        {
            execution.AppendTo(builder);
        }

        return builder.ToString();
    }
}

/// <summary>The exception which <see cref="DurableTaskReplayResult.EnsureSucceeded"/> throws for a failed replay.</summary>
public sealed class DurableTaskReplayException : Exception
{
    /// <summary>Initializes an exception which reports <paramref name="result"/>.</summary>
    /// <param name="result">The failed replay.</param>
    public DurableTaskReplayException(DurableTaskReplayResult result)
        : base((result ?? throw new ArgumentNullException(nameof(result))).ToString())
    {
        Result = result;
    }

    /// <summary>Gets the failed replay.</summary>
    public DurableTaskReplayResult Result { get; }
}
