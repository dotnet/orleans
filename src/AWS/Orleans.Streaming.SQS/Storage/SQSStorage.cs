using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Configuration;
using Orleans.Streaming.SQS;
using SQSMessage = Amazon.SQS.Model.Message;

namespace OrleansAWSUtils.Storage
{
    /// <summary>
    /// Wrapper/Helper class around AWS SQS queue service
    /// </summary>
    internal partial class SQSStorage
    {
        /// <summary>
        /// Maximum number of messages allowed by SQS to peak per request
        /// </summary>
        public const int MAX_NUMBER_OF_MESSAGE_TO_PEEK = 10;
        private const string AccessKeyPropertyName = "AccessKey";
        private const string SecretKeyPropertyName = "SecretKey";
        private const string SessionTokenPropertyName = "SessionToken";
        private const string ServicePropertyName = "Service";
        private readonly SqsOptions sqsOptions;
        private readonly ILogger Logger;
        private string? accessKey;
        private string? secretKey;
        private string? sessionToken;
        private string service = null!;
        private string? queueUrl;
        private AmazonSQSClient sqsClient = null!;

        private readonly List<string> receiveMessageSystemAttributes;
        private readonly List<string> receiveMessageAttributes;


        /// <summary>
        /// The queue Name
        /// </summary>
        public string QueueName { get; private set; }

        /// <summary>
        /// Default Ctor
        /// </summary>
        /// <param name="loggerFactory">logger factory to use</param>
        /// <param name="queueName">The name of the queue</param>
        /// <param name="sqsOptions">The options for the SQS connection</param>
        /// <param name="serviceId">The service ID</param>
        public SQSStorage(ILoggerFactory loggerFactory, string queueName, SqsOptions sqsOptions, string serviceId = "")
        {
            if (sqsOptions is null) throw new ArgumentNullException(nameof(sqsOptions));
            this.sqsOptions = sqsOptions;
            QueueName = ConstructQueueName(queueName, sqsOptions, serviceId);
            ParseDataConnectionString(sqsOptions.ConnectionString);
            Logger = loggerFactory.CreateLogger<SQSStorage>();
            CreateClient();

            receiveMessageSystemAttributes = [.. sqsOptions.ReceiveMessageSystemAttributes];
            receiveMessageAttributes = [.. sqsOptions.ReceiveMessageAttributes];

            if (sqsOptions.FifoQueue)
            {
                if (!receiveMessageSystemAttributes.Contains(MessageSystemAttributeName.SequenceNumber))
                    receiveMessageSystemAttributes.Add(MessageSystemAttributeName.SequenceNumber);
            }
        }

        private void ParseDataConnectionString(string dataConnectionString)
        {
            if (string.IsNullOrEmpty(dataConnectionString)) throw new ArgumentNullException(nameof(dataConnectionString));

            var parameters = dataConnectionString.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);

            var serviceConfig = Array.Find(parameters, p => p.Contains(ServicePropertyName));
            if (!string.IsNullOrWhiteSpace(serviceConfig))
            {
                var value = serviceConfig.Split('=', StringSplitOptions.RemoveEmptyEntries);
                if (value.Length == 2 && !string.IsNullOrWhiteSpace(value[1]))
                    service = value[1];
            }

            var secretKeyConfig = Array.Find(parameters, p => p.Contains(SecretKeyPropertyName));
            if (!string.IsNullOrWhiteSpace(secretKeyConfig))
            {
                var value = secretKeyConfig.Split('=', StringSplitOptions.RemoveEmptyEntries);
                if (value.Length == 2 && !string.IsNullOrWhiteSpace(value[1]))
                    secretKey = value[1];
            }

            var accessKeyConfig = Array.Find(parameters, p => p.Contains(AccessKeyPropertyName));
            if (!string.IsNullOrWhiteSpace(accessKeyConfig))
            {
                var value = accessKeyConfig.Split('=', StringSplitOptions.RemoveEmptyEntries);
                if (value.Length == 2 && !string.IsNullOrWhiteSpace(value[1]))
                    accessKey = value[1];
            }

