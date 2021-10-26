using System;

namespace Distributed.Common
{
    public class SiloMessage
    {
        public Guid MessageId { get; }

        public bool IsGraceful { get; }

        public bool Restart { get; }

        public SiloMessage(bool isGraceful, bool restart)
        {
            MessageId = Guid.NewGuid();
            IsGraceful = isGraceful;
            Restart = restart;
        }
    }

    public class AckMessage
    {
        public Guid MessageId { get; }

        public string SiloName { get; set; }

        public AckMessage(Guid messageId, string siloName)
        {
            MessageId = messageId;
            SiloName = siloName;
        }

        public static AckMessage CreateAckMessage(SiloMessage msg, string siloName) => new(msg.MessageId, siloName);
    }
}
