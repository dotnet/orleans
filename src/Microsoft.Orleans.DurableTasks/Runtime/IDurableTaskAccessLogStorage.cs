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
    /// staged, in order. Later appends don't change the returned records.
    /// </summary>
    ReadOnlyMemory<byte> GetAccessLog(TaskId taskId);

    /// <summary>Appends <paramref name="records"/> to the access log of execution <paramref name="taskId"/>.</summary>
    /// <remarks>The records are staged, and commit with the next write together with the changes staged before them.</remarks>
    void AppendAccessLog(TaskId taskId, ReadOnlySpan<byte> records);

    /// <summary>
    /// Stages the records which <paramref name="source"/> holds before the next write captures the activation's changes.
    /// </summary>
    /// <remarks>
    /// A source adds itself when it begins to hold records which it hasn't staged, such as reads which no later write of
    /// its attempt depends on yet. Every durable consequence of such a read commits with a write, so staging them as the
    /// write captures commits each read with everything which can depend on it.
    /// </remarks>
    void AddPendingRecords(IDurableTaskPendingAccessRecords source);
}

/// <summary>Records of accesses to journaled state which an attempt holds before staging them.</summary>
internal interface IDurableTaskPendingAccessRecords
{
    /// <summary>Stages the records which the attempt holds.</summary>
    void Flush();
}
