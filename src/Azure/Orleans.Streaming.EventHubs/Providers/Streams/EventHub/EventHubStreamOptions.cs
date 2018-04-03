
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
        /// EventHub connection string.
        /// </summary>
        [Redact]
        public string ConnectionString { get; set; }
        /// <summary>
        /// EventHub consumer group.
        /// </summary>
        public string ConsumerGroup { get; set; }
        /// <summary>
        /// Hub path.
        /// </summary>
        public string Path { get; set; }
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
            if (String.IsNullOrEmpty(options.ConnectionString))
                throw new OrleansConfigurationException($"{nameof(EventHubOptions)} on stream provider {this.name} is invalid. {nameof(EventHubOptions.ConnectionString)} is invalid");
            if (String.IsNullOrEmpty(options.ConsumerGroup))
                throw new OrleansConfigurationException($"{nameof(EventHubOptions)} on stream provider {this.name} is invalid. {nameof(EventHubOptions.ConsumerGroup)} is invalid");
            if (String.IsNullOrEmpty(options.Path))
                throw new OrleansConfigurationException($"{nameof(EventHubOptions)} on stream provider {this.name} is invalid. {nameof(EventHubOptions.Path)} is invalid");
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
            if(checkpointerFactory == null)
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
        /// In cases where no checkpoint is found, this indicates if service should read from the most recent data, or from the begining of a partition.
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