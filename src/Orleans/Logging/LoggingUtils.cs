using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime
{
    /// <summary>
    /// Logging Utility 
    /// </summary>
    public class LoggingUtils
    {
        public const int MAX_LOG_MESSAGE_SIZE = 20000;
        internal static EventId CreateEventId(ErrorCode errorCode)
        {
            return new EventId((int)errorCode);
        }
    }
}
