namespace Orleans.Runtime
{
    // Used for Client -> gateway and Silo <-> Silo messeging
    // Message implements ITimeInterval to be able to measure different time intervals in the lifecycle of a message,
    // such as time in queue...
    internal interface IOutgoingMessage : ITimeInterval
    {
        bool IsSameDestination(IOutgoingMessage other);
    }

    // Used for gateway -> Client messaging
}
