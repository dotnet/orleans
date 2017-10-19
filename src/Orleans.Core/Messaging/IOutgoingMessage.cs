using System;


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
    internal class OutgoingClientMessage : Tuple<GrainId, Message>, IOutgoingMessage
    {
        public OutgoingClientMessage(GrainId clientId, Message message)
            : base(clientId, message)
        {
        }

        public bool IsSameDestination(IOutgoingMessage other)
        {
            var otherTuple = (OutgoingClientMessage)other;
            return otherTuple != null && this.Item1.Equals(otherTuple.Item1);
        }

        public void Start()
        {
            this.Item2.Start();
        }

        public void Stop()
        {
            this.Item2.Stop();
        }

        public void Restart()
        {
            this.Item2.Start();
        }

        public TimeSpan Elapsed
        {
            get { return this.Item2.Elapsed; }
        }
    }
}
