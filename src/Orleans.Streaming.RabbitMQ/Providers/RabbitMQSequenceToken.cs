using System;
using Orleans.Providers.Streams.Common;

namespace Orleans.RabbitMQ.Providers
{
    /// <summary>
    /// Implementation of a DateTime-based sequencer for RabbitMQ so messages can be handed to consumers in FIFO order.
    /// </summary>
    [Serializable]
    public class RabbitMQSequenceToken : EventSequenceToken
    {
        public RabbitMQSequenceToken(long seqNumber) : base(seqNumber)
        {
        }

        public RabbitMQSequenceToken(long seqNumber, int eventInd) : base(seqNumber, eventInd)
        {
        }
    }
}
