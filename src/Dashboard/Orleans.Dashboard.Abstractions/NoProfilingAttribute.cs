using System;

namespace Orleans.Dashboard;

/// <summary>
/// Suppresses profiling for grain classes or methods, preventing them from being tracked by the Orleans Dashboard profiler.
/// Apply this attribute to exclude specific grains or methods from performance metrics collection.
/// </summary>
/// <example>
/// <code>
/// [NoProfiling]
/// public class MyInternalGrain : Grain
/// {
///     // This entire grain will not be profiled
/// }
///
/// public class MyGrain : Grain
/// {
///     [NoProfiling]
///     public Task InternalMethod()
///     {
///         // This specific method will not be profiled
///     }
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
public sealed class NoProfilingAttribute : Attribute
{
}
