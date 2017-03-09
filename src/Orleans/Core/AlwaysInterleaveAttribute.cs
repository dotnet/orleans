using System;

namespace Orleans.Concurrency
{
    /// <summary>
    /// The AlwaysInterleaveAttribute attribute is used to mark methods that can interleave with any other method type, including write (non ReadOnly) requests.
    /// </summary>
    /// <remarks>
    /// Note that this attribute is applied to method declaration in the grain interface, 
    /// and not to the method in the implementation class itself.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class AlwaysInterleaveAttribute : Attribute
    {
    }
}