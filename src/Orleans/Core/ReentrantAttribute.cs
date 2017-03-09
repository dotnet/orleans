using System;

namespace Orleans.Concurrency
{
    /// <summary>
    /// The Reentrant attribute is used to mark grain implementation classes that allow request interleaving within a task.
    /// <para>
    /// This is an advanced feature and should not be used unless the implications are fully understood.
    /// That said, allowing request interleaving allows the run-time system to perform a number of optimizations
    /// that may significantly improve the performance of your application. 
    /// </para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class ReentrantAttribute : Attribute
    {
    }
}