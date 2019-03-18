using System;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;

namespace Orleans.Streams
{
    public interface ISiloRecoverableStreamConfigurator : ISiloPersistentStreamConfigurator
    {
    }

    public static class SiloRecoverableStreamConfiguratorExtensions
    {
        public static TConfigurator ConfigureStatistics<TConfigurator>(this TConfigurator configurator, Action<OptionsBuilder<StreamStatisticOptions>> configureOptions)
            where TConfigurator : ISiloRecoverableStreamConfigurator
        {
            configurator.Configure<StreamStatisticOptions>(configureOptions);
            return configurator;
        }
        public static TConfigurator ConfigureCacheEviction<TConfigurator>(this TConfigurator configurator, Action<OptionsBuilder<StreamCacheEvictionOptions>> configureOptions)
            where TConfigurator : ISiloRecoverableStreamConfigurator
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
