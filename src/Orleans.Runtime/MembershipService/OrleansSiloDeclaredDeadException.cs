using System;
using System.Runtime.Serialization;

namespace Orleans.Runtime.MembershipService
{
    public class OrleansSiloDeclaredDeadException : OrleansException
    {
        public const string BaseMessage = "This silo has been declared dead.";

        public OrleansSiloDeclaredDeadException() : base(BaseMessage) { }

        public OrleansSiloDeclaredDeadException(string message) : base(message) { }

        public OrleansSiloDeclaredDeadException(string message, Exception innerException) : base(message, innerException) { }

        public OrleansSiloDeclaredDeadException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }
}
