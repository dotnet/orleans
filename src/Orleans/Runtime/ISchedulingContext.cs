using System;

namespace Orleans.Runtime
{
    internal interface ISchedulingContext : IEquatable<ISchedulingContext>
    {
        SchedulingContextType ContextType { get; }
        string Name { get; }
        bool IsSystemPriorityContext { get; }
        string DetailedStatus();
    }
}
