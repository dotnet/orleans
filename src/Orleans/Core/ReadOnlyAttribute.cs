using System;

namespace Orleans.Concurrency
{
    /// <summary>
    /// The ReadOnly attribute is used to mark methods that do not modify the state of a grain.
    /// <para>
    /// Marking methods as ReadOnly allows the run-time system to perform a number of optimizations
    /// that may significantly improve the performance of your application.
    /// </para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    internal sealed class ReadOnlyAttribute : Attribute
    {
    }
}