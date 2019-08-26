using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Providers.Streams.SimpleMessageStream;

namespace Orleans.Streams
{
    public interface ISimpleMessageStreamConfigurator : INamedServiceConfigurator { }

    public class SimpleMessageStreamConfigurator : NamedServiceConfigurator, ISimpleMessageStreamConfigurator
    {
        public SimpleMessageStreamConfigurator(string name, Action<Action<IServiceCollection>> configureDelegate)
            : base(name, configureDelegate)
        {
            this.ConfigureComponent(SimpleMessageStreamProvider.Create);
        }
    }
}
