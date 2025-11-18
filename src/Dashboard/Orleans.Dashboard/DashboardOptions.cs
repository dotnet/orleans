namespace Orleans.Dashboard;

/// <summary>
/// Configuration options for the Orleans Dashboard.
/// </summary>
public sealed class DashboardOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether to disable the trace feature.
    /// When true, the live log streaming endpoint will be disabled.
    /// The default is false.
    /// </summary>
    public bool HideTrace { get; set; }

    /// <summary>
    /// Gets or sets the number of milliseconds between counter samples.
    /// Must be greater than or equal to 1000.
    /// The default is 1000 (1 second).
    /// </summary>
    public int CounterUpdateIntervalMs { get; set; } = 1000;

    /// <summary>
    /// Gets or sets the length of the history to maintain for metrics.
    /// Higher values provide more historical data but consume more memory.
    /// The default is 100.
    /// </summary>
    public int HistoryLength { get; set; } = 100;
}
