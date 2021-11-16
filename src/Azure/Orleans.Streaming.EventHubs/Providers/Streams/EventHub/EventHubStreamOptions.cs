using Azure;
using Azure.Core;
using Azure.Messaging.EventHubs;
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
        internal CreateConnectionDelegate CreateConnection { get; private set; }

        /// <summary>
        /// Event Hub consumer group.
        /// </summary>
        internal string ConsumerGroup { get; private set; }

        /// <summary>
        /// Event Hub name.
        /// </summary>
        internal string EventHubName { get; private set; }

        /// <summary>
        /// Connection options used when creating a connection to an Azure Event Hub.
        /// </summary>
        public EventHubConnectionOptions ConnectionOptions { get; set; } = new EventHubConnectionOptions { TransportType = EventHubsTransportType.AmqpTcp };

        /// <summary>
        /// Creates an Azure Event Hub connection.
        /// </summary>
        /// <param name="connectionOptions">The connection options.</param>
        /// <returns>An Azure Event Hub connection.</returns>
        public delegate EventHubConnection CreateConnectionDelegate(EventHubConnectionOptions connectionOptions);

        /// <summary>
        /// Configures the Azure Event Hub connection using the provided connection string.
        /// </summary>
        public void ConfigureEventHubConnection(string connectionString, string eventHubName, string consumerGroup)
        {
            EventHubName = eventHubName;
            ConsumerGroup = consumerGroup;

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ArgumentException("A non-null, non-empty value must be provided.", nameof(connectionString));
            }

            ValidateValues(eventHubName, consumerGroup);

            CreateConnection = connectionOptions => new EventHubConnection(connectionString, EventHubName, connectionOptions);
        }

        /// <summary>
        /// Configures the Azure Event Hub connection using the provided fully-qualified namespace string and credential.
        /// </summary>
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

            CreateConnection = connectionOptions => new EventHubConnection(fullyQualifiedNamespace, EventHubName, credential, connectionOptions);
        }

        /// <summary>
        /// Configures the Azure Event Hub connection using the provided fully-qualified namespace string and credential.
        /// </summary>
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

            CreateConnection = connectionOptions => new EventHubConnection(fullyQualifiedNamespace, EventHubName, credential, connectionOptions);
        }

        /// <summary>
        /// Configures the Azure Event Hub connection using the provided fully-qualified namespace string and credential.
        /// </summary>
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
            
            CreateConnection = connectionOptions => new EventHubConnection(fullyQualifiedNamespace, EventHubName, credential, connectionOptions);
        }

        /// <summary>
        /// Configures the Azure Event Hub connection using the provided connection instance.
        /// </summary>
        public void ConfigureEventHubConnection(EventHubConnection connection, string consumerGroup)
        {
            EventHubName = connection.EventHubName;
            ConsumerGroup = consumerGroup;
            ValidateValues(connection.EventHubName, consumerGroup);
            if (connection is null) throw new ArgumentNullException(nameof(connection));
            CreateConnection = _ => connection;
        }

        /// <summary>
        /// Configures the Azure Event Hub connection using the provided delegate.
        /// </summary>
        public void ConfigureEventHubConnection(CreateConnectionDelegate createConnection, string eventHubName, string consumerGroup)
        {
            EventHubName = eventHubName;
            ConsumerGroup = consumerGroup;
            ValidateValues(eventHubName, consumerGroup);
            CreateConnection = createConnection ?? throw new ArgumentNullException(nameof(createConnection));
        }

        private void ValidateValues(string eventHubName, string consumerGroup)
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

    public class EventHubOptionsValidator : IConfigurationValidator
    {
        private readonly EventHubOptions options;
        private readonly string name;
        public EventHubOptionsValidator(EventHubOptions options, string name)
        {
            this.options = options;
            this.name = name;
        }
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

    public class StreamCheckpointerConfigurationValidator : IConfigurationValidator
    {
        private readonly IServiceProvider services;
        private string name;
        public StreamCheckpointerConfigurationValidator(IServiceProvider services, string name)
        {
            this.services = services;
            this.name = name;
        }
        public void ValidateConfiguration()
        {
            var checkpointerFactory = services.GetServiceByName<IStreamQueueCheckpointerFactory>(this.name);
            if (checkpointerFactory == null)
                throw new OrleansConfigurationException($"No IStreamQueueCheckpointer is configured with PersistentStreamProvider {this.name}. Please configure one.");
        }
    }

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
        public const bool DEFAULT_START_FROM_NOW = true;
    }

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
        public const double AVERAGING_CACHE_PRESSURE_MONITORING_OFF = 1.0;
        public const double DEFAULT_AVERAGING_CACHE_PRESSURE_MONITORING_THRESHOLD = 1.0 / 3.0;
    }
}