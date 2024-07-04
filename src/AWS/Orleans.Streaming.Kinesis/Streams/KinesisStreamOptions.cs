namespace Orleans.Streaming.Kinesis
{
    public class KinesisStreamOptions
    {
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
        /// Kinesis region name, such as "us-west-2", or a URL for the development endpoint.
        /// </summary>
        public string Service { get; set; }

        /// <summary>
        /// Name of the Kinesis Stream.
        /// </summary>
        public string StreamName { get; set; }
    }
}
