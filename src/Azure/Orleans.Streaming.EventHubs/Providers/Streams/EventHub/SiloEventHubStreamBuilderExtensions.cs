using Orleans.Configuration;
using Orleans.ServiceBus.Providers;
using System;
using System.Collections.Generic;
using System.Text;

namespace Orleans.Streams
{
    public static class SiloEventHubStreamBuilderExtensions
    {
        public static SiloEventHubStreamConfigurator UseEventHubCheckpointer(this SiloEventHubStreamConfigurator builder, Action<OptionsBuilder<EventHubCheckpointerOptions>> configureOptions)
        {
            return builder.ConfigureCheckpointer<EventHubCheckpointerOptions>(configureOptions, (s,n) => new EventHubCheckpointerFactory(n , s));
        }
    }
}
