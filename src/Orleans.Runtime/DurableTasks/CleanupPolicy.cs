using System;

namespace Orleans.Runtime.DurableTasks;

public class CleanupPolicy
{
    public TimeSpan CleanupAge { get; set; }
}
