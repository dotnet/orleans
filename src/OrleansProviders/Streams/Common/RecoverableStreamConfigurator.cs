using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;
using System;
using System.Collections.Generic;
using System.Text;

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
        public SiloRecoverableStreamConfigurator(string name, ISiloHostBuilder builder, Func<IServiceProvider, string, IQueueAdapterFactory> adapterFactory)
            : base(name, builder, adapterFactory)
        {
            this.siloBuilder.ConfigureServices(services => services.ConfigureNamedOptionForLogging<StreamStatisticOptions>(name)
            .ConfigureNamedOptionForLogging<StreamCacheEvictionOptions>(name));
        }
    }
}
