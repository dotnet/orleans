using System;

namespace Orleans.Streaming.Kinesis
{
    public class KinesisStreamOptions
    {
        /// <summary>
        /// Connection string for AWS Kinesis. Format: "Service;AccessKey;SecretKey;Region" or "Service" for default credentials.
        /// </summary>
        public string ConnectionString
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
                    throw new ArgumentException($"Invalid connection string format. Expected 'Service' or 'Service;AccessKey;SecretKey;Region', but got '{value}'");
                }
            }
        }

        /// <summary>
        /// Optional Access Key string for Kinesis.
        /// </summary>
        [Redact]
        public string AccessKey { get; set; }

        /// <summary>
        /// Optional Secret key for Kinesis.
        /// </summary>
        [Redact]
        public string SecretKey { get; set; }

        /// <summary>
        /// Kinesis service endpoint URL, such as "https://kinesis.us-west-2.amazonaws.com" or a URL for the development endpoint.
        /// </summary>
        public string Service { get; set; }

        /// <summary>
        /// AWS Region name, such as "us-west-2".
        /// </summary>
        public string Region { get; set; }

        /// <summary>
        /// Name of the Kinesis Stream.
        /// </summary>
        public string StreamName { get; set; } = "OrleansTestStream";
    }
}
