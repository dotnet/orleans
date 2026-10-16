namespace Orleans.DurableTasks;

/// <summary>
/// Represents a durable task whose recorded version of a change isn't one which the running code supports, because the
/// code raised the change's minimum supported version, or because older code replays an execution which newer code
/// recorded.
/// </summary>
/// <remarks>
/// <see cref="DurableTask.GetVersion(string, int, int)"/> stalls such an execution: the host cancels the attempt for
/// <see cref="DurableTaskAttemptCancellationReason.UnsupportedVersion"/>, and the attempt's
/// <see cref="DurableTaskAttemptCanceledException"/> carries this exception as its inner exception. The host discards the
/// attempt's result and keeps the task pending. A later activation, typically on a deployment whose code supports the
/// recorded version, replays the task. Abort the task to end it instead.
/// </remarks>
public sealed class DurableTaskVersionException : Exception
{
    /// <summary>Initializes an exception for the recorded version <paramref name="version"/> of <paramref name="name"/>.</summary>
    /// <param name="taskId">The execution which recorded the version.</param>
    /// <param name="name">The change's name.</param>
    /// <param name="version">The execution's recorded version.</param>
    /// <param name="minSupported">The minimum version which the running code supports.</param>
    /// <param name="maxSupported">The maximum version which the running code supports.</param>
    public DurableTaskVersionException(TaskId taskId, string name, int version, int minSupported, int maxSupported)
        : base($"Durable task '{taskId}' recorded version {version} of '{name}', and the running code supports versions {minSupported} to {maxSupported}. The task stays pending until code which supports version {version} replays it.")
    {
        TaskId = taskId;
        Name = name;
        Version = version;
        MinSupported = minSupported;
        MaxSupported = maxSupported;
    }

    /// <summary>Gets the execution which recorded the version.</summary>
    public TaskId TaskId { get; }

    /// <summary>Gets the change's name.</summary>
    public string Name { get; }

    /// <summary>Gets the execution's recorded version.</summary>
    public int Version { get; }

    /// <summary>Gets the minimum version which the running code supports.</summary>
    public int MinSupported { get; }

    /// <summary>Gets the maximum version which the running code supports.</summary>
    public int MaxSupported { get; }
}
