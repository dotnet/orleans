using Amazon.Runtime;
using Amazon.SQS;
using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Amazon.SQS.Model;
using Orleans;
using SQSMessage = Amazon.SQS.Model.Message;

namespace OrleansAWSUtils.Storage
{
    /// <summary>
    /// Wrapper/Helper class around AWS SQS queue service
    /// </summary>
    public class SQSStorage
    {
        /// <summary>
        /// Maximum number of messages allowed by SQS to peak per request
        /// </summary>
        public const int MAX_NUMBER_OF_MESSAGE_TO_PEAK = 10;
        private const string AccessKeyPropertyName = "AccessKey";
        private const string SecretKeyPropertyName = "SecretKey";
        private const string ServicePropertyName = "Service";
        private Logger Logger;
        private string accessKey;
        private string secretKey;
        private string service;
        private string queueUrl;
        private AmazonSQSClient sqsClient;

        /// <summary>
        /// The queue Name
        /// </summary>
        public string QueueName { get; private set; }

        /// <summary>
        /// Default Ctor
        /// </summary>
        /// <param name="queueName">The name of the queue</param>
        /// <param name="connectionString">The connection string</param>
        /// <param name="deploymentId">The cluster deployment Id</param>
        public SQSStorage(string queueName, string connectionString, string deploymentId = "")
        {
            QueueName = string.IsNullOrWhiteSpace(deploymentId) ? queueName : $"{deploymentId}-{queueName}";
            ParseDataConnectionString(connectionString);
            Logger = LogManager.GetLogger($"SQSStorage", LoggerType.Runtime);
            CreateClient();
        }

        #region Queue Management Operations

        private void ParseDataConnectionString(string dataConnectionString)
        {
            var parameters = dataConnectionString.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);

            var serviceConfig = parameters.Where(p => p.Contains(ServicePropertyName)).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(serviceConfig))
            {
                var value = serviceConfig.Split(new[] { '=' }, StringSplitOptions.RemoveEmptyEntries);
                if (value.Length == 2 && !string.IsNullOrWhiteSpace(value[1]))
                    service = value[1];
            }

            var secretKeyConfig = parameters.Where(p => p.Contains(SecretKeyPropertyName)).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(secretKeyConfig))
            {
                var value = secretKeyConfig.Split(new[] { '=' }, StringSplitOptions.RemoveEmptyEntries);
                if (value.Length == 2 && !string.IsNullOrWhiteSpace(value[1]))
                    secretKey = value[1];
            }

            var accessKeyConfig = parameters.Where(p => p.Contains(AccessKeyPropertyName)).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(accessKeyConfig))
            {
                var value = accessKeyConfig.Split(new[] { '=' }, StringSplitOptions.RemoveEmptyEntries);
                if (value.Length == 2 && !string.IsNullOrWhiteSpace(value[1]))
                    accessKey = value[1];
            }
        }

        private void CreateClient()
        {
            var credentials = new BasicAWSCredentials(accessKey, secretKey);
            sqsClient = new AmazonSQSClient(credentials, new AmazonSQSConfig { RegionEndpoint = AWSUtils.GetRegionEndpoint(service) });
        }

        private async Task<string> GetQueueUrl()
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
                    var response = await sqsClient.CreateQueueAsync(QueueName);
                    queueUrl = response.QueueUrl;
                }
            }
            catch (Exception exc)
            {
                ReportErrorAndRethrow(exc, "InitQueueAsync", ErrorCode.StreamProviderManagerBase);
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
                ReportErrorAndRethrow(exc, "DeleteQueue", ErrorCode.StreamProviderManagerBase);
            }
        }

        #endregion

        #region Messaging

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
                await sqsClient.SendMessageAsync(message);
            }
            catch (Exception exc)
            {
                ReportErrorAndRethrow(exc, "AddMessage", ErrorCode.StreamProviderManagerBase);
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

                var request = new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = count <= MAX_NUMBER_OF_MESSAGE_TO_PEAK ? count : MAX_NUMBER_OF_MESSAGE_TO_PEAK };
                var response = await sqsClient.ReceiveMessageAsync(request);
                return response.Messages;
            }
            catch (Exception exc)
            {
                ReportErrorAndRethrow(exc, "GetMessages", ErrorCode.StreamProviderManagerBase);
            }
            return null;
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
                    throw new ArgumentNullException(nameof(message.ReceiptHandle));

                if (string.IsNullOrWhiteSpace(queueUrl))
                    throw new InvalidOperationException("Queue not initialized");

                await sqsClient.DeleteMessageAsync(
                    new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = message.ReceiptHandle });
            }
            catch (Exception exc)
            {
                ReportErrorAndRethrow(exc, "GetMessages", ErrorCode.StreamProviderManagerBase);
            }
        }

        #endregion

        private void ReportErrorAndRethrow(Exception exc, string operation, ErrorCode errorCode)
        {
            var errMsg = String.Format(
                "Error doing {0} for SQS queue {1} " + Environment.NewLine
                + "Exception = {2}", operation, QueueName, exc);
            Logger.Error(errorCode, errMsg, exc);
            throw new AggregateException(errMsg, exc);
        }
    }
}
