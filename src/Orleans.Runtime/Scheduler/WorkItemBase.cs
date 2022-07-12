using System;

namespace Orleans.Runtime.Scheduler
{
    internal abstract class WorkItemBase : IWorkItem, ISpanFormattable
    {
        public abstract IGrainContext GrainContext { get; }

        public abstract string Name { get; }

        public abstract void Execute();

        public sealed override string ToString() => $"{this}";

        string IFormattable.ToString(string format, IFormatProvider formatProvider) => ToString();

        public virtual bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider provider)
            => destination.TryWrite($"[{GetType().Name} WorkItem Name={Name}, Ctx={GrainContext}{(GrainContext != null ? null : "null")}]", out charsWritten);
    }
}

