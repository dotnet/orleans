namespace Orleans.DurableTasks.Runtime;

/// <summary>Stores the access logs of durable task executions, which record their accesses to journaled state.</summary>
/// <remarks>
/// A storage removes an execution's access log when it stores the execution's result or removes the execution's task:
/// the execution's body never runs again.
/// </remarks>
internal interface IDurableTaskAccessLogStorage
{
    /// <summary>
    /// Gets the records of the accesses to journaled state which the attempts of execution <paramref name="taskId"/>
    /// staged, in order, which later appends don't change.
    /// </summary>
    byte[] GetAccessLog(TaskId taskId);

    /// <summary>Appends <paramref name="records"/> to the access log of execution <paramref name="taskId"/>.</summary>
    /// <remarks>The records are staged, and commit with the next write together with the changes staged before them.</remarks>
    void AppendAccessLog(TaskId taskId, ReadOnlySpan<byte> records);
}
