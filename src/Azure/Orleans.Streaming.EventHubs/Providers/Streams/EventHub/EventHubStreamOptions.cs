using Azure;
using Azure.Core;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Streams;
using System;

namespace Orleans.Configuration
{
    /// <summary>
    /// EventHub settings for a specific hub
    /// </summary>
    public class EventHubOptions
    {
        /// <summary>
        /// Gets the delegate used to create connections to Azure Event Hub.
        /// </summary>
        internal CreateConnectionDelegate CreateConnection { get; private set; } = null!;

        /// <summary>
        /// Event Hub consumer group.
        /// </summary>
        internal string ConsumerGroup { get; private set; } = null!;

        /// <summary>
        /// Event Hub name.
        /// </summary>
        internal string EventHubName { get; private set; } = null!;

        internal bool OwnsConnection { get; private set; }

        /// <summary>
        /// Connection options used when creating a connection to an Azure Event Hub.
        /// </summary>
        public EventHubConnectionOptions ConnectionOptions { get; set; } = new EventHubConnectionOptions { TransportType = EventHubsTransportType.AmqpTcp };

        /// <summary>
        /// Gets or sets the options used for buffered event publishing.
        /// </summary>
        /// <remarks>
        /// When this value is <see langword="null"/>, events are published directly and each call results in a separate
        /// Event Hubs send operation. When configured, events are buffered into batches, and a stream publication completes
        /// only after Event Hubs acknowledges the batch containing its event.
        /// The Azure SDK may map a partition key to a different partition for buffered and direct producers. Avoid switching
        /// publishing modes while preserving strict ordering for an active stream.
        /// </remarks>
        public EventHubBufferedProducerClientOptions? BufferedProducerOptions { get; set; }

        /// <summary>
        /// Creates an Azure Event Hub connection.
        /// </summary>
        /// <param name="connectionOptions">The connection options.</param>
        /// <returns>An Azure Event Hub connection.</returns>
        public delegate EventHubConnection CreateConnectionDelegate(EventHubConnectionOptions connectionOptions);

        /// <summary>
        /// Configures the Azure Event Hub connection using the provided connection string.
        /// </summary>
        /// <param name="connectionString">The Event Hub connection string.</param>
        /// <param name="eventHubName">The Event Hub name.</param>
        /// <param name="consumerGroup">The consumer group name.</param>
        public void ConfigureEventHubConnection(string connectionString, string eventHubName, string consumerGroup)
        {
            EventHubName = eventHubName;
            ConsumerGroup = consumerGroup;

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ArgumentException("A non-null, non-empty value must be provided.", nameof(connectionString));
            }

            ValidateValues(eventHubName, consumerGroup);

            OwnsConnection = true;
            CreateConnection = connectionOptions => new EventHubConnection(connectionString, EventHubName, connectionOptions);
        }

        /// <summary>
        /// Configures the Azure Event Hub connection using the provided fully-qualified namespace string and credential.
        /// </summary>
        /// <param name="fullyQualifiedNamespace">The fully qualified Event Hubs namespace.</param>
        /// <param name="eventHubName">The Event Hub name.</param>
        /// <param name="consumerGroup">The consumer group name.</param>
        /// <param name="credential">The named key credential.</param>
        public void ConfigureEventHubConnection(string fullyQualifiedNamespace, string eventHubName, string consumerGroup, AzureNamedKeyCredential credential)
        {
            EventHubName = eventHubName;
            ConsumerGroup = consumerGroup;

            if (string.IsNullOrWhiteSpace(fullyQualifiedNamespace))
            {
                throw new ArgumentException("A non-null, non-empty value must be provided.", nameof(fullyQualifiedNamespace));
            }

            ValidateValues(eventHubName, consumerGroup);

            if (credential is null)
            {
                throw new ArgumentNullException(nameof(credential));
            }

            OwnsConnection = true;
            CreateConnection = connectionOptions => new EventHubConnection(fullyQualifiedNamespace, EventHubName, credential, connectionOptions);
        }

        /// <summary>
        /// Configures the Azure Event Hub connection using the provided fully-qualified namespace string and credential.
        /// </summary>
        /// <param name="fullyQualifiedNamespace">The fully qualified Event Hubs namespace.</param>
        /// <param name="eventHubName">The Event Hub name.</param>
        /// <param name="consumerGroup">The consumer group name.</param>
        /// <param name="credential">The shared access signature credential.</param>
        public void ConfigureEventHubConnection(string fullyQualifiedNamespace, string eventHubName, string consumerGroup, AzureSasCredential credential)
        {
            EventHubName = eventHubName;
            ConsumerGroup = consumerGroup;

            if (string.IsNullOrWhiteSpace(fullyQualifiedNamespace))
            {
                throw new ArgumentException("A non-null, non-empty value must be provided.", nameof(fullyQualifiedNamespace));
            }

            ValidateValues(eventHubName, consumerGroup);

            if (credential is null)
            {
                throw new ArgumentNullException(nameof(credential));
            }

            OwnsConnection = true;
            CreateConnection = connectionOptions => new EventHubConnection(fullyQualifiedNamespace, EventHubName, credential, connectionOptions);
        }

        /// <summary>
        /// Configures the Azure Event Hub connection using the provided fully-qualified namespace string and credential.
        /// </summary>
        /// <param name="fullyQualifiedNamespace">The fully qualified Event Hubs namespace.</param>
        /// <param name="eventHubName">The Event Hub name.</param>
        /// <param name="consumerGroup">The consumer group name.</param>
        /// <param name="credential">The token credential.</param>
        public void ConfigureEventHubConnection(string fullyQualifiedNamespace, string eventHubName, string consumerGroup, TokenCredential credential)
        {
            EventHubName = eventHubName;
            ConsumerGroup = consumerGroup;
            if (string.IsNullOrWhiteSpace(fullyQualifiedNamespace))
            {
                throw new ArgumentException("A non-null, non-empty value must be provided.", nameof(fullyQualifiedNamespace));
            }

            ValidateValues(eventHubName, consumerGroup);
            if (credential is null)
            {
                throw new ArgumentNullException(nameof(credential));
            }
            OwnsConnection = true;
            CreateConnection = connectionOptions => new EventHubConnection(fullyQualifiedNamespace, EventHubName, credential, connectionOptions);
        }

        /// <summary>
        /// Configures the Azure Event Hub connection using the provided connection instance.
        /// </summary>
        /// <param name="connection">The Event Hub connection.</param>
        /// <param name="consumerGroup">The consumer group name.</param>
        public void ConfigureEventHubConnection(EventHubConnection connection, string consumerGroup)
        {
            if (connection is null) throw new ArgumentNullException(nameof(connection));
            EventHubName = connection.EventHubName;
            ConsumerGroup = consumerGroup;
            ValidateValues(connection.EventHubName, consumerGroup);
            OwnsConnection = false;
            CreateConnection = _ => connection;
        }

        /// <summary>
        /// Configures the Azure Event Hub connection using the provided delegate.
        /// </summary>
        /// <param name="createConnection">The delegate used to create Event Hub connections.</param>
        /// <param name="eventHubName">The Event Hub name.</param>
        /// <param name="consumerGroup">The consumer group name.</param>
        public void ConfigureEventHubConnection(CreateConnectionDelegate createConnection, string eventHubName, string consumerGroup)
        {
            EventHubName = eventHubName;
            ConsumerGroup = consumerGroup;
            ValidateValues(eventHubName, consumerGroup);
            OwnsConnection = true;
            CreateConnection = createConnection ?? throw new ArgumentNullException(nameof(createConnection));
        }

        private static void ValidateValues(string eventHubName, string consumerGroup)
        {
            if (string.IsNullOrWhiteSpace(eventHubName))
            {
                throw new ArgumentException("A non-null, non-empty value must be provided.", nameof(eventHubName));
            }

            if (string.IsNullOrWhiteSpace(consumerGroup))
            {
                throw new ArgumentException("A non-null, non-empty value must be provided.", nameof(consumerGroup));
            }
        }
    }

    /// <summary>
    /// Validates <see cref="EventHubOptions"/>.
    /// </summary>
    public class EventHubOptionsValidator : IConfigurationValidator
    {
        private readonly EventHubOptions options;
        private readonly string name;
        /// <summary>
        /// Initializes a new instance of the <see cref="EventHubOptionsValidator"/> class.
        /// </summary>
        /// <param name="options">The options to validate.</param>
        /// <param name="name">The stream provider name.</param>
        public EventHubOptionsValidator(EventHubOptions options, string name)
        {
            this.options = options;
            this.name = name;
        }
        /// <inheritdoc />
        public void ValidateConfiguration()
        {
            if (options.CreateConnection is null)
            {
                throw new OrleansConfigurationException($"Azure Event Hub connection not configured for stream provider options {nameof(EventHubOptions)} with name \"{name}\". Use the {options.GetType().Name}.{nameof(EventHubOptions.ConfigureEventHubConnection)} method to configure the connection.");
            }

            if (string.IsNullOrEmpty(options.ConsumerGroup))
            {
                throw new OrleansConfigurationException($"{nameof(EventHubOptions)} on stream provider {this.name} is invalid. {nameof(EventHubOptions.ConsumerGroup)} is invalid");
            }

            if (string.IsNullOrEmpty(options.EventHubName))
            {
                throw new OrleansConfigurationException($"{nameof(EventHubOptions)} on stream provider {this.name} is invalid. {nameof(EventHubOptions.EventHubName)} is invalid");
            }
        }
    }

    /// <summary>
    /// Validates that a stream queue checkpointer is configured for an Event Hubs stream provider.
    /// </summary>
    public class StreamCheckpointerConfigurationValidator : IConfigurationValidator
    {
        private readonly IServiceProvider services;
        private readonly string name;
        /// <summary>
        /// Initializes a new instance of the <see cref="StreamCheckpointerConfigurationValidator"/> class.
        /// </summary>
        /// <param name="services">The service provider.</param>
        /// <param name="name">The stream provider name.</param>
        public StreamCheckpointerConfigurationValidator(IServiceProvider services, string name)
        {
            this.services = services;
            this.name = name;
        }
        /// <inheritdoc />
        public void ValidateConfiguration()
        {
            var checkpointerFactory = services.GetKeyedService<IStreamQueueCheckpointerFactory>(this.name);
            if (checkpointerFactory == null)
                throw new OrleansConfigurationException($"No IStreamQueueCheckpointer is configured with PersistentStreamProvider {this.name}. Please configure one.");
        }
    }

    /// <summary>
    /// Configures how an Event Hubs stream provider receives events from each partition.
    /// </summary>
    public class EventHubReceiverOptions
    {
        /// <summary>
        /// Optional parameter that configures the receiver prefetch count.
        /// </summary>
        public int? PrefetchCount { get; set; }
        /// <summary>
        /// In cases where no checkpoint is found, this indicates if service should read from the most recent data, or from the beginning of a partition.
        /// </summary>
        public bool StartFromNow { get; set; } = DEFAULT_START_FROM_NOW;
        private const bool DEFAULT_START_FROM_NOW = true;
    }

    /// <summary>
    /// Configures cache pressure monitoring for an Event Hubs stream provider.
    /// </summary>
    public class EventHubStreamCachePressureOptions
    {
        /// <summary>
        /// SlowConsumingPressureMonitorConfig
        /// </summary>
        public double? SlowConsumingMonitorFlowControlThreshold { get; set; }

        /// <summary>
        /// SlowConsumingMonitorPressureWindowSize
        /// </summary>
        public TimeSpan? SlowConsumingMonitorPressureWindowSize { get; set; }

        /// <summary>
        /// AveragingCachePressureMonitorFlowControlThreshold, AveragingCachePressureMonitor is turn on by default. 
        /// User can turn it off by setting this value to null
        /// </summary>
        public double? AveragingCachePressureMonitorFlowControlThreshold { get; set; } = DEFAULT_AVERAGING_CACHE_PRESSURE_MONITORING_THRESHOLD;
        internal const double DEFAULT_AVERAGING_CACHE_PRESSURE_MONITORING_THRESHOLD = 1.0 / 3.0;
    }
}
