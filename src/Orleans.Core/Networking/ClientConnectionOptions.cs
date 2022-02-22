using System;
using Microsoft.AspNetCore.Connections;

namespace Orleans.Configuration
{
    /// <summary>
    /// Options for clients connections.
    /// </summary>
    public class ClientConnectionOptions
    {
        private readonly ConnectionBuilderDelegates delegates = new ConnectionBuilderDelegates();

        /// <summary>
        /// Adds a connection configuration delegate.
        /// </summary>
        /// <param name="configure">The configuration delegate.</param>
        public void ConfigureConnection(Action<IConnectionBuilder> configure) => this.delegates.Add(configure);

        /// <summary>
        /// Configures the provided connection builder using these options.
        /// </summary>
        /// <param name="builder">The connection builder.</param>
        internal void ConfigureConnectionBuilder(IConnectionBuilder builder) => this.delegates.Invoke(builder);
    }
}
