using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Providers.Streams.SimpleMessageStream;
using Orleans.Runtime;

namespace Orleans.Streams
{
    public interface ISimpleMessageStreamConfigurator : IComponentConfigurator<ISimpleMessageStreamConfigurator> { }

    public class SimpleMessageStreamConfigurator : NamedServiceConfigurator<ISimpleMessageStreamConfigurator>, ISimpleMessageStreamConfigurator
    {
        public SimpleMessageStreamConfigurator(string name, Action<Action<IServiceCollection>> configureDelegate)
            : base(name, configureDelegate)
        {
            //wire stream provider into lifecycle 
            this.configureDelegate(AddClusterClientSimpleMessageStreamProvider);
        }

        private void AddClusterClientSimpleMessageStreamProvider(
            IServiceCollection services)
        {
            services
                .AddSingletonNamedService<IStreamProvider>(name, SimpleMessageStreamProvider.Create);
        }
    }
}
