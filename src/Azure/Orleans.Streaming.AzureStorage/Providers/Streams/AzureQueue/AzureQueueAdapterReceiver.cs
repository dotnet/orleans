using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Azure.Storage.Queues.Models;
using Microsoft.Extensions.Logging;
using Orleans.AzureUtils;
using Orleans.AzureUtils.Utilities;
using Orleans.Configuration;
using Orleans.Streams;

namespace Orleans.Providers.Streams.AzureQueue
{
    /// <summary>
    /// Receives batches of messages from a single partition of a message queue.
    /// </summary>
    internal partial class AzureQueueAdapterReceiver : IQueueAdapterReceiver
    {
        private const int MaxConcurrentReleases = 32;
        private IAzureQueueDataManager? queue;
        private long lastReadMessage;
        private readonly ILogger logger;
        private readonly IQueueDataAdapter<string, IBatchContainer> dataAdapter;
        private readonly List<PendingDelivery> pending;
        private readonly object pendingLock = new();
        private int activeOperations;
        private TaskCompletionSource? operationsCompleted;

        private readonly string azureQueueName;

        public static IQueueAdapterReceiver Create(ILoggerFactory loggerFactory, string azureQueueName, AzureQueueOptions queueOptions, IQueueDataAdapter<string, IBatchContainer> dataAdapter)
        {
            if (azureQueueName == null) throw new ArgumentNullException(nameof(azureQueueName));
            if (queueOptions == null) throw new ArgumentNullException(nameof(queueOptions));
            if (dataAdapter == null) throw new ArgumentNullException(nameof(dataAdapter));

            var queue = new AzureQueueDataManager(loggerFactory, azureQueueName, queueOptions);
            return new AzureQueueAdapterReceiver(azureQueueName, loggerFactory, queue, dataAdapter);
        }

        internal AzureQueueAdapterReceiver(string azureQueueName, ILoggerFactory loggerFactory, IAzureQueueDataManager queue, IQueueDataAdapter<string, IBatchContainer> dataAdapter)
        {
            this.azureQueueName = azureQueueName ?? throw new ArgumentNullException(nameof(azureQueueName));
            this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
            this.dataAdapter = dataAdapter ?? throw new ArgumentNullException(nameof(dataAdapter));
            this.logger = loggerFactory.CreateLogger<AzureQueueAdapterReceiver>();
            this.pending = new List<PendingDelivery>();
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
            IAzureQueueDataManager? queueRef;
            Task? pendingOperations;

            lock (pendingLock)
            {
                queueRef = queue;
                queue = null;
                pendingOperations = operationsCompleted?.Task;
            }

            try
            {
                if (pendingOperations != null)
                {
                    try
                    {
                        await pendingOperations.WaitAsync(timeoutCancellation.Token);
                    }
                    catch (OperationCanceledException exception) when (timeoutCancellation.IsCancellationRequested)
                    {
                        LogWarningPendingOperationException(exception, azureQueueName);
                    }
                    catch (Exception exception)
                    {
                        LogWarningPendingOperationException(exception, azureQueueName);
                    }
                }

                QueueMessage[] pendingMessages;
                lock (pendingLock)
                {
                    pendingMessages = pending.Select(static item => item.Message).ToArray();
                }

                if (queueRef is not null && pendingMessages.Length > 0)
                {
                    var releaseTask = ReleaseMessagesAsync(queueRef, pendingMessages, timeoutCancellation.Token);
                    try
                    {
                        await releaseTask.WaitAsync(timeoutCancellation.Token);
                    }
                    catch (OperationCanceledException exception) when (timeoutCancellation.IsCancellationRequested)
                    {
                        releaseTask.Ignore();
                        LogWarningReleaseQueueMessage(exception, azureQueueName, pendingMessages.Length);
                    }
                    catch (Exception exception)
                    {
                        LogWarningReleaseQueueMessage(exception, azureQueueName, pendingMessages.Length);
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
            IAzureQueueDataManager? queueRef;
            lock (pendingLock)
            {
                queueRef = queue;
                if (queueRef is null)
                {
                    return Array.Empty<IBatchContainer>();
                }

                BeginOperation();
            }

            const int MaxNumberOfMessagesToPeek = 32;

            try
            {
                int count = maxCount < 0 || maxCount == QueueAdapterConstants.UNLIMITED_GET_QUEUE_MSG ?
                    MaxNumberOfMessagesToPeek : Math.Min(maxCount, MaxNumberOfMessagesToPeek);

                var messages = (await queueRef.GetQueueMessages(count)).ToArray();

                List<IBatchContainer> azureQueueMessages = new List<IBatchContainer>();
                List<PendingDelivery> pendingDeliveries = new List<PendingDelivery>();
                foreach (var message in messages)
                {
                    IBatchContainer container = this.dataAdapter.FromQueueMessage(message.MessageText, lastReadMessage++);
                    azureQueueMessages.Add(container);
                    pendingDeliveries.Add(new PendingDelivery(container, message));
                }

                lock (pendingLock)
                {
                    if (!ReferenceEquals(queue, queueRef))
                    {
                        pendingDeliveries.Clear();
                    }

                    foreach (var delivery in pendingDeliveries)
                    {
                        pending.RemoveAll(item => string.Equals(item.Message.MessageId, delivery.Message.MessageId, StringComparison.Ordinal));
                    }

                    pending.AddRange(pendingDeliveries);
                }

                if (pendingDeliveries.Count == 0 && messages.Length > 0)
                {
                    try
                    {
                        await ReleaseMessagesAsync(queueRef, messages, CancellationToken.None);
                    }
                    catch (Exception exception)
                    {
                        LogWarningReleaseQueueMessage(exception, azureQueueName, messages.Length);
                    }

                    return Array.Empty<IBatchContainer>();
                }

                return azureQueueMessages;
            }
            finally
            {
                EndOperation();
            }
        }

        public async Task MessagesDeliveredAsync(IList<IBatchContainer> messages)
        {
            IAzureQueueDataManager? queueRef;
            lock (pendingLock)
            {
                queueRef = queue;
                if (messages.Count == 0 || queueRef is null)
                {
                    return;
                }

                BeginOperation();
            }

            try
            {
                HashSet<PendingDelivery> delivered;
                lock (pendingLock)
                {
                    delivered = messages
                        .Select(message => pending.Find(item => ReferenceEquals(item.Batch, message)))
                        .OfType<PendingDelivery>()
                        .ToHashSet();
                }
                if (delivered.Count == 0) return;

                try
                {
                    await ConfirmMessagesDeliveredAsync(queueRef, delivered);
                }
                catch (Exception exc)
                {
                    LogWarningOnDeleteQueueMessage(exc, this.azureQueueName);
                }
            }
            finally
            {
                EndOperation();
            }
        }

        private void BeginOperation()
        {
            if (activeOperations++ == 0)
            {
                operationsCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        private void EndOperation()
        {
            TaskCompletionSource? completed = null;
            lock (pendingLock)
            {
                if (--activeOperations == 0)
                {
                    completed = operationsCompleted;
                    operationsCompleted = null;
                }
            }

            completed?.TrySetResult();
        }

        private static async Task ReleaseMessagesAsync(
            IAzureQueueDataManager queueRef,
            IEnumerable<QueueMessage> messages,
            CancellationToken cancellationToken)
        {
            List<Exception>? failures = null;
            foreach (var batch in messages.Chunk(MaxConcurrentReleases))
            {
                var results = await Task.WhenAll(batch.Select(ReleaseMessage));
                foreach (var failure in results.OfType<Exception>())
                {
                    (failures ??= []).Add(failure);
                }

                cancellationToken.ThrowIfCancellationRequested();
            }

            if (failures is not null)
            {
                throw new AggregateException("One or more Azure Queue messages could not be released.", failures);
            }

            async Task<Exception?> ReleaseMessage(QueueMessage message)
            {
                try
                {
                    await queueRef.ReleaseQueueMessage(message, cancellationToken);
                    return null;
                }
                catch (Exception exception)
                {
                    return exception;
                }
            }
        }

        private async Task ConfirmMessagesDeliveredAsync(IAzureQueueDataManager queueRef, HashSet<PendingDelivery> delivered)
        {
            await Task.WhenAll(delivered.Select(item => queueRef.DeleteQueueMessage(item.Message)));

            lock (pendingLock)
            {
                pending.RemoveAll(delivered.Contains);
            }
        }

        [LoggerMessage(
            Level = LogLevel.Warning,
            EventId = (int)AzureQueueErrorCode.AzureQueue_15,
            Message = "Exception upon DeleteQueueMessage on queue {QueueName}. Ignoring."
        )]
        private partial void LogWarningOnDeleteQueueMessage(Exception exception, string queueName);

        [LoggerMessage(
            Level = LogLevel.Warning,
            EventId = (int)AzureQueueErrorCode.AzureQueue_17,
            Message = "Exception while awaiting a pending operation for Azure queue {QueueName}. Continuing shutdown cleanup."
        )]
        private partial void LogWarningPendingOperationException(Exception exception, string queueName);

        [LoggerMessage(
            Level = LogLevel.Warning,
            EventId = (int)AzureQueueErrorCode.AzureQueue_18,
            Message = "An error occurred while releasing up to {MessageCount} pending messages for Azure queue {QueueName}."
        )]
        private partial void LogWarningReleaseQueueMessage(Exception exception, string queueName, int messageCount);

        private sealed class PendingDelivery
        {
            public PendingDelivery(IBatchContainer batch, QueueMessage message)
            {
                this.Batch = batch;
                this.Message = message;
            }

            public IBatchContainer Batch { get; }

            public QueueMessage Message { get; }
        }
    }
}
