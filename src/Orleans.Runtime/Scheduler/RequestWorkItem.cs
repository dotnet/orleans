using System;

namespace Orleans.Runtime.Scheduler
{
    internal sealed class RequestWorkItem : WorkItemBase
    {
        private readonly Message request;
        private readonly SystemTarget target;

        public RequestWorkItem(SystemTarget t, Message m)
        {
            target = t;
            request = m;
        }

        public override string Name => $"RequestWorkItem:Id={request.Id}";

        public override IGrainContext GrainContext => this.target;

        public override void Execute()
        {
            try
            {
                RuntimeContext.SetExecutionContext(this.target);
                target.HandleNewRequest(request);
            }
            finally
            {
                RuntimeContext.ResetExecutionContext();
            }
        }

        public override bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider provider)
        {
            if (base.TryFormat(destination, out var len, format, provider) && destination[len..].TryWrite($": {request}", out var len2))
            {
                charsWritten = len + len2;
                return true;
            }

            charsWritten = 0;
            return false;
        }
    }
}
