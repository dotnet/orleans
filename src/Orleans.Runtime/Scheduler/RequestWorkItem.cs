using System;

namespace Orleans.Runtime.Scheduler
{
    internal sealed class RequestWorkItem : WorkItemBase
    {
        private readonly Message request;
        private readonly SystemTarget _target;

        public RequestWorkItem(SystemTarget t, Message m)
        {
            _target = t;
            request = m;
        }

        public override string Name => $"RequestWorkItem:Id={request.Id}";

        public override IGrainContext GrainContext => _target;

        public override void Execute() => _target.HandleNewRequest(request);

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
