using System;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Streams;

namespace Orleans.Hosting
{
    public interface ISiloRecoverableStreamConfigurator : ISiloPersistentStreamConfigurator {}

    public static class SiloRecoverableStreamConfiguratorExtensions
    {
        public static void ConfigureStatistics(this ISiloRecoverableStreamConfigurator configurator, Action<OptionsBuilder<StreamStatisticOptions>> configureOptions)
        {
            configurator.Configure(configureOptions);
        }
        public static void ConfigureCacheEviction(this ISiloRecoverableStreamConfigurator configurator, Action<OptionsBuilder<StreamCacheEvictionOptions>> configureOptions)
        {
            configurator.Configure(configureOptions);
        }
    }

    public class SiloRecoverableStreamConfigurator : SiloPersistentStreamConfigurator, ISiloRecoverableStreamConfigurator
    {
        public SiloRecoverableStreamConfigurator(string name, Action<Action<IServiceCollection>> configureDelegate, Func<IServiceProvider, string, IQueueAdapterFactory> adapterFactory)
            : base(name, configureDelegate, adapterFactory)
        {
            this.ConfigureDelegate(services => services
                .ConfigureNamedOptionForLogging<StreamStatisticOptions>(name)
                .ConfigureNamedOptionForLogging<StreamCacheEvictionOptions>(name));
        }
    }
}
