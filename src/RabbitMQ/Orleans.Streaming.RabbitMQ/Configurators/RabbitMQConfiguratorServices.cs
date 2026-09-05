using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streaming.RabbitMQ.Adapters;
using Orleans.Streaming.RabbitMQ.RabbitMQ;

namespace Orleans.Streaming.RabbitMQ.Configurators;

internal static class RabbitMQConfiguratorServices
{
    public static void Configure(IServiceCollection services, string providerName)
    {
        services
            .AddKeyedSingleton<RabbitMQStreamSystemProvider>(providerName, (sp, _) =>
                new RabbitMQStreamSystemProvider(
                    sp.GetOptionsByName<RabbitMQClientOptions>(providerName),
                    sp.GetRequiredService<ILogger<RabbitMQStreamSystemProvider>>()))
            .AddKeyedSingleton<RabbitMQQueueProvider>(providerName, (sp, _) =>
                new RabbitMQQueueProvider(
                    sp.GetRequiredKeyedService<RabbitMQStreamSystemProvider>(providerName),
                    providerName,
                    sp.GetOptionsByName<RabbitMQClientOptions>(providerName)))
            .AddKeyedSingleton<RabbitMQAdapterReceiverFactory>(providerName, (sp, _) =>
                new RabbitMQAdapterReceiverFactory(
                    sp.GetRequiredService<ILoggerFactory>(),
                    sp.GetRequiredService<Serializer>(),
                    sp.GetOptionsByName<RabbitMQClientOptions>(providerName),
                    sp.GetRequiredService<OrleansInstruments>()))
            .ConfigureFormatterResolver<RabbitMQClientOptions, RabbitMQClientOptionsFormatterResolver>()
            .ConfigureNamedOptionForLogging<RabbitMQClientOptions>(providerName)
            .ConfigureNamedOptionForLogging<HashRingStreamQueueMapperOptions>(providerName);
    }
}
