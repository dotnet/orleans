using Google.Api.Gax.Grpc;
using Google.Cloud.PubSub.V1;
using Grpc.Core;
using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
namespace Orleans.Providers.GCP.Streams.PubSub
{
    /// <summary>
    /// Utility class to encapsulate access to Google PubSub APIs.
    /// </summary>
    /// <remarks> Used by Google PubSub streaming provider.</remarks>
    public class PubSubDataManager
    {
        public const int MAX_PULLED_MESSAGES = 1000;

        public TopicName TopicName { get; private set; }
        public SubscriptionName SubscriptionName { get; private set; }

        private Subscription _subscription;
        private Topic _topic;
        private PublisherClient _publisher;
        private SubscriberClient _subscriber;
        private TimeSpan? _deadline;
        private ServiceEndpoint _customEndpoint;

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Security", "CA2104:DoNotDeclareReadOnlyMutableReferenceTypes")]
        private readonly ILogger _logger;

        public PubSubDataManager(ILoggerFactory loggerFactory, string projectId, string topicId, string subscriptionId, string serviceId, TimeSpan? deadline = null, string customEndpoint = "")
        {
            if (string.IsNullOrWhiteSpace(serviceId)) throw new ArgumentNullException(nameof(serviceId));
            if (string.IsNullOrWhiteSpace(projectId)) throw new ArgumentNullException(nameof(projectId));
            if (string.IsNullOrWhiteSpace(topicId)) throw new ArgumentNullException(nameof(topicId));
            if (string.IsNullOrWhiteSpace(subscriptionId)) throw new ArgumentNullException(nameof(subscriptionId));

            _logger = loggerFactory.CreateLogger<PubSubDataManager>();
            _deadline = deadline;
            topicId = $"{topicId}-{serviceId}";
            subscriptionId = $"{projectId}-{serviceId}";
            TopicName = new TopicName(projectId, topicId);
            SubscriptionName = new SubscriptionName(projectId, subscriptionId);

            if (!string.IsNullOrWhiteSpace(customEndpoint))
            {
                var hostPort = customEndpoint.Split(new char[] { ':' }, StringSplitOptions.RemoveEmptyEntries);
                if (hostPort.Length != 2) throw new ArgumentException(nameof(customEndpoint));

                var host = hostPort[0];
                int port;
                if (!int.TryParse(hostPort[1], out port)) throw new ArgumentException(nameof(customEndpoint));

                _customEndpoint = new ServiceEndpoint(host, port);
            }
        }

        public async Task Initialize()
        {
            try
            {
                _publisher = await PublisherClient.CreateAsync(_customEndpoint);
            }
            catch (Exception e)
            {
                ReportErrorAndRethrow(e, "CreateAsync", GoogleErrorCode.Initializing);
            }

            bool didCreate = false;

            try
            {
                _topic = await _publisher.CreateTopicAsync(TopicName);
                didCreate = true;
            }
            catch (RpcException e)
            {
                if (e.Status.StatusCode != StatusCode.AlreadyExists)
                    ReportErrorAndRethrow(e, "CreateTopicAsync", GoogleErrorCode.Initializing);

                _topic = await _publisher.GetTopicAsync(TopicName);
            }

            _logger.Info((int)GoogleErrorCode.Initializing, "{0} Google PubSub Topic {1}", (didCreate ? "Created" : "Attached to"), TopicName.TopicId);

            didCreate = false;

            try
            {
                _subscriber = await SubscriberClient.CreateAsync(_customEndpoint);
                _subscription = await _subscriber.CreateSubscriptionAsync(SubscriptionName, TopicName, pushConfig: null,
                    ackDeadlineSeconds: _deadline.HasValue ? (int)_deadline.Value.TotalSeconds : 60);
                didCreate = true;
            }
            catch (RpcException e)
            {
                if (e.Status.StatusCode != StatusCode.AlreadyExists)
                    ReportErrorAndRethrow(e, "CreateSubscriptionAsync", GoogleErrorCode.Initializing);

                _subscription = await _subscriber.GetSubscriptionAsync(SubscriptionName);
            }
            _logger.Info((int)GoogleErrorCode.Initializing, "{0} Google PubSub Subscription {1} to Topic {2}", (didCreate ? "Created" : "Attached to"), SubscriptionName.SubscriptionId, TopicName.TopicId);
        }

        public async Task DeleteTopic()
        {
            if (_logger.IsEnabled(LogLevel.Debug)) _logger.Debug("Deleting Google PubSub topic: {0}", TopicName.TopicId);
            try
            {
                await _publisher?.DeleteTopicAsync(TopicName);
                _logger.Info((int)GoogleErrorCode.Initializing, "Deleted Google PubSub topic {0}", TopicName.TopicId);
            }
            catch (Exception exc)
            {
                ReportErrorAndRethrow(exc, "DeleteTopic", GoogleErrorCode.DeleteTopic);
            }
        }

        public async Task PublishMessages(IEnumerable<PubsubMessage> messages)
        {
            var count = messages.Count();
            if (count < 1) return;

            if (_logger.IsEnabled(LogLevel.Trace)) _logger.Trace("Publishing {0} message to topic {1}", count, TopicName.TopicId);

            try
            {
                await _publisher?.PublishAsync(TopicName, messages);
            }
            catch (Exception exc)
            {
                ReportErrorAndRethrow(exc, "PublishMessage", GoogleErrorCode.PublishMessage);
            }
        }

        public async Task<IEnumerable<ReceivedMessage>> GetMessages(int count = 1)
        {
            if (_logger.IsEnabled(LogLevel.Trace)) _logger.Trace("Getting {0} message(s) from Google PubSub topic {1}", count, TopicName.TopicId);

            PullResponse response = null;
            try
            {
                //According to Google, no more than 1000 messages can be published/received
                response = await _subscriber?.PullAsync(SubscriptionName, true, count < 1 ? MAX_PULLED_MESSAGES : count);
            }
            catch (Exception exc)
            {
                ReportErrorAndRethrow(exc, "GetMessages", GoogleErrorCode.GetMessages);
            }

            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.Trace("Received {0} message(s) from Google PubSub topic {1}", response.ReceivedMessages.Count, TopicName.TopicId);

                foreach (var received in response.ReceivedMessages)
                {
                    _logger.Trace("Received message {0} published {1} from Google PubSub topic {2}", received.Message.MessageId,
                            received.Message.PublishTime.ToDateTime(), TopicName.TopicId);
                }
            }

            return response.ReceivedMessages;
        }

        public async Task AcknowledgeMessages(IEnumerable<ReceivedMessage> messages)
        {
            var count = messages.Count();
            if (count < 1) return;

            if (_logger.IsEnabled(LogLevel.Trace)) _logger.Trace("Deleting {0} message(s) from Google PubSub topic {1}", count, TopicName.TopicId);

            try
            {
                await _subscriber.AcknowledgeAsync(SubscriptionName, messages.Select(m => m.AckId));
            }
            catch (Exception exc)
            {
                ReportErrorAndRethrow(exc, "DeleteMessage", GoogleErrorCode.DeleteMessage);
            }
        }

        private void ReportErrorAndRethrow(Exception exc, string operation, GoogleErrorCode errorCode)
        {
            var errMsg = String.Format(
                "Error doing {0} for Google Project {1} at PubSub Topic {2} " + Environment.NewLine
                + "Exception = {3}", operation, TopicName.ProjectId, TopicName.TopicId, exc);
            _logger.Error((int)errorCode, errMsg, exc);
            throw new AggregateException(errMsg, exc);
        }
    }
}
