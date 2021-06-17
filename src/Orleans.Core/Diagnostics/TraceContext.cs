using System;

namespace Orleans.Runtime
{
    [GenerateSerializer]
    internal class TraceContext
    {
        [Id(1)]
        public Guid ActivityId { get; set; }

        public override bool Equals(object obj) => obj is TraceContext context && ActivityId.Equals(context.ActivityId);
        public override int GetHashCode() => HashCode.Combine(ActivityId);
    }
}
