using System;

namespace Orleans.Concurrency
{
    /// <summary>
    /// The Immutable attribute indicates that instances of the marked class or struct are never modified
    /// after they are created.
    /// </summary>
    /// <remarks>
    /// Note that this implies that sub-objects are also not modified after the instance is created.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class)]
    public sealed class ImmutableAttribute : Attribute
    {
    }
}