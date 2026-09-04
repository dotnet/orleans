using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Configuration;
using Orleans.Streaming.SQS.Streams;
using Orleans.Streams;
using OrleansAWSUtils.Storage;
using SQSMessage = Amazon.SQS.Model.Message;

namespace OrleansAWSUtils.Streams
{
    /// <summary>
    /// Receives batches of messages from a single partition of a message queue.
    /// </summary>
    internal partial class SQSAdapterReceiver : IQueueAdapterReceiver
    {
        private SQSStorage? queue;
        private long lastReadMessage = -1;
        private Task? outstandingTask;
        private readonly ILogger logger;
        private readonly ISQSDataAdapter dataAdapter;
        private readonly List<PendingDelivery> pending = [];
        private readonly object pendingLock = new();

        public QueueId Id { get; private set; }

        public static IQueueAdapterReceiver Create(ISQSDataAdapter dataAdapter, ILoggerFactory loggerFactory, QueueId queueId, SqsOptions sqsOptions, string serviceId)
        {
            if (queueId.IsDefault) throw new ArgumentNullException(nameof(queueId));
            if (sqsOptions is null) throw new ArgumentNullException(nameof(sqsOptions));
            if (string.IsNullOrEmpty(serviceId)) throw new ArgumentNullException(nameof(serviceId));

            var queue = new SQSStorage(loggerFactory, queueId.ToString(), sqsOptions, serviceId);
            return new SQSAdapterReceiver(dataAdapter, loggerFactory, queueId, queue);
        }

        private SQSAdapterReceiver(ISQSDataAdapter dataAdapter, ILoggerFactory loggerFactory, QueueId queueId, SQSStorage queue)
        {
            if (queueId.IsDefault) throw new ArgumentNullException(nameof(queueId));
            if (queue == null) throw new ArgumentNullException(nameof(queue));

            Id = queueId;
            this.queue = queue;
            logger = loggerFactory.CreateLogger<SQSAdapterReceiver>();
            this.dataAdapter = dataAdapter;
        }

        public Task Initialize(TimeSpan timeout)
        {
            if (queue != null) // check in case we already shut it down.
            {
                return queue.InitQueueAsync();
            }
            return Task.CompletedTask;
        }

        public async Task Shutdown(TimeSpan timeout)
        {
            using var timeoutCancellation = new CancellationTokenSource(timeout);
            SQSStorage? queueRef;

            lock (pendingLock)
            {
                queueRef = queue;
                queue = null;
            }

            try
            {
                // await the last storage operation, so after we shutdown and stop this receiver we don't get async operation completions from pending storage operations.
                var pendingTask = outstandingTask;
                if (pendingTask != null)
                {
                    try
                    {
                        await pendingTask.WaitAsync(timeoutCancellation.Token);
                    }
                    catch (OperationCanceledException exception) when (timeoutCancellation.IsCancellationRequested)
                    {
                        pendingTask.Ignore();
                        LogWarningPendingOperationException(logger, exception, Id);
                    }
                    catch (Exception exception)
                    {
                        LogWarningPendingOperationException(logger, exception, Id);
                    }
                }

                SQSMessage[] pendingMessages;
                lock (pendingLock)
                {
                    pendingMessages = pending.Select(static item => item.Message).ToArray();
                }

                if (queueRef is not null && pendingMessages.Length > 0)
                {
                    var releaseTask = queueRef.ReleaseMessages(pendingMessages);
                    try
                    {
                        await releaseTask.WaitAsync(timeoutCancellation.Token);
                    }
                    catch (OperationCanceledException exception) when (timeoutCancellation.IsCancellationRequested)
                    {
                        releaseTask.Ignore();
                        LogWarningReleaseMessageException(logger, exception, Id, pendingMessages.Length);
                    }
                    catch (Exception exc)
                    {
                        LogWarningReleaseMessageException(logger, exc, Id, pendingMessages.Length);
                    }
                }
            }
            finally
            {
                lock (pendingLock)
                {
                    pending.Clear();
                }
            }
        }

        public async Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount)
        {
            try
            {
                var queueRef = queue; // store direct ref, in case we are somehow asked to shutdown while we are receiving.
                if (queueRef == null) return new List<IBatchContainer>();

                int count = maxCount < 0 || maxCount == QueueAdapterConstants.UNLIMITED_GET_QUEUE_MSG ?
                    SQSStorage.MAX_NUMBER_OF_MESSAGE_TO_PEEK : Math.Min(maxCount, SQSStorage.MAX_NUMBER_OF_MESSAGE_TO_PEEK);

                var task = queueRef.GetMessages(count);
                outstandingTask = task;
                var messages = (await task).ToArray();
                if (messages.Length == 0)
                    return Array.Empty<IBatchContainer>();

                var messageBatch = new List<IBatchContainer>();
                var pendingDeliveries = new List<PendingDelivery>();
                foreach (var message in messages)
                {
                    var sequenceId = Interlocked.Increment(ref lastReadMessage);
                    var batch = dataAdapter.FromQueueMessage(message, sequenceId);
                    messageBatch.Add(batch);
                    pendingDeliveries.Add(new PendingDelivery(batch, message));
                }

                lock (pendingLock)
                {
                    if (!ReferenceEquals(queue, queueRef))
                    {
                        pendingDeliveries.Clear();
                    }

                    foreach (var delivery in pendingDeliveries)
                    {
                        if (!string.IsNullOrEmpty(delivery.Message.MessageId))
                        {
                            pending.RemoveAll(item => string.Equals(item.Message.MessageId, delivery.Message.MessageId, StringComparison.Ordinal));
                        }
                    }

                    pending.AddRange(pendingDeliveries);
                }

                if (pendingDeliveries.Count == 0)
                {
                    try
                    {
                        await queueRef.ReleaseMessages(messages);
                    }
                    catch (Exception exception)
                    {
                        LogWarningReleaseMessageException(logger, exception, Id, messages.Length);
                    }

                    return Array.Empty<IBatchContainer>();
                }

                return messageBatch;
            }
            finally
            {
                outstandingTask = null;
            }
        }

        public async Task MessagesDeliveredAsync(IList<IBatchContainer> messages)
        {
            try
            {
                var queueRef = queue; // store direct ref, in case we are somehow asked to shutdown while we are receiving.
                if (messages.Count == 0 || queueRef == null) return;

                var pendingDeliveries = new HashSet<PendingDelivery>(ReferenceEqualityComparer.Instance);
                lock (pendingLock)
                {
                    foreach (var message in messages)
                    {
                        var delivery = pending.Find(item => ReferenceEquals(item.Batch, message));
                        if (delivery is not null)
                        {
                            pendingDeliveries.Add(delivery);
                        }
                    }
                }

                if (pendingDeliveries.Count == 0) return;

                outstandingTask = ConfirmMessagesDeliveredAsync(queueRef, pendingDeliveries);
                try
                {
                    await outstandingTask;
                }
                catch (Exception exc)
                {
                    LogWarningDeleteMessageException(logger, exc, Id);
                }
            }
            finally
            {
                outstandingTask = null;
            }
        }

        private async Task ConfirmMessagesDeliveredAsync(SQSStorage queueRef, HashSet<PendingDelivery> deliveries)
        {
            await queueRef.DeleteMessages(deliveries.Select(static item => item.Message));

            lock (pendingLock)
            {
                pending.RemoveAll(deliveries.Contains);
            }
        }

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Exception upon DeleteMessage on queue {Id}. Ignoring."
        )]
        private static partial void LogWarningDeleteMessageException(ILogger logger, Exception exception, QueueId id);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Exception while awaiting a pending operation for queue {Id}. Continuing shutdown cleanup."
        )]
        private static partial void LogWarningPendingOperationException(ILogger logger, Exception exception, QueueId id);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "An error occurred while releasing up to {MessageCount} pending messages for queue {Id}."
        )]
        private static partial void LogWarningReleaseMessageException(ILogger logger, Exception exception, QueueId id, int messageCount);

        private sealed record PendingDelivery(IBatchContainer Batch, SQSMessage Message);
    }
}
