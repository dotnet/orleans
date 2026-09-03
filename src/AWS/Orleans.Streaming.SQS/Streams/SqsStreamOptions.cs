using System.Collections.Generic;

namespace Orleans.Configuration
{
    /// <summary>
    /// Configures an Amazon SQS-backed persistent stream provider.
    /// </summary>
    public class SqsOptions
    {
        /// <summary>
        /// Specifies the connection string to use for connecting to SQS.
        /// </summary>
        /// <example>
        /// Example for AWS: Service=eu-west-1;AccessKey=XXXXXX;SecretKey=XXXXXX;SessionToken=XXXXXX;
        /// </example>
        /// <example>
        /// Example for LocalStack: Service=http://localhost:4566
        /// </example>
        [Redact]
        public string ConnectionString { get; set; } = null!;

        /// <summary>
        /// Specifies which Amazon SQS system attributes are retrieved with each message.
        /// </summary>
        public List<string> ReceiveMessageSystemAttributes { get; set; } = [];

        /// <summary>
        /// Specifies which application-defined message attributes are retrieved with each message.
        /// </summary>
        public List<string> ReceiveMessageAttributes { get; set; } = [];

        /// <summary>
        /// The optional duration to long-poll for new SQS messages.
        /// </summary>
        public int? ReceiveWaitTimeSeconds { get; set; }

        /// <summary>
        /// The visibility timeout begins when Amazon SQS returns a message.
        /// During this time, the consumer processes and deletes the message.
        /// However, if the consumer fails before deleting the message and your system doesn't call the DeleteMessage action for that message before the visibility timeout expires,
        /// the message becomes visible to other consumers and the message is received again.
        /// If a message must be received only once, your consumer should delete it within the duration of the visibility timeout.
        /// </summary>
        public int? VisibilityTimeoutSeconds { get; set; }

        /// <summary>
        /// Configures the provider to use Amazon SQS FIFO queues and preserve ordering per stream.
        /// </summary>
        public bool FifoQueue { get; set; }
    }
}
