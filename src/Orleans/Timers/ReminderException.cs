using System;
using System.Runtime.Serialization;

namespace Orleans.Runtime
{
    /// <summary>
    /// Exception related to Orleans Reminder functions or Reminder service.
    /// </summary>
    [Serializable]
    public class ReminderException : OrleansException
    {
        public ReminderException(string msg) : base(msg) { }

#if !NETSTANDARD
        public ReminderException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
#endif
    }
}