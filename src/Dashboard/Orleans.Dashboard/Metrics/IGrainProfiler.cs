using System;
using System.Runtime.CompilerServices;

namespace Orleans.Dashboard.Metrics;

/// <summary>
/// Provides profiling capabilities for grain method invocations, tracking execution time and failures.
/// </summary>
internal interface IGrainProfiler
{
    /// <summary>
    /// Records a grain method invocation for profiling purposes.
    /// </summary>
    /// <param name="elapsedMs">The elapsed time in milliseconds for the method invocation.</param>
    /// <param name="grainType">The type of the grain.</param>
    /// <param name="methodName">The name of the method that was invoked. Automatically captured from the caller if not specified.</param>
    /// <param name="failed">True if the method invocation resulted in an exception; otherwise, false.</param>
    void Track(double elapsedMs, Type grainType, [CallerMemberName] string methodName = null, bool failed = false);

    /// <summary>
    /// Enables or disables the grain profiler.
    /// </summary>
    /// <param name="enabled">True to enable profiling; false to disable.</param>
    void Enable(bool enabled);

    /// <summary>
    /// Gets a value indicating whether the grain profiler is currently enabled.
    /// </summary>
    bool IsEnabled { get; }
}
