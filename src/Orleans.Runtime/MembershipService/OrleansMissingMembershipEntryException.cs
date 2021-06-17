using System;
using System.Runtime.Serialization;

namespace Orleans.Runtime.MembershipService
{
    [Serializable]
    [GenerateSerializer]
    public class OrleansMissingMembershipEntryException : OrleansException
    {
        public OrleansMissingMembershipEntryException() : base("Membership table does not contain information an entry for this silo.") { }

        public OrleansMissingMembershipEntryException(string message) : base(message) { }

        public OrleansMissingMembershipEntryException(string message, Exception innerException) : base(message, innerException) { }

        public OrleansMissingMembershipEntryException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }
}
