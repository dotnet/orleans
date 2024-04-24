using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Hosting;

public class ClusterClientAdoNetStreamConfigurator : ClusterClientPersistentStreamConfigurator
{
    public ClusterClientAdoNetStreamConfigurator(string name, IClientBuilder clientBuilder, Func<IServiceProvider, string, IQueueAdapterFactory> adapterFactory) : base(name, clientBuilder, adapterFactory)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(clientBuilder);
        ArgumentNullException.ThrowIfNull(adapterFactory);

        clientBuilder.ConfigureServices(services =>
        {
            services
                .ConfigureNamedOptionForLogging<AdoNetStreamOptions>(name)
                .ConfigureNamedOptionForLogging<HashRingStreamQueueMapperOptions>(name);
        });
    }

    public ClusterClientAdoNetStreamConfigurator ConfigureAdoNet(Action<OptionsBuilder<AdoNetStreamOptions>> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(configureOptions);

        this.Configure(configureOptions);

        return this;
    }

    public ClusterClientAdoNetStreamConfigurator ConfigurePartitioning(int partitions = HashRingStreamQueueMapperOptions.DEFAULT_NUM_QUEUES)
    {
        this.Configure<HashRingStreamQueueMapperOptions>(ob => ob.Configure(options => options.TotalQueueCount = partitions));

        return this;
    }
}