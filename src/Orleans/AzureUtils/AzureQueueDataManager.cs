/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.Storage.RetryPolicies;
using Microsoft.WindowsAzure.Storage.Queue;
using Orleans.Runtime;
using Orleans.Storage;


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
    /// <typeparam name="T"></typeparam>
    internal static class AzureQueueDefaultPolicies
    {
        public static int MaxQueueOperationRetries;
        public static TimeSpan PauseBetweenQueueOperationRetries;
        public static TimeSpan QueueOperationTimeout;
        public static IRetryPolicy QueueOperationRetryPolicy;

        static AzureQueueDefaultPolicies()
        {
            MaxQueueOperationRetries = 5;
            PauseBetweenQueueOperationRetries = TimeSpan.FromMilliseconds(100);
            QueueOperationRetryPolicy = new LinearRetry(PauseBetweenQueueOperationRetries, MaxQueueOperationRetries); // 5 x 100ms
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

        protected string ConnectionString { get; set; }
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Security", "CA2104:DoNotDeclareReadOnlyMutableReferenceTypes")]
        private readonly TraceLogger logger;
        private readonly CloudQueueClient queueOperationsClient;
        private CloudQueue queue;

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="queueName">Name of the queue to be connected to.</param>
        /// <param name="storageConnectionString">Connection string for the Azure storage account used to host this table.</param>
        public AzureQueueDataManager(string queueName, string storageConnectionString)
        {
            AzureStorageUtils.ValidateQueueName(queueName);

            logger = TraceLogger.GetLogger(this.GetType().Name, TraceLogger.LoggerType.Runtime);
            QueueName = queueName;
            ConnectionString = storageConnectionString;

            queueOperationsClient = AzureStorageUtils.GetCloudQueueClient(
                ConnectionString,
                AzureQueueDefaultPolicies.QueueOperationRetryPolicy,
                AzureQueueDefaultPolicies.QueueOperationTimeout,
                logger);
        }

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="queueName">Name of the queue to be connected to.</param>
        /// <param name="deploymentId">The deployment id of the Azure service hosting this silo. It will be concatenated to the queueName.</param>
        /// <param name="storageConnectionString">Connection string for the Azure storage account used to host this table.</param>
        public AzureQueueDataManager(string queueName, string deploymentId, string storageConnectionString)
        {
            AzureStorageUtils.ValidateQueueName(queueName);

            logger = TraceLogger.GetLogger(this.GetType().Name, TraceLogger.LoggerType.Runtime);
            QueueName = deploymentId + "-" + queueName;
            AzureStorageUtils.ValidateQueueName(QueueName);
            ConnectionString = storageConnectionString;

            queueOperationsClient = AzureStorageUtils.GetCloudQueueClient(
                ConnectionString,
                AzureQueueDefaultPolicies.QueueOperationRetryPolicy,
                AzureQueueDefaultPolicies.QueueOperationTimeout,
                logger);
        }

        /// <summary>
        /// Initializes the connection to the queue.
        /// </summary>
        public async Task InitQueueAsync()
        {
            var startTime = DateTime.UtcNow;

            try
            {
                // Retrieve a reference to a queue.
                // Not sure if this is a blocking call or not. Did not find an alternative async API. Should probably use BeginListQueuesSegmented.
                queue = queueOperationsClient.GetQueueReference(QueueName);

                // Create the queue if it doesn't already exist.

                bool didCreate = await Task<bool>.Factory.FromAsync(queue.BeginCreateIfNotExists, queue.EndCreateIfNotExists, null);

                logger.Info(ErrorCode.AzureQueue_01, "{0} Azure storage queue {1}", (didCreate ? "Created" : "Attached to"), QueueName);
            }
            catch (Exception exc)
            {
                ReportErrorAndRethrow(exc, "CreateIfNotExist", ErrorCode.AzureQueue_02);
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
            if (logger.IsVerbose2) logger.Verbose2("Deleting queue: {0}", QueueName);
            try
            {
                // that way we don't have first to create the queue to be able later to delete it.
                CloudQueue queueRef = queue ?? queueOperationsClient.GetQueueReference(QueueName);
                var exists = Task<bool>.Factory.FromAsync(queueRef.BeginExists, queueRef.EndExists, null);
                if (await exists)
                {
                    await Task.Factory.FromAsync(queueRef.BeginDelete, queueRef.EndDelete, null);
                    logger.Info(ErrorCode.AzureQueue_03, "Deleted Azure Queue {0}", QueueName);
                }
            }
            catch (Exception exc)
            {
                ReportErrorAndRethrow(exc, "DeleteQueue", ErrorCode.AzureQueue_04);
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
            if (logger.IsVerbose2) logger.Verbose2("Clearing a queue: {0}", QueueName);
            try
            {
                await Task.Factory.FromAsync(queue.BeginClear, queue.EndClear, null);
                logger.Info(ErrorCode.AzureQueue_05, "Cleared Azure Queue {0}", QueueName);
            }
            catch (Exception exc)
            {
                ReportErrorAndRethrow(exc, "ClearQueue", ErrorCode.AzureQueue_06);
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
        public async Task AddQueueMessage(CloudQueueMessage message)
        {
            var startTime = DateTime.UtcNow;
            if (logger.IsVerbose2) logger.Verbose2("Adding message {0} to queue: {1}", message, QueueName);
            try
            {
                await Task.Factory.FromAsync(
                    queue.BeginAddMessage, queue.EndAddMessage, message, null);
            }
            catch (Exception exc)
            {
                ReportErrorAndRethrow(exc, "AddQueueMessage", ErrorCode.AzureQueue_07);
            }
            finally
            {
                CheckAlertSlowAccess(startTime, "AddQueueMessage");
            }
        }

        /// <summary>
        /// Peeks in the queue for latest message, without dequeueing it.
        /// </summary>
        public async Task<CloudQueueMessage> PeekQueueMessage()
        {
            var startTime = DateTime.UtcNow;
            if (logger.IsVerbose2) logger.Verbose2("Peeking a message from queue: {0}", QueueName);
            try
            {
                return await Task<CloudQueueMessage>.Factory.FromAsync( 
                    queue.BeginPeekMessage, queue.EndPeekMessage, null);
            }
            catch (Exception exc)
            {
                ReportErrorAndRethrow(exc, "PeekQueueMessage", ErrorCode.AzureQueue_08);
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
        public async Task<CloudQueueMessage> GetQueueMessage()
        {
            var startTime = DateTime.UtcNow;
            if (logger.IsVerbose2) logger.Verbose2("Getting a message from queue: {0}", QueueName);
            try
            {
                // http://msdn.microsoft.com/en-us/library/ee758456.aspx
                // If no messages are visible in the queue, GetMessage returns null.
                return await Task<CloudQueueMessage>.Factory.FromAsync(
                     queue.BeginGetMessage, queue.EndGetMessage, null);
            }
            catch (Exception exc)
            {
                ReportErrorAndRethrow(exc, "GetQueueMessage", ErrorCode.AzureQueue_09);
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
        public async Task<IEnumerable<CloudQueueMessage>> GetQueueMessages(int count = -1)
        {
            var startTime = DateTime.UtcNow;
            if (count == -1)
            {
                count = CloudQueueMessage.MaxNumberOfMessagesToPeek;
            }
            if (logger.IsVerbose2) logger.Verbose2("Getting up to {0} messages from queue: {1}", count, QueueName);
            try
            {
                return await Task<IEnumerable<CloudQueueMessage>>.Factory.FromAsync(
                     queue.BeginGetMessages, queue.EndGetMessages, count, null);
            }
            catch (Exception exc)
            {
                ReportErrorAndRethrow(exc, "GetQueueMessages", ErrorCode.AzureQueue_10);
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
        public async Task DeleteQueueMessage(CloudQueueMessage message)
        {
            var startTime = DateTime.UtcNow;
            if (logger.IsVerbose2) logger.Verbose2("Deleting a message from queue: {0}", QueueName);
            try
            {
               await Task.Factory.FromAsync(
                   queue.BeginDeleteMessage, queue.EndDeleteMessage, message.Id, message.PopReceipt, null);
            }
            catch (Exception exc)
            {
                ReportErrorAndRethrow(exc, "DeleteMessage", ErrorCode.AzureQueue_11);
            }
            finally
            {
                CheckAlertSlowAccess(startTime, "DeleteQueueMessage");
            }
        }

        internal async Task GetAndDeleteQueueMessage()
        {
            CloudQueueMessage message = await GetQueueMessage();
            await DeleteQueueMessage(message);
        }

        /// <summary>
        /// Returns an approximate number of messages in the queue.
        /// </summary>
        public async Task<int> GetApproximateMessageCount()
        {
            var startTime = DateTime.UtcNow;
            if (logger.IsVerbose2) logger.Verbose2("GetApproximateMessageCount a message from queue: {0}", QueueName);
            try
            {
                await Task.Factory.FromAsync(queue.BeginFetchAttributes, queue.EndFetchAttributes, null);
                return queue.ApproximateMessageCount.HasValue ? queue.ApproximateMessageCount.Value : 0;
            }
            catch (Exception exc)
            {
                ReportErrorAndRethrow(exc, "FetchAttributes", ErrorCode.AzureQueue_12);
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
                logger.Warn(ErrorCode.AzureQueue_13, "Slow access to Azure queue {0} for {1}, which took {2}.", QueueName, operation, timeSpan);
            }
        }

        private void ReportErrorAndRethrow(Exception exc, string operation, ErrorCode errorCode)
        {
            var errMsg = String.Format(
                "Error doing {0} for Azure storage queue {1} " + Environment.NewLine 
                + "Exception = {2}", operation, QueueName, exc);
            logger.Error(errorCode, errMsg, exc);
            throw new AggregateException(errMsg, exc);
        }
    }
}

