using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure.Storage.Queues;
using Microsoft.Extensions.Configuration;

namespace Distributed.Common
{
    public interface ISendChannel
    {
        Task<List<AckMessage>> SendMessages(List<SiloMessage> messages, CancellationToken cancellationToken);
    }

    public interface IReceiveChannel
    {
        Task<SiloMessage> WaitForMessage(CancellationToken cancellationToken);

        Task SendAck(SiloMessage message);
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

        public async Task<List<AckMessage>> SendMessages(List<SiloMessage> messages, CancellationToken cancellationToken)
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
        private readonly string _siloName;

        internal ReceiveChannel(QueueClient writeQueue, QueueClient readQueue, string siloName)
        {
            _writeQueue = writeQueue;
            _readQueue = readQueue;
            _siloName = siloName;
        }

        public async Task<SiloMessage> WaitForMessage(CancellationToken cancellationToken) => await _readQueue.WaitForMessage<SiloMessage>(cancellationToken);

        public async Task SendAck(SiloMessage message)
        {
            var ack = AckMessage.CreateAckMessage(message, _siloName);
            await _writeQueue.SendMessageAsync(JsonSerializer.Serialize(ack));
        }
    }

    public static class Channels
    {
        private static readonly string CLIENT_TO_SILO_QUEUE = "silos-{0}";
        private static readonly string SILO_TO_CLIENT_QUEUE = "client-{0}";

        public static async Task<ISendChannel> CreateSendChannel(string clusterId, SecretConfiguration configuration)
        {
            var writeQueue = new QueueClient(configuration.ClusteringConnectionString, string.Format(CLIENT_TO_SILO_QUEUE, clusterId));
            var readQueue = new QueueClient(configuration.ClusteringConnectionString, string.Format(SILO_TO_CLIENT_QUEUE, clusterId));

            await writeQueue.CreateIfNotExistsAsync();
            await readQueue.CreateIfNotExistsAsync();

            return new SendChannel(writeQueue, readQueue);
        }

        public static async Task<IReceiveChannel> CreateReceiveChannel(string siloName, string clusterId, SecretConfiguration configuration)
        {
            var readQueue = new QueueClient(configuration.ClusteringConnectionString, string.Format(CLIENT_TO_SILO_QUEUE, clusterId));
            var writeQueue = new QueueClient(configuration.ClusteringConnectionString, string.Format(SILO_TO_CLIENT_QUEUE, clusterId));

            await writeQueue.CreateIfNotExistsAsync();
            await readQueue.CreateIfNotExistsAsync();

            return new ReceiveChannel(writeQueue, readQueue, siloName);
        }

        internal static async Task<T> WaitForMessage<T>(this QueueClient queueClient, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await queueClient.ReceiveMessagesAsync(maxMessages: 1, cancellationToken: ct);
                var msg = result.Value?.FirstOrDefault();

                if (msg != null)
                {
                    await queueClient.DeleteMessageAsync(msg.MessageId, msg.PopReceipt);
                    return JsonSerializer.Deserialize<T>(msg.MessageText);
                }

                await Task.Delay(1000, ct);
            }
            ct.ThrowIfCancellationRequested();
            throw new Exception("No message");
        }
    }
}
