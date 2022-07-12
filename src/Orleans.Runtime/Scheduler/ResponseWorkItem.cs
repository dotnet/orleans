using System;

namespace Orleans.Runtime.Scheduler
{
    internal sealed class ResponseWorkItem : WorkItemBase
    {
        private readonly Message response;
        private readonly SystemTarget target;

        public ResponseWorkItem(SystemTarget t, Message m)
        {
            target = t;
            response = m;
        }

        public override string Name => $"ResponseWorkItem:Id={response.Id},Type={response.Result}";

        public override IGrainContext GrainContext => this.target;

        public override void Execute()
        {
            try
            {
                RuntimeContext.SetExecutionContext(this.target);
                target.HandleResponse(response);
            }
            finally
            {
                RuntimeContext.ResetExecutionContext();
            }
        }

        public override bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider provider)
        {
            if (base.TryFormat(destination, out var len, format, provider) && destination[len..].TryWrite($": {response}", out var len2))
            {
                charsWritten = len + len2;
                return true;
            }

            charsWritten = 0;
            return false;
        }
    }
}
