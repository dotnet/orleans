using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using System;
using Microsoft.Extensions.Options;

namespace Orleans.Streams
{
    public interface ISiloRecoverableStreamConfigurator : ISiloPersistentStreamConfigurator
    {
    }

    public static class SiloRecoverableStreamConfiguratorExtensions
    {
        public static ISiloRecoverableStreamConfigurator ConfigureStatistics(this ISiloRecoverableStreamConfigurator configurator, Action<OptionsBuilder<StreamStatisticOptions>> configureOptions)
        {
            configurator.Configure<StreamStatisticOptions>(configureOptions);
            return configurator;
        }
        public static ISiloRecoverableStreamConfigurator ConfigureCacheEviction(this ISiloRecoverableStreamConfigurator configurator, Action<OptionsBuilder<StreamCacheEvictionOptions>> configureOptions)
        {
            configurator.Configure<StreamCacheEvictionOptions>(configureOptions);
            return configurator;
        }
    }

    public class SiloRecoverableStreamConfigurator : SiloPersistentStreamConfigurator, ISiloRecoverableStreamConfigurator
    {
        public SiloRecoverableStreamConfigurator(string name, Action<Action<IServiceCollection>> configureDelegate, Func<IServiceProvider, string, IQueueAdapterFactory> adapterFactory)
            : base(name, configureDelegate, adapterFactory)
        {
            this.configureDelegate(services => services.ConfigureNamedOptionForLogging<StreamStatisticOptions>(name)
            .ConfigureNamedOptionForLogging<StreamCacheEvictionOptions>(name));
        }
    }
}
