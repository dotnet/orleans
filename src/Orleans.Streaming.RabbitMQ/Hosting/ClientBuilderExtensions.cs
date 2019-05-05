using System;
using Orleans.Streams;

namespace Orleans.Hosting
{
    // todo (mxplusb): eventually add a way to read all the config options from the environment to be cloud native about it.
    public static class ClientBuilderExtensions
    {
        /// <summary>
        /// Configure cluster client to use RabbitMQ persistent streams.
        /// </summary>
        /// <param name="builder"><seealso cref="IClientBuilder"/></param>
        /// <param name="name">RabbitMQ client name, used for logging and internal referencing.</param>
        /// <param name="configure"><seealso cref="ClusterClientRabbitMQStreamConfigurator"/>Options to configure a RabbitMQ client.</param>
        /// <returns></returns>
        public static IClientBuilder AddRabbitMQ(this IClientBuilder builder, string name, Action<ClusterClientRabbitMQStreamConfigurator> configure)
        {
            var configurator = new ClusterClientRabbitMQStreamConfigurator(name, builder);
            configure?.Invoke(configurator);
            return builder;
        }
    }
}
