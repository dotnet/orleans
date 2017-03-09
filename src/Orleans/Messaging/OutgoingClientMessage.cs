using System;

namespace Orleans.Runtime
{
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