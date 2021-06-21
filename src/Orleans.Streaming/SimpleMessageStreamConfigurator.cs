using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Providers.Streams.SimpleMessageStream;

namespace Orleans.Hosting
{
    public interface ISimpleMessageStreamConfigurator : INamedServiceConfigurator { }

    public class SimpleMessageStreamConfigurator : NamedServiceConfigurator, ISimpleMessageStreamConfigurator
    {
        public SimpleMessageStreamConfigurator(string name, Action<Action<IServiceCollection>> configureDelegate, IClientBuilder builder)
            : base(name, configureDelegate)
        {
            builder.AddStreaming();
            this.ConfigureComponent(SimpleMessageStreamProvider.Create);
        }

        public SimpleMessageStreamConfigurator(string name, Action<Action<IServiceCollection>> configureDelegate, ISiloBuilder builder) : base(name, configureDelegate)
        {
            builder.AddStreaming();
            this.ConfigureComponent(SimpleMessageStreamProvider.Create);
        }
    }
}
