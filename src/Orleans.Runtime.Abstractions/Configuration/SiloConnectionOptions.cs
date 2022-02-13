using System;
using Microsoft.AspNetCore.Connections;

namespace Orleans.Configuration
{
    /// <summary>
    /// Options for configuring silo networking.
    /// Implements the <see cref="Orleans.Configuration.SiloConnectionOptions.ISiloConnectionBuilderOptions" />
    /// </summary>
    /// <seealso cref="Orleans.Configuration.SiloConnectionOptions.ISiloConnectionBuilderOptions" />
    public class SiloConnectionOptions : SiloConnectionOptions.ISiloConnectionBuilderOptions
    {
        private readonly ConnectionBuilderDelegates siloOutboundDelegates = new ConnectionBuilderDelegates();
        private readonly ConnectionBuilderDelegates siloInboundDelegates = new ConnectionBuilderDelegates();
        private readonly ConnectionBuilderDelegates gatewayInboundDelegates = new ConnectionBuilderDelegates();

        /// <summary>
        /// Configures silo outbound connections.
        /// </summary>
        /// <param name="configure">The configuration delegate.</param>
        public void ConfigureSiloOutboundConnection(Action<IConnectionBuilder> configure) => this.siloOutboundDelegates.Add(configure);

        /// <summary>
        /// Configures silo inbound connections from other silos.
        /// </summary>
        /// <param name="configure">The configuration delegate.</param>
        public void ConfigureSiloInboundConnection(Action<IConnectionBuilder> configure) => this.siloInboundDelegates.Add(configure);

        /// <summary>
        /// Configures silo inbound connections from clients.
        /// </summary>
        /// <param name="configure">The configuration delegate.</param>
        public void ConfigureGatewayInboundConnection(Action<IConnectionBuilder> configure) => this.gatewayInboundDelegates.Add(configure);

        /// <inheritdoc/>
        void ISiloConnectionBuilderOptions.ConfigureSiloOutboundBuilder(IConnectionBuilder builder) => this.siloOutboundDelegates.Invoke(builder);

        /// <inheritdoc/>
        void ISiloConnectionBuilderOptions.ConfigureSiloInboundBuilder(IConnectionBuilder builder) => this.siloInboundDelegates.Invoke(builder);

        /// <inheritdoc/>
        void ISiloConnectionBuilderOptions.ConfigureGatewayInboundBuilder(IConnectionBuilder builder) => this.gatewayInboundDelegates.Invoke(builder);

        /// <summary>
        /// Options for silo networking.
        /// </summary>
        public interface ISiloConnectionBuilderOptions
        {
            /// <summary>
            /// Configures the silo outbound connection builder.
            /// </summary>
            /// <param name="builder">The builder.</param>
            public void ConfigureSiloOutboundBuilder(IConnectionBuilder builder);

            /// <summary>
            /// Configures the silo inbound connection builder.
            /// </summary>
            /// <param name="builder">The builder.</param>
            public void ConfigureSiloInboundBuilder(IConnectionBuilder builder);

            /// <summary>
            /// Configures the silo gateway connection builder.
            /// </summary>
            /// <param name="builder">The builder.</param>
            public void ConfigureGatewayInboundBuilder(IConnectionBuilder builder);
        }
    }
}