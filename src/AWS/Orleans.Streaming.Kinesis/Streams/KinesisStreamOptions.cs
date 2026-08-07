using System;
using Orleans.Runtime;

namespace Orleans.Streaming.Kinesis
{
    /// <summary>
    /// Configures the Kinesis persistent stream provider.
    /// </summary>
    public class KinesisStreamOptions
    {
        /// <summary>
        /// Connection string for AWS Kinesis. Format: "Service;AccessKey;SecretKey;Region" or "Service" for default credentials.
        /// </summary>
        [Redact]
        public string? ConnectionString
        {
            get
            {
                if (!string.IsNullOrEmpty(Service) && !string.IsNullOrEmpty(AccessKey) && !string.IsNullOrEmpty(SecretKey))
                {
                    return $"{Service};{AccessKey};{SecretKey};{Region ?? "us-east-1"}";
                }
                return Service;
            }
            set
            {
                if (string.IsNullOrEmpty(value))
                {
                    Service = null;
                    AccessKey = null;
                    SecretKey = null;
                    Region = null;
                    return;
                }

                var parts = value.Split(';');
                if (parts.Length == 1)
                {
                    Service = parts[0];
                }
                else if (parts.Length >= 4)
                {
                    Service = parts[0];
                    AccessKey = parts[1];
                    SecretKey = parts[2];
                    Region = parts[3];
                }
                else
                {
                    throw new ArgumentException("Invalid connection string format. Expected 'Service' or 'Service;AccessKey;SecretKey;Region'.", nameof(value));
                }
            }
        }

        /// <summary>
        /// Optional Access Key string for Kinesis.
        /// </summary>
        [Redact]
        public string? AccessKey { get; set; }

        /// <summary>
        /// Optional Secret key for Kinesis.
        /// </summary>
        [Redact]
        public string? SecretKey { get; set; }

        /// <summary>
        /// Kinesis service endpoint URL, such as "https://kinesis.us-west-2.amazonaws.com" or a URL for the development endpoint.
        /// </summary>
        public string? Service { get; set; }

        /// <summary>
        /// AWS Region name, such as "us-west-2".
        /// </summary>
        public string? Region { get; set; }

        /// <summary>
        /// Name of the Kinesis Stream.
        /// </summary>
        public string StreamName { get; set; } = "OrleansTestStream";

        /// <summary>
        /// Gets or sets the minimum interval between calls to Kinesis <c>GetRecords</c> for a shard.
        /// </summary>
        /// <remarks>
        /// Kinesis allows at most five <c>GetRecords</c> transactions per second for each open shard.
        /// </remarks>
        public TimeSpan GetRecordsInterval { get; set; } = TimeSpan.FromMilliseconds(200);

        /// <summary>
        /// Gets or sets the interval between checks for shard topology changes.
        /// </summary>
        /// <remarks>
        /// Live resharding is not supported. When topology changes, receivers stop and require the provider to be restarted.
        /// </remarks>
        public TimeSpan TopologyCheckInterval { get; set; } = TimeSpan.FromMinutes(1);
    }

    internal sealed class KinesisStreamOptionsValidator(KinesisStreamOptions options, string name) : IConfigurationValidator
    {
        private static readonly TimeSpan MinimumGetRecordsInterval = TimeSpan.FromMilliseconds(200);

        public void ValidateConfiguration()
        {
            if (string.IsNullOrWhiteSpace(options.StreamName))
            {
                throw new OrleansConfigurationException(
                    $"The {nameof(KinesisStreamOptions.StreamName)} is required for the Kinesis stream provider '{name}'.");
            }

            if (options.GetRecordsInterval < MinimumGetRecordsInterval)
            {
                throw new OrleansConfigurationException(
                    $"The {nameof(KinesisStreamOptions.GetRecordsInterval)} must be at least {MinimumGetRecordsInterval} for the Kinesis stream provider '{name}'.");
            }

            if (options.TopologyCheckInterval <= TimeSpan.Zero)
            {
                throw new OrleansConfigurationException(
                    $"The {nameof(KinesisStreamOptions.TopologyCheckInterval)} must be greater than zero for the Kinesis stream provider '{name}'.");
            }

            if (string.IsNullOrEmpty(options.AccessKey) != string.IsNullOrEmpty(options.SecretKey))
            {
                throw new OrleansConfigurationException(
                    $"The {nameof(KinesisStreamOptions.AccessKey)} and {nameof(KinesisStreamOptions.SecretKey)} must either both be configured or both be omitted for the Kinesis stream provider '{name}'.");
            }
        }
    }
}
