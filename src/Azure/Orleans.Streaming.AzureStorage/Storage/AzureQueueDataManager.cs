using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Azure;
using Azure.Core;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Microsoft.Extensions.Logging;
using Orleans.AzureUtils.Utilities;
using Orleans.Configuration;
using Orleans.Runtime;

namespace Orleans.AzureUtils
{
    /// <summary>
    /// How to use the Queue Storage Service: http://www.windowsazure.com/en-us/develop/net/how-to-guides/queue-service/
    /// Windows Azure Storage Abstractions and their Scalability Targets: http://blogs.msdn.com/b/windowsazurestorage/archive/2010/05/10/windows-azure-storage-abstractions-and-their-scalability-targets.aspx
    /// Naming Queues and Metadata: http://msdn.microsoft.com/en-us/library/windowsazure/dd179349.aspx
    /// Windows Azure Queues and Windows Azure Service Bus Queues - Compared and Contrasted: http://msdn.microsoft.com/en-us/library/hh767287(VS.103).aspx
    /// Status and Error Codes: http://msdn.microsoft.com/en-us/library/dd179382.aspx
    ///
    /// http://blogs.msdn.com/b/windowsazurestorage/archive/tags/scalability/
    /// http://blogs.msdn.com/b/windowsazurestorage/archive/2010/12/30/windows-azure-storage-architecture-overview.aspx
    /// http://blogs.msdn.com/b/windowsazurestorage/archive/2010/11/06/how-to-get-most-out-of-windows-azure-tables.aspx
    ///
    /// </summary>
    internal static class AzureQueueDefaultPolicies
    {
        public static int MaxQueueOperationRetries;
        public static TimeSpan PauseBetweenQueueOperationRetries;
        public static TimeSpan QueueOperationTimeout;

        static AzureQueueDefaultPolicies()
        {
            MaxQueueOperationRetries = 5;
            PauseBetweenQueueOperationRetries = TimeSpan.FromMilliseconds(100);
            QueueOperationTimeout = PauseBetweenQueueOperationRetries.Multiply(MaxQueueOperationRetries).Multiply(6);    // 3 sec
        }
    }

    /// <summary>
    /// Utility class to encapsulate access to Azure queue storage.
    /// </summary>
    /// <remarks>
    /// Used by Azure queue streaming provider.
    /// </remarks>
    public class AzureQueueDataManager
    {
        /// <summary> Name of the table queue instance is managing. </summary>
        public string QueueName { get; private set; }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Security", "CA2104:DoNotDeclareReadOnlyMutableReferenceTypes")]
        private readonly ILogger logger;
        private readonly TimeSpan? messageVisibilityTimeout;
        private readonly AzureQueueOptions options;
        private QueueClient _queueClient;

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="loggerFactory">logger factory to use</param>
        /// <param name="queueName">Name of the queue to be connected to.</param>
        /// <param name="storageConnectionString">Connection string for the Azure storage account used to host this table.</param>
        /// <param name="visibilityTimeout">A TimeSpan specifying the visibility timeout interval</param>
        public AzureQueueDataManager(ILoggerFactory loggerFactory, string queueName, string storageConnectionString, TimeSpan? visibilityTimeout = null)
            : this (loggerFactory, queueName, ConfigureOptions(storageConnectionString, visibilityTimeout))
        {
        }

        private static AzureQueueOptions ConfigureOptions(string storageConnectionString, TimeSpan? visibilityTimeout)
        {
            var options = new AzureQueueOptions
            {
                MessageVisibilityTimeout = visibilityTimeout
            };
            options.ConfigureQueueServiceClient(storageConnectionString);
            return options;
        }

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="loggerFactory">logger factory to use</param>
        /// <param name="queueName">Name of the queue to be connected to.</param>
        /// <param name="options">Queue connection options.</param>
        public AzureQueueDataManager(ILoggerFactory loggerFactory, string queueName, AzureQueueOptions options)
        {
            queueName = SanitizeQueueName(queueName);
            ValidateQueueName(queueName);

            logger = loggerFactory.CreateLogger<AzureQueueDataManager>();
            QueueName = queueName;
            messageVisibilityTimeout = options.MessageVisibilityTimeout;
            this.options = options;
        }

        private ValueTask<QueueClient> GetQueueClient()
        {
            if (_queueClient is { } client)
            {
                return new(client);
            }

            return GetQueueClientAsync();
            async ValueTask<QueueClient> GetQueueClientAsync() => _queueClient ??= await GetCloudQueueClient(options, logger);
        }

        /// <summary>
        /// Initializes the connection to the queue.
        /// </summary>
        public async Task InitQueueAsync()
        {
            var startTime = DateTime.UtcNow;

            try
            {
                // Create the queue if it doesn't already exist.
                var client = await GetQueueClient();
                var response = await client.CreateIfNotExistsAsync();
                logger.LogInformation((int)AzureQueueErrorCode.AzureQueue_01, "Connected to Azure storage queue {QueueName}", QueueName);
            }
            catch (Exception exc)
            {
                ReportErrorAndRethrow(exc, "CreateIfNotExist", AzureQueueErrorCode.AzureQueue_02);
            }
            finally
            {
                CheckAlertSlowAccess(startTime, "InitQueue_Async");
            }
        }

        /// <summary>
        /// Deletes the queue.
        /// </summary>
        public async Task DeleteQueue()
        {
            var startTime = DateTime.UtcNow;
            if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Deleting queue: {0}", QueueName);
            try
            {
                // that way we don't have first to create the queue to be able later to delete it.
                var client = await GetQueueClient();
                if (await client.DeleteIfExistsAsync())
                {
                    logger.Info((int)AzureQueueErrorCode.AzureQueue_03, "Deleted Azure Queue {0}", QueueName);
                }
            }
            catch (Exception exc)
            {
                ReportErrorAndRethrow(exc, "DeleteQueue", AzureQueueErrorCode.AzureQueue_04);
            }
            finally
            {
                CheckAlertSlowAccess(startTime, "DeleteQueue");
            }
        }

        /// <summary>
        /// Clears the queue.
        /// </summary>
        public async Task ClearQueue()
        {
            var startTime = DateTime.UtcNow;
            if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Clearing a queue: {0}", QueueName);
            try
            {
                // that way we don't have first to create the queue to be able later to delete it.
                var client = await GetQueueClient();
                await client.ClearMessagesAsync();
                logger.Info((int)AzureQueueErrorCode.AzureQueue_05, "Cleared Azure Queue {0}", QueueName);
            }
            catch (RequestFailedException exc)
            {
                if (exc.Status != (int)HttpStatusCode.NotFound)
                {
                    ReportErrorAndRethrow(exc, "ClearQueue", AzureQueueErrorCode.AzureQueue_06);
                }
            }
            catch (Exception exc)
            {
                ReportErrorAndRethrow(exc, "ClearQueue", AzureQueueErrorCode.AzureQueue_06);
            }
            finally
            {
                CheckAlertSlowAccess(startTime, "ClearQueue");
            }
        }

        /// <summary>
        /// Adds a new message to the queue.
        /// </summary>
        /// <param name="message">Message to be added to the queue.</param>
        public async Task AddQueueMessage(string message)
        {
            var startTime = DateTime.UtcNow;
            if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Adding message {0} to queue: {1}", message, QueueName);
            try
            {
                var client = await GetQueueClient();
                await client.SendMessageAsync(message);
            }
            catch (Exception exc)
            {
                ReportErrorAndRethrow(exc, "AddQueueMessage", AzureQueueErrorCode.AzureQueue_07);
            }
            finally
            {
                CheckAlertSlowAccess(startTime, "AddQueueMessage");
            }
        }

        /// <summary>
        /// Peeks in the queue for latest message, without dequeuing it.
        /// </summary>
        public async Task<PeekedMessage> PeekQueueMessage()
        {
            var startTime = DateTime.UtcNow;
            if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Peeking a message from queue: {0}", QueueName);
            try
            {
                var client = await GetQueueClient();
                var messages = await client.PeekMessagesAsync(maxMessages: 1);
                return messages.Value.FirstOrDefault();

            }
            catch (Exception exc)
            {
                ReportErrorAndRethrow(exc, "PeekQueueMessage", AzureQueueErrorCode.AzureQueue_08);
                return null; // Dummy statement to keep compiler happy
            }
            finally
            {
                CheckAlertSlowAccess(startTime, "PeekQueueMessage");
            }
        }


        /// <summary>
        /// Gets a new message from the queue.
        /// </summary>
        public async Task<QueueMessage> GetQueueMessage()
        {
               var startTime = DateTime.UtcNow;
            if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Getting a message from queue: {0}", QueueName);
            try
            {
                //BeginGetMessage and EndGetMessage is not supported in netstandard, may be use GetMessageAsync
                // http://msdn.microsoft.com/en-us/library/ee758456.aspx
                // If no messages are visible in the queue, GetMessage returns null.
                var client = await GetQueueClient();
                var messages = await client.ReceiveMessagesAsync(maxMessages: 1, messageVisibilityTimeout);
                return messages.Value.FirstOrDefault();
            }
            catch (Exception exc)
            {
                ReportErrorAndRethrow(exc, "GetQueueMessage", AzureQueueErrorCode.AzureQueue_09);
                return null; // Dummy statement to keep compiler happy
            }
            finally
            {
                CheckAlertSlowAccess(startTime, "GetQueueMessage");
            }
        }

        /// <summary>
        /// Gets a number of new messages from the queue.
        /// </summary>
        /// <param name="count">Number of messages to get from the queue.</param>
        public async Task<IEnumerable<QueueMessage>> GetQueueMessages(int? count = null)
        {
            var startTime = DateTime.UtcNow;
            if (count == -1)
            {
                count = null;
            }

            if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Getting up to {0} messages from queue: {1}", count, QueueName);
            try
            {
                var client = await GetQueueClient();
                var messages = await client.ReceiveMessagesAsync(count, messageVisibilityTimeout);
                return messages.Value;
            }
            catch (Exception exc)
            {
                ReportErrorAndRethrow(exc, "GetQueueMessages", AzureQueueErrorCode.AzureQueue_10);
                return null; // Dummy statement to keep compiler happy
            }
            finally
            {
                CheckAlertSlowAccess(startTime, "GetQueueMessages");
            }
        }

        /// <summary>
        /// Deletes a messages from the queue.
        /// </summary>
        /// <param name="message">A message to be deleted from the queue.</param>
        public async Task DeleteQueueMessage(QueueMessage message)
        {
            var startTime = DateTime.UtcNow;
            if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("Deleting a message from queue: {0}", QueueName);
            try
            {
                var client = await GetQueueClient();
                await client.DeleteMessageAsync(message.MessageId, message.PopReceipt);

            }
            catch (RequestFailedException exc)
            {
                if (exc.Status != (int)HttpStatusCode.NotFound)
                {
                    ReportErrorAndRethrow(exc, "DeleteMessage", AzureQueueErrorCode.AzureQueue_11);
                }
            }
            catch (Exception exc)
            {
                ReportErrorAndRethrow(exc, "DeleteMessage", AzureQueueErrorCode.AzureQueue_11);
            }
            finally
            {
                CheckAlertSlowAccess(startTime, "DeleteQueueMessage");
            }
        }

        internal async Task GetAndDeleteQueueMessage()
        {
            var message = await GetQueueMessage();
            await DeleteQueueMessage(message);
        }

        /// <summary>
        /// Returns an approximate number of messages in the queue.
        /// </summary>
        public async Task<int> GetApproximateMessageCount()
        {
            var startTime = DateTime.UtcNow;
            if (logger.IsEnabled(LogLevel.Trace)) logger.Trace("GetApproximateMessageCount a message from queue: {0}", QueueName);
            try
            {
                var client = await GetQueueClient();
                var properties = await client.GetPropertiesAsync();
                return properties.Value.ApproximateMessagesCount;

            }
            catch (Exception exc)
            {
                ReportErrorAndRethrow(exc, "FetchAttributes", AzureQueueErrorCode.AzureQueue_12);
                return 0; // Dummy statement to keep compiler happy
            }
            finally
            {
                CheckAlertSlowAccess(startTime, "GetApproximateMessageCount");
            }
        }

