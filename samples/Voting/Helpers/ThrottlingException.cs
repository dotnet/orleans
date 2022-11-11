using System.Runtime.Serialization;

namespace VotingContract;

[GenerateSerializer]
public class ThrottlingException : Exception
{
    public ThrottlingException(string message) : base(message) { }
    public ThrottlingException(string message, Exception innerException) : base(message, innerException) { }
    protected ThrottlingException(SerializationInfo info, StreamingContext context) : base(info, context) { }
}
