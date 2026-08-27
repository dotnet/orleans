using System.Diagnostics;
using System.Text.Json;
using Azure.Identity;
using Azure.Storage.Queues;

namespace DistributedTests.Common.MessageChannel
{
    public interface ISendChannel
    {
        Task<List<AckMessage>> SendMessages(List<ServerMessage> messages, CancellationToken cancellationToken);
    }

    public interface IReceiveChannel
    {
        Task<ServerMessage> WaitForMessage(CancellationToken cancellationToken);

        Task SendAck(ServerMessage message, CancellationToken cancellationToken);
    }

    public class SendChannel : ISendChannel
    {
        private readonly QueueClient _writeQueue;
        private readonly QueueClient _readQueue;

        internal SendChannel(QueueClient writeQueue, QueueClient readQueue)
        {
            _writeQueue = writeQueue;
            _readQueue = readQueue;
        }

        public async Task<List<AckMessage>> SendMessages(List<ServerMessage> messages, CancellationToken cancellationToken)
        {
            foreach (var msg in messages)
            {
                await _writeQueue.SendMessageAsync(JsonSerializer.Serialize(msg), cancellationToken);
            }
            var acks = new List<AckMessage>();
            for (var i = 0; i < messages.Count; i++)
            {
                var msg = await _readQueue.WaitForMessage<AckMessage>(cancellationToken);
                acks.Add(msg);
            }
            return acks;
        }
    }

    public class ReceiveChannel : IReceiveChannel
    {
        private readonly QueueClient _writeQueue;
        private readonly QueueClient _readQueue;
        private readonly string _serverName;

        internal ReceiveChannel(QueueClient writeQueue, QueueClient readQueue, string serverName)
        {
            _writeQueue = writeQueue;
            _readQueue = readQueue;
            _serverName = serverName;
        }

        public async Task<ServerMessage> WaitForMessage(CancellationToken cancellationToken) => await _readQueue.WaitForMessage<ServerMessage>(cancellationToken);

        public async Task SendAck(ServerMessage message, CancellationToken cancellationToken)
        {
            var ack = AckMessage.CreateAckMessage(message, _serverName);
            await _writeQueue.SendMessageAsync(JsonSerializer.Serialize(ack), cancellationToken);
        }
    }

    public static class Channels
    {
        private static readonly string CLIENT_TO_SERVER_QUEUE = "servers-{0}";
        private static readonly string SILO_TO_CLIENT_QUEUE = "client-{0}";

        public static Task<ISendChannel> CreateSendChannel(
            string clusterId,
            Uri azureQueueUri,
            CancellationToken cancellationToken)
            => CreateSendChannel(clusterId, azureQueueUri.CreateQueueServiceClient(), cancellationToken);

        public static async Task<ISendChannel> CreateSendChannel(
            string clusterId,
            QueueServiceClient queueServiceClient,
            CancellationToken cancellationToken)
        {
            var writeQueue = queueServiceClient.GetQueueClient(string.Format(CLIENT_TO_SERVER_QUEUE, clusterId));
            var readQueue = queueServiceClient.GetQueueClient(string.Format(SILO_TO_CLIENT_QUEUE, clusterId));

            await writeQueue.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
            await readQueue.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

            return new SendChannel(writeQueue, readQueue);
        }

        public static Task<IReceiveChannel> CreateReceiveChannel(
            string serverName,
            string clusterId,
            Uri azureQueueUri,
            CancellationToken cancellationToken)
            => CreateReceiveChannel(serverName, clusterId, azureQueueUri.CreateQueueServiceClient(), cancellationToken);

        public static async Task<IReceiveChannel> CreateReceiveChannel(
            string serverName,
            string clusterId,
            QueueServiceClient queueServiceClient,
            CancellationToken cancellationToken)
        {
            var writeQueue = queueServiceClient.GetQueueClient(string.Format(SILO_TO_CLIENT_QUEUE, clusterId));
            var readQueue = queueServiceClient.GetQueueClient(string.Format(CLIENT_TO_SERVER_QUEUE, clusterId));

            await writeQueue.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
            await readQueue.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

            return new ReceiveChannel(writeQueue, readQueue, serverName);
        }

        internal static async Task<T> WaitForMessage<T>(
            this QueueClient queueClient,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await queueClient.ReceiveMessagesAsync(
                    maxMessages: 1,
                    cancellationToken: cancellationToken);
                var msg = result.Value?.FirstOrDefault();

                if (msg != null)
                {
                    await queueClient.DeleteMessageAsync(
                        msg.MessageId,
                        msg.PopReceipt,
                        cancellationToken);
                    // A message that was just dequeued is expected to deserialize to a valid T.
                    return JsonSerializer.Deserialize<T>(msg.MessageText)!;
                }

                await Task.Delay(1000, cancellationToken);
            }
        }
    }
}