        private void CheckAlertSlowAccess(DateTime startOperation, string operation)
        {
            var timeSpan = DateTime.UtcNow - startOperation;
            if (timeSpan > AzureQueueDefaultPolicies.QueueOperationTimeout)
            {
                logger.Warn((int)AzureQueueErrorCode.AzureQueue_13, "Slow access to Azure queue {0} for {1}, which took {2}.", QueueName, operation, timeSpan);
            }
        }

        private void ReportErrorAndRethrow(Exception exc, string operation, AzureQueueErrorCode errorCode)
        {
            var errMsg = String.Format(
                "Error doing {0} for Azure storage queue {1} " + Environment.NewLine
                + "Exception = {2}", operation, QueueName, exc);
            logger.Error((int)errorCode, errMsg, exc);
            throw new AggregateException(errMsg, exc);
        }

        private async Task<QueueClient> GetCloudQueueClient(AzureQueueOptions options, ILogger logger)
        {
            try
            {
                var client = await options.CreateClient();
                return client.GetQueueClient(QueueName);
            }
            catch (Exception exc)
            {
                logger.LogError((int)AzureQueueErrorCode.AzureQueue_14, exc, "Error creating Azure Storage Queues client");
                throw;
            }
        }

        private string SanitizeQueueName(string queueName)
        {
            var tmp = queueName;
            //Azure queue naming rules : https://docs.microsoft.com/en-us/rest/api/storageservices/Naming-Queues-and-Metadata?redirectedfrom=MSDN
            tmp = tmp.ToLowerInvariant();
            tmp = tmp
                .Replace('/', '-') // Forward slash
                .Replace('\\', '-') // Backslash
                .Replace('#', '-') // Pound sign
                .Replace('?', '-') // Question mark
                .Replace('&', '-')
                .Replace('+', '-')
                .Replace(':', '-')
                .Replace('.', '-')
                .Replace('%', '-');
            return tmp;
        }

        private void ValidateQueueName(string queueName)
        {
            // Naming Queues and Metadata: http://msdn.microsoft.com/en-us/library/windowsazure/dd179349.aspx
            if (!(queueName.Length >= 3 && queueName.Length <= 63))
            {
                // A queue name must be from 3 through 63 characters long.
                throw new ArgumentException(String.Format("A queue name must be from 3 through 63 characters long, while your queueName length is {0}, queueName is {1}.", queueName.Length, queueName), queueName);
            }

            if (!Char.IsLetterOrDigit(queueName.First()))
            {
                // A queue name must start with a letter or number
                throw new ArgumentException(String.Format("A queue name must start with a letter or number, while your queueName is {0}.", queueName), queueName);
            }

            if (!Char.IsLetterOrDigit(queueName.Last()))
            {
                // The first and last letters in the queue name must be alphanumeric. The dash (-) character cannot be the first or last character.
                throw new ArgumentException(String.Format("The last letter in the queue name must be alphanumeric, while your queueName is {0}.", queueName), queueName);
            }

            if (!queueName.All(c => Char.IsLetterOrDigit(c) || c.Equals('-')))
            {
                // A queue name can only contain letters, numbers, and the dash (-) character.
                throw new ArgumentException(String.Format("A queue name can only contain letters, numbers, and the dash (-) character, while your queueName is {0}.", queueName), queueName);
            }

            if (queueName.Contains("--"))
            {
                // Consecutive dash characters are not permitted in the queue name.
                throw new ArgumentException(String.Format("Consecutive dash characters are not permitted in the queue name, while your queueName is {0}.", queueName), queueName);
            }

            if (queueName.Where(Char.IsLetter).Any(c => !Char.IsLower(c)))
            {
                // All letters in a queue name must be lowercase.
                throw new ArgumentException(String.Format("All letters in a queue name must be lowercase, while your queueName is {0}.", queueName), queueName);
            }
        }
    }
}
