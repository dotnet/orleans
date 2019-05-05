using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Providers.Streams.Common;
using Orleans.Streaming.RabbitMQ;
using Orleans.Streaming.RabbitMQ.Interfaces;

namespace Orleans.Streams
{
    // todo (mxplusb): tests?
    public class ClusterClientRabbitMQStreamConfigurator : ClusterClientPersistentStreamConfigurator, IClusterClientRabbitMQStreamConfigurator
    {
        public ClusterClientRabbitMQStreamConfigurator(string name, IClientBuilder builder) : base(name, builder, RabbitMQAdapterFactory.Create)
        {
            builder.ConfigureApplicationParts(parts =>
            {
                parts.AddFrameworkPart(typeof(RabbitMQAdapterFactory).Assembly)
                .AddFrameworkPart(typeof(EventSequenceTokenV2).Assembly);
            })
                .ConfigureServices(services => services.ConfigureNamedOptionForLogging<RabbitMQOptions>(name)
                .AddTransient<IConfigurationValidator>(sp => new RabbitMQOptionsValidator(sp.GetOptionsByName<RabbitMQOptions>(name), name)));
        }
    }
}
