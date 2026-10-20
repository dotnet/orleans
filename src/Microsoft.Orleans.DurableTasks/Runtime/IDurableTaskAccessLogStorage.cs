namespace Orleans.DurableTasks.Runtime;

/// <summary>Stores the access logs of durable task executions, which record their accesses to journaled state.</summary>
/// <remarks>
/// <para>
/// Every durable task storage which the Orleans grain host binds to stores access logs: the runtime fails to bind to an
/// activation whose storage doesn't.
/// </para>
/// <para>
/// Appended records commit in the same atomic write as the changes to the activation's journaled state which were staged
/// before them, so a recovered activation holds a change together with the records which precede it, or neither. A
/// storage removes an execution's access log when it stores the execution's result or removes the execution's task: the
/// execution's body never runs again.
/// </para>
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
