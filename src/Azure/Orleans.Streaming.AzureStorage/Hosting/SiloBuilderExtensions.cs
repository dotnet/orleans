using System;
using Orleans.Configuration;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Streaming;

namespace Orleans.Hosting
{
    public static class SiloBuilderExtensions
    {
        /// <summary>
        /// Configure silo to use azure queue persistent streams.
        /// </summary>
        public static SiloAzureQueueStreamConfigurator<TDataAdapter> AddAzureQueueStreams<TDataAdapter>(this ISiloHostBuilder builder, string name)
           where TDataAdapter : IAzureQueueDataAdapter
        {
            return new SiloAzureQueueStreamConfigurator<TDataAdapter>(name,builder);
        }
    }
}
