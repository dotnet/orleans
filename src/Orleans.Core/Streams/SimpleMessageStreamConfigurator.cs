using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Providers.Streams.SimpleMessageStream;

namespace Orleans.Hosting
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
