
using System.Collections.Generic;

namespace Orleans.Configuration
{
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
        public string ConnectionString { get; set; }

        /// <summary>
        /// Specifies which SQS Attributes should be retrieved about the SQS message from the Queue.
        /// </summary>
        public List<string> ReceiveAttributes { get; set; } = new();

        /// <summary>
        /// Specifies which Message Attributes should be retrieved with the SQS messages.
        /// </summary>
        public List<string> ReceiveMessageAttributes { get; set; } = new();

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

        public bool FifoQueue { get; set; }
    }
}