            var sessionTokenConfig = parameters.Where(p => p.Contains(SessionTokenPropertyName)).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(sessionTokenConfig))
            {
                var value = sessionTokenConfig.Split('=', 2, StringSplitOptions.RemoveEmptyEntries);
                if (value.Length == 2 && !string.IsNullOrWhiteSpace(value[1]))
                    sessionToken = value[1];
            }
        }

        private void CreateClient()
        {
            if (service.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                service.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                // Local SQS instance (for testing)
                var credentials = new BasicAWSCredentials("dummy", "dummyKey");
                sqsClient = new AmazonSQSClient(credentials, new AmazonSQSConfig { ServiceURL = service });
            }
            else if (!string.IsNullOrEmpty(accessKey) && !string.IsNullOrEmpty(secretKey) && !string.IsNullOrEmpty(sessionToken))
            {
                // AWS SQS instance (auth via explicit credentials)
                var credentials = new SessionAWSCredentials(accessKey, secretKey, sessionToken);
                sqsClient = new AmazonSQSClient(credentials, new AmazonSQSConfig { RegionEndpoint = AWSUtils.GetRegionEndpoint(service) });
            }
            else if (!string.IsNullOrEmpty(accessKey) && !string.IsNullOrEmpty(secretKey))
            {
                // AWS SQS instance (auth via explicit credentials)
                var credentials = new BasicAWSCredentials(accessKey, secretKey);
                sqsClient = new AmazonSQSClient(credentials, new AmazonSQSConfig { RegionEndpoint = AWSUtils.GetRegionEndpoint(service) });
            }
            else
            {
                // AWS SQS instance (implicit auth - EC2 IAM Roles etc)
                sqsClient = new AmazonSQSClient(new AmazonSQSConfig { RegionEndpoint = AWSUtils.GetRegionEndpoint(service) });
            }
        }

        private async Task<string?> GetQueueUrl()
        {
            try
            {
                var response = await sqsClient.GetQueueUrlAsync(QueueName);
                if (!string.IsNullOrWhiteSpace(response.QueueUrl))
                    queueUrl = response.QueueUrl;

                return queueUrl;
            }
            catch (QueueDoesNotExistException)
            {
                return null;
            }
        }

        /// <summary>
        /// Initialize SQSStorage by creating or connecting to an existent queue
        /// </summary>
        /// <returns></returns>
        public async Task InitQueueAsync()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(await GetQueueUrl()))
                {
                    var createQueueRequest = new CreateQueueRequest(QueueName)
                    {
                        Attributes = [],
                    };

                    if (sqsOptions.FifoQueue)
                    {
                        // The stream must have these attributes to be a valid FIFO queue.
                        createQueueRequest.Attributes.Add(QueueAttributeName.FifoQueue, "true");
                        createQueueRequest.Attributes.Add(QueueAttributeName.FifoThroughputLimit, "perMessageGroupId");
                        createQueueRequest.Attributes.Add(QueueAttributeName.DeduplicationScope, "messageGroup");
                        createQueueRequest.Attributes.Add(QueueAttributeName.ContentBasedDeduplication, "true");

                    }

                    if (sqsOptions.ReceiveWaitTimeSeconds.HasValue)
                    {
                        createQueueRequest.Attributes.Add(QueueAttributeName.ReceiveMessageWaitTimeSeconds,
                            sqsOptions.ReceiveWaitTimeSeconds.Value.ToString());
                    }

                    if (sqsOptions.VisibilityTimeoutSeconds.HasValue)
                    {
                        createQueueRequest.Attributes.Add(QueueAttributeName.VisibilityTimeout,
                            sqsOptions.VisibilityTimeoutSeconds.Value.ToString());
                    }

                    var response = await sqsClient.CreateQueueAsync(createQueueRequest);
                    queueUrl = response.QueueUrl;
                }
            }
            catch (Exception exc)
            {
                ReportErrorAndRethrow(exc, "InitQueueAsync");
            }
        }

        /// <summary>
        /// Delete the queue
        /// </summary>
        /// <returns></returns>
        public async Task DeleteQueue()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(queueUrl))
                    throw new InvalidOperationException("Queue not initialized");
                await sqsClient.DeleteQueueAsync(queueUrl);
            }
            catch (Exception exc)
            {
                ReportErrorAndRethrow(exc, "DeleteQueue");
            }
        }

        /// <summary>
        /// Add a message to the SQS queue
        /// </summary>
        /// <param name="message">Message request</param>
        /// <returns></returns>
        public async Task AddMessage(SendMessageRequest message)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(queueUrl))
                    throw new InvalidOperationException("Queue not initialized");

                message.QueueUrl = queueUrl;
                var response = await sqsClient.SendMessageAsync(message);
                if (response.HttpStatusCode != HttpStatusCode.OK)
                {
                    throw new InvalidOperationException(
                        $"Amazon SQS returned HTTP status {response.HttpStatusCode} when sending a message to queue {QueueName}.");
                }
            }
            catch (Exception exc)
            {
                ReportErrorAndRethrow(exc, "AddMessage");
            }
        }

        /// <summary>
        /// Get Messages from SQS Queue.
        /// </summary>
        /// <param name="count">The number of messages to peak. Min 1 and max 10</param>
        /// <returns>Collection with messages from the queue</returns>
        public async Task<IEnumerable<SQSMessage>> GetMessages(int count = 1)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(queueUrl))
                    throw new InvalidOperationException("Queue not initialized");

                if (count < 1)
                    throw new ArgumentOutOfRangeException(nameof(count));


                var request = new ReceiveMessageRequest
                {
                    QueueUrl = queueUrl,
                    MaxNumberOfMessages = count <= MAX_NUMBER_OF_MESSAGE_TO_PEEK ? count : MAX_NUMBER_OF_MESSAGE_TO_PEEK,
                    MessageSystemAttributeNames = receiveMessageSystemAttributes,
                    MessageAttributeNames = receiveMessageAttributes,
                };

                if (sqsOptions.ReceiveWaitTimeSeconds.HasValue)
                    request.WaitTimeSeconds = sqsOptions.ReceiveWaitTimeSeconds.Value;

                var response = await sqsClient.ReceiveMessageAsync(request);
                return response.Messages ?? [];
            }
            catch (Exception exc)
            {
                ReportErrorAndRethrow(exc, "GetMessages");
            }
            throw new InvalidOperationException("Unable to retrieve messages from the queue.");
        }

        /// <summary>
        /// Delete a message from SQS queue
        /// </summary>
        /// <param name="message">The message to be deleted</param>
        /// <returns></returns>
        public async Task DeleteMessage(SQSMessage message)
        {
            try
            {
                if (message == null)
                    throw new ArgumentNullException(nameof(message));

                if (string.IsNullOrWhiteSpace(message.ReceiptHandle))
                    throw new ArgumentException("The message must have a receipt handle.", nameof(message));

                if (string.IsNullOrWhiteSpace(queueUrl))
                    throw new InvalidOperationException("Queue not initialized");

                await sqsClient.DeleteMessageAsync(
                    new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = message.ReceiptHandle });
            }
            catch (Exception exc)
            {
                ReportErrorAndRethrow(exc, "DeleteMessage");
            }
        }

        public async Task DeleteMessages(IEnumerable<SQSMessage> messages)
        {
            try
            {
                ArgumentNullException.ThrowIfNull(messages);
                if (string.IsNullOrWhiteSpace(queueUrl))
                {
                    throw new InvalidOperationException("Queue not initialized");
                }

                var messagesToDelete = messages.ToArray();
                if (messagesToDelete.Length == 0)
                {
                    return;
                }

                foreach (var message in messagesToDelete)
                {
                    ValidateMessageReceipt(message);
                }

                foreach (var batch in messagesToDelete.Chunk(MAX_NUMBER_OF_MESSAGE_TO_PEEK))
                {
                    var deleteRequest = new DeleteMessageBatchRequest
                    {
                        QueueUrl = queueUrl,
                        Entries = batch
                            .Select((m, i) =>
                                new DeleteMessageBatchRequestEntry(i.ToString(), m.ReceiptHandle))
                            .ToList()
                    };

                    var result = await sqsClient.DeleteMessageBatchAsync(deleteRequest);
                    var failedEntries = result.Failed ?? [];
                    foreach (var failed in failedEntries)
                    {
                        Logger.LogWarning("Failed to delete message {MessageId} from SQS queue {QueueName}. Error code: {ErrorCode}. Error message: {ErrorMessage}",
                                failed.Id, QueueName, failed.Code, failed.Message);
                    }

                    if (failedEntries.Count > 0)
                    {
                        throw new InvalidOperationException($"Amazon SQS failed to delete {failedEntries.Count} message(s) from queue {QueueName}.");
                    }
                }
            }
            catch (Exception exc)
            {
                ReportErrorAndRethrow(exc, "DeleteMessages");
            }
        }

        public async Task ReleaseMessages(IEnumerable<SQSMessage> messages)
        {
            try
            {
                ArgumentNullException.ThrowIfNull(messages);
                if (string.IsNullOrWhiteSpace(queueUrl))
                {
                    throw new InvalidOperationException("Queue not initialized");
                }

                var messagesToRelease = messages.ToArray();
                if (messagesToRelease.Length == 0)
                {
                    return;
                }

                foreach (var message in messagesToRelease)
                {
                    ValidateMessageReceipt(message);
                }

                var failedEntryCount = 0;
                foreach (var batch in messagesToRelease.Chunk(MAX_NUMBER_OF_MESSAGE_TO_PEEK))
                {
                    var releaseRequest = new ChangeMessageVisibilityBatchRequest
                    {
                        QueueUrl = queueUrl,
                        Entries = batch
                            .Select((m, i) => new ChangeMessageVisibilityBatchRequestEntry
                            {
                                Id = i.ToString(CultureInfo.InvariantCulture),
                                ReceiptHandle = m.ReceiptHandle,
                                VisibilityTimeout = 0,
                            })
                            .ToList()
                    };

                    var result = await sqsClient.ChangeMessageVisibilityBatchAsync(releaseRequest);
                    var failedEntries = result.Failed ?? [];
                    failedEntryCount += failedEntries.Count;
                    foreach (var failed in failedEntries)
                    {
                        Logger.LogWarning(
                            "Failed to release SQS batch entry {EntryId} from queue {QueueName}. Error code: {ErrorCode}. Error message: {ErrorMessage}",
                            failed.Id,
                            QueueName,
                            failed.Code,
                            failed.Message);
                    }
                }

                if (failedEntryCount > 0)
                {
                    throw new InvalidOperationException($"Amazon SQS failed to release {failedEntryCount} message(s) from queue {QueueName}.");
                }
            }
            catch (Exception exc)
            {
                ReportErrorAndRethrow(exc, "ReleaseMessages");
            }
        }

        private static void ValidateMessageReceipt(SQSMessage message)
        {
            if (message == null)
                throw new ArgumentNullException(nameof(message));

            if (string.IsNullOrWhiteSpace(message.ReceiptHandle))
                throw new ArgumentException("The message must have a receipt handle.", nameof(message));
        }

        private void ReportErrorAndRethrow(Exception exc, string operation)
        {
            LogErrorSQSOperation(exc, operation, QueueName);
            throw new AggregateException($"Error doing {operation} for SQS queue {QueueName}", exc);
        }

        private static string ConstructQueueName(string queueName, SqsOptions sqsOptions, string serviceId)
        {
            var queueNameBuilder = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(serviceId))
            {
                queueNameBuilder.Append(serviceId);
                queueNameBuilder.Append('-');
            }

            queueNameBuilder.Append(queueName);
            if (sqsOptions.FifoQueue)
            {
                queueNameBuilder.Append(".fifo");
            }

            return queueNameBuilder.ToString();
        }

        [LoggerMessage(
            EventId = (int)ErrorCode.StreamProviderManagerBase,
            Level = LogLevel.Error,
            Message = "Error doing {Operation} for SQS queue {QueueName}"
        )]
        private partial void LogErrorSQSOperation(Exception exception, string operation, string queueName);
    }
}
