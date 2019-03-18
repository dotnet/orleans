using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Providers.Streams.SimpleMessageStream;

namespace Orleans.Streams
{
    public interface ISimpleMessageStreamConfigurator : IComponentConfigurator<ISimpleMessageStreamConfigurator> { }

    public class SimpleMessageStreamConfigurator : NamedServiceConfigurator<ISimpleMessageStreamConfigurator>, ISimpleMessageStreamConfigurator
    {
        public SimpleMessageStreamConfigurator(string name, Action<Action<IServiceCollection>> configureDelegate)
            : base(name, configureDelegate)
        {
            ConfigureComponent<IStreamProvider>(SimpleMessageStreamProvider.Create);
        }
    }
}
