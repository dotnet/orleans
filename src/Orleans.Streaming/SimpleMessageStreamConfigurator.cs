using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Providers.Streams.SimpleMessageStream;

namespace Orleans.Hosting
{
    /// <summary>
    /// Interface for types which configure Simple Message Streams.
    /// </summary>
    public interface ISimpleMessageStreamConfigurator : INamedServiceConfigurator
    {
    }

    /// <summary>
    /// Configures Simple Message Streams.
    /// </summary>
    public class SimpleMessageStreamConfigurator : NamedServiceConfigurator, ISimpleMessageStreamConfigurator
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="SimpleMessageStreamConfigurator"/> class.
        /// </summary>
        /// <param name="name">The stream provider name.</param>
        /// <param name="configureDelegate">The configuration delegate.</param>
        /// <param name="builder">The builder.</param>
        public SimpleMessageStreamConfigurator(string name, Action<Action<IServiceCollection>> configureDelegate, IClientBuilder builder)
            : base(name, configureDelegate)
        {
            builder.AddStreaming();
            this.ConfigureComponent(SimpleMessageStreamProvider.Create);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SimpleMessageStreamConfigurator"/> class.
        /// </summary>
        /// <param name="name">The stream provider name.</param>
        /// <param name="configureDelegate">The configuration delegate.</param>
        /// <param name="builder">The builder.</param>
        public SimpleMessageStreamConfigurator(string name, Action<Action<IServiceCollection>> configureDelegate, ISiloBuilder builder) : base(name, configureDelegate)
        {
            builder.AddStreaming();
            this.ConfigureComponent(SimpleMessageStreamProvider.Create);
        }
    }
}
