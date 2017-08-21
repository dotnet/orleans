using Microsoft.Extensions.Logging;

namespace Orleans.Runtime
{
    internal class LoggingUtils
    {
        internal static EventId CreateEventId(ErrorCode errorCode)
        {
            return new EventId((int)errorCode);
        }
    }
}
