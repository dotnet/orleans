using System;

namespace DistributedTests.Common.MessageChannel
{
    public class ServerMessage
    {
        public Guid MessageId { get; }

        public bool IsGraceful { get; }

        public bool Restart { get; }

        public ServerMessage(bool isGraceful, bool restart)
        {
            MessageId = Guid.NewGuid();
            IsGraceful = isGraceful;
            Restart = restart;
        }
    }

    public class AckMessage
    {
        public Guid MessageId { get; }

        public string ServerName { get; set; }

        public AckMessage(Guid messageId, string serverName)
        {
            MessageId = messageId;
            ServerName = serverName;
        }

        public static AckMessage CreateAckMessage(ServerMessage msg, string serverName) => new(msg.MessageId, serverName);
    }
}
