using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Orleans.ApplicationParts;
using Orleans.Configuration;
using Orleans.Providers.Streams.Common;
using Orleans.Streaming.RabbitMQ;
using Orleans.Streaming.RabbitMQ.Providers;

namespace Orleans.Streams
{
    public class RabbitMQStreamConfigurator : SiloRecoverableStreamConfigurator
    {
        public RabbitMQStreamConfigurator(string name,
            Action<Action<IServiceCollection>> configureServicesDelegate,
            Action<Action<IApplicationPartManager>> configureAppPartsDelegate) : base(name, configureServicesDelegate, RabbitMQAdapterFactory.Create)
        {
            configureAppPartsDelegate(parts =>
            {
                parts.AddFrameworkPart(typeof(RabbitMQAdapterFactory).Assembly)
                .AddFrameworkPart(typeof(EventSequenceTokenV2).Assembly);
            });
            this.configureDelegate(services => services.ConfigureNamedOptionForLogging<RabbitMQOptions>(name));
        }
    }
}
