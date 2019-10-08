namespace Orleans.Configuration
{
    /// <summary>
    /// Simple Azure queue stream provider options.
    /// </summary>
    public class SimpleAzureQueueStreamOptions
    {
        [RedactConnectionString]
        public string ConnectionString { get; set; }

        public string QueueName { get; set; }
    }
}
