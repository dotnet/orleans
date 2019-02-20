using Orleans.Configuration;
using Orleans.ServiceBus.Providers;
using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Options;

namespace Orleans.Streams
{
    public static class SiloEventHubStreamBuilderExtensions
    {
        public static SiloEventHubStreamConfigurator UseEventHubCheckpointer(this SiloEventHubStreamConfigurator builder, Action<OptionsBuilder<AzureTableStreamCheckpointerOptions>> configureOptions)
        {
            return builder.ConfigureCheckpointer<AzureTableStreamCheckpointerOptions>(EventHubCheckpointerFactory.CreateFactory, configureOptions);
        }
    }
}
