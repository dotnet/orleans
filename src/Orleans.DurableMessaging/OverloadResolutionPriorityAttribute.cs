#if !NET9_0_OR_GREATER
using System;

namespace System.Runtime.CompilerServices;

/// <summary>
/// Provides overload-resolution priority metadata when targeting frameworks earlier than .NET 9.
/// </summary>
[AttributeUsage(AttributeTargets.Constructor | AttributeTargets.Method | AttributeTargets.Property, Inherited = false)]
internal sealed class OverloadResolutionPriorityAttribute(int priority) : Attribute
{
    /// <summary>
    /// Gets the relative priority of the annotated member during overload resolution.
    /// </summary>
    public int Priority { get; } = priority;
}
#endif
