namespace Orleans.Dashboard;

/// <summary>
/// Configuration options for the grain profiler.
/// </summary>
public sealed class GrainProfilerOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether tracing should always be enabled, regardless of dashboard activity.
    /// When set to <see langword="true"/>, profiling data is continuously collected even when the dashboard is not being queried.
    /// When set to <see langword="false"/> (default), profiling is automatically disabled after <see cref="DeactivationTime"/> of inactivity.
    /// Default is <see langword="false"/>.
    /// </summary>
    public bool TraceAlways { get; set; }

    /// <summary>
    /// Gets or sets the duration of inactivity (no dashboard queries) after which profiling is automatically disabled.
    /// This setting only applies when <see cref="TraceAlways"/> is <see langword="false"/>.
    /// After this period without queries, profiling stops to reduce overhead until the next dashboard query.
    /// Default is 1 minute.
    /// </summary>
    public TimeSpan DeactivationTime { get; set; } = TimeSpan.FromMinutes(1);
}
