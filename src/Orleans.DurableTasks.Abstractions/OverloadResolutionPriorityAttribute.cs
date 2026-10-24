#if !NET9_0_OR_GREATER
namespace System.Runtime.CompilerServices;

/// <summary>Specifies the priority of a member in overload resolution, which compilers apply before betterness.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor | AttributeTargets.Property, Inherited = false)]
internal sealed class OverloadResolutionPriorityAttribute(int priority) : Attribute
{
    /// <summary>Gets the priority: members with higher priority are preferred over applicable members with lower priority.</summary>
    public int Priority { get; } = priority;
}
#endif
