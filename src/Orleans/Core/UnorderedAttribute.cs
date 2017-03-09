using System;

namespace Orleans.Concurrency
{
    /// <summary>
    /// The Unordered attribute is used to mark grain interface in which the delivery order of
    /// messages is not significant.
    /// </summary>
    [AttributeUsage(AttributeTargets.Interface)]
    public sealed class UnorderedAttribute : Attribute
    {
    }
}