namespace VotingContract;

[GenerateSerializer]
public class ThrottlingException : Exception
{
    public ThrottlingException() { }
    public ThrottlingException(string message) : base(message) { }
    public ThrottlingException(string message, Exception innerException) : base(message, innerException) { }
}
