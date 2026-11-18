namespace Orleans.DurableTasks;

/// <summary>Describes how an invoked durable task handled a signal.</summary>
[GenerateSerializer]
[Alias("Orleans.DurableTasks.DurableSignalResult")]
public enum DurableSignalResult
{
    /// <summary>The task recorded the signal's value.</summary>
    Accepted,

    /// <summary>The task already holds an equal value for the signal, so it is unchanged.</summary>
    Duplicate,

    /// <summary>The task holds a different value for the signal, so it rejected the value.</summary>
    Conflict,

    /// <summary>The grain retains no invoked task with the identifier.</summary>
    TaskNotFound,

    /// <summary>The task is terminal and holds no value for the signal, so it rejected the value.</summary>
    TaskCompleted,
}
