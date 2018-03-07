
using Orleans.Streams;
using System;

namespace Orleans.Configuration
{
    /// <summary>
    /// Azure queue stream provider options.
    /// </summary>
    public class AzureQueueOptions
    {
        [RedactConnectionString]
        public string ConnectionString { get; set; }

        //TODO: should this be here ? I guess it should be because it determine the queue name?
        public string ClusterId { get; set; }

        public TimeSpan? MessageVisibilityTimeout { get; set; }   
    }
}
