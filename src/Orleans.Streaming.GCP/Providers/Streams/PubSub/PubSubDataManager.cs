using Google.Api.Gax;
using Google.Cloud.PubSub.V1;
using Grpc.Core;
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
        private PublisherServiceApiClient _publisherService;
        private PublisherClient _publisher;
        private SubscriberServiceApiClient _subscriberService;
        private SubscriberServiceApiClient _subscriber;
        private readonly TimeSpan? _deadline;
        private readonly string _customEndpoint;

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Security", "CA2104:DoNotDeclareReadOnlyMutableReferenceTypes")]
        private readonly ILogger _logger;

        public PubSubDataManager(ILoggerFactory loggerFactory, string projectId, string topicId, string subscriptionId, string serviceId, TimeSpan? deadline = null, string customEndpoint = null)
        {
            if (string.IsNullOrWhiteSpace(serviceId)) throw new ArgumentNullException(nameof(serviceId));
            if (string.IsNullOrWhiteSpace(projectId)) throw new ArgumentNullException(nameof(projectId));
            if (string.IsNullOrWhiteSpace(topicId)) throw new ArgumentNullException(nameof(topicId));
            if (string.IsNullOrWhiteSpace(subscriptionId)) throw new ArgumentNullException(nameof(subscriptionId));

            _logger = loggerFactory.CreateLogger<PubSubDataManager>();
            _deadline = deadline;
            _customEndpoint = customEndpoint;
            
            topicId = $"{topicId}-{serviceId}";
            subscriptionId = $"{projectId}-{serviceId}";
            TopicName = new TopicName(projectId, topicId);
            SubscriptionName = new SubscriptionName(projectId, subscriptionId);
        }

        public async Task Initialize()
        {
            try
            {
                _publisherService = await new PublisherServiceApiClientBuilder
                {
                    EmulatorDetection = EmulatorDetection.EmulatorOrProduction,
                    Endpoint = _customEndpoint
                }.BuildAsync();

                _publisher = await new PublisherClientBuilder
                {
                    TopicName = TopicName,
                    EmulatorDetection = EmulatorDetection.EmulatorOrProduction,
                    Endpoint = _customEndpoint,
                }.BuildAsync();
            }
            catch (Exception e)
            {
                ReportErrorAndRethrow(e, "CreateAsync", GoogleErrorCode.Initializing);
            }

            bool didCreate = false;

            try
            {
                _topic = await _publisherService.CreateTopicAsync(TopicName);
                didCreate = true;
            }
            catch (RpcException e)
            {
                if (e.Status.StatusCode != StatusCode.AlreadyExists)
                    ReportErrorAndRethrow(e, "CreateTopicAsync", GoogleErrorCode.Initializing);

                _topic = await _publisherService.GetTopicAsync(TopicName);
            }

            _logger.LogInformation((int)GoogleErrorCode.Initializing, "{Verb} Google PubSub Topic {TopicId}", (didCreate ? "Created" : "Attached to"), TopicName.TopicId);

            didCreate = false;

            try
            {
                _subscriberService = await new SubscriberServiceApiClientBuilder 
                {
                    EmulatorDetection = EmulatorDetection.EmulatorOrProduction,
                    Endpoint = _customEndpoint,
                }.BuildAsync();

                _subscriber = await new SubscriberServiceApiClientBuilder
                {
                    EmulatorDetection = EmulatorDetection.EmulatorOrProduction,
                    Endpoint = _customEndpoint,
                }.BuildAsync();
                
                _subscription = await _subscriberService.CreateSubscriptionAsync(SubscriptionName, TopicName, pushConfig: null,
                    ackDeadlineSeconds: _deadline.HasValue ? (int)_deadline.Value.TotalSeconds : 60);
                didCreate = true;
            }
            catch (RpcException e)
            {
                if (e.Status.StatusCode != StatusCode.AlreadyExists)
                    ReportErrorAndRethrow(e, "CreateSubscriptionAsync", GoogleErrorCode.Initializing);

                _subscription = await _subscriberService.GetSubscriptionAsync(SubscriptionName);
            }

            _logger.LogInformation(
                (int)GoogleErrorCode.Initializing,
                "{Verb} Google PubSub Subscription {SubscriptionId} to Topic {TopicId}",
                (didCreate ? "Created" : "Attached to"),
                SubscriptionName.SubscriptionId,
                TopicName.TopicId);
        }

        public async Task DeleteTopic()
        {
            if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("Deleting Google PubSub topic: {TopicId}", TopicName.TopicId);
            try
            {
                await _publisherService?.DeleteTopicAsync(TopicName);
                _logger.LogInformation((int)GoogleErrorCode.Initializing, "Deleted Google PubSub topic {TopicId}", TopicName.TopicId);
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

            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Publishing {Count} messages to topic {TopicId}", count, TopicName.TopicId);

            try
            {
                foreach(var message in messages)
                {
                    await _publisher?.PublishAsync(message);
                }
            }
            catch (Exception exc)
            {
                ReportErrorAndRethrow(exc, "PublishMessage", GoogleErrorCode.PublishMessage);
            }
        }

        public async Task<IEnumerable<ReceivedMessage>> GetMessages(int count = 1)
        {
            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Getting {Count} message(s) from Google PubSub topic {TopicId}", count, TopicName.TopicId);

            PullResponse response = null;
            try
            {
                //According to Google, no more than 1000 messages can be published/received
                response = await _subscriber.PullAsync(SubscriptionName, count < 1 ? MAX_PULLED_MESSAGES : count);
            }
            catch (Exception exc)
            {
                ReportErrorAndRethrow(exc, "GetMessages", GoogleErrorCode.GetMessages);
            }

            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.LogTrace("Received {Count} message(s) from Google PubSub topic {TopicId}", response.ReceivedMessages.Count, TopicName.TopicId);

                foreach (var received in response.ReceivedMessages)
                {
                    _logger.LogTrace(
                        "Received message {MessageId} published {PublishedTime} from Google PubSub topic {TopicId}",
                        received.Message.MessageId,
                        received.Message.PublishTime.ToDateTime(),
                        TopicName.TopicId);
                }
            }

            return response.ReceivedMessages;
        }

        public async Task AcknowledgeMessages(IEnumerable<ReceivedMessage> messages)
        {
            var count = messages.Count();
            if (count < 1) return;

            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("Deleting {Count} message(s) from Google PubSub topic {TopicId}", count, TopicName.TopicId);

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
            _logger.LogError(
                (int)errorCode,
                exc,
                "Error doing {Operation} for Google Project {ProjectId} at PubSub Topic {TopicId} ",
                operation,
                TopicName.ProjectId,
                TopicName.TopicId);
            throw new AggregateException(
                $"Error doing {operation} for Google Project {TopicName.ProjectId} at PubSub Topic {TopicName.TopicId} {Environment.NewLine}Exception = {exc}",
                exc);
        }
    }
}
