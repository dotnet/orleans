using System;
using Microsoft.AspNetCore.Connections;

namespace Orleans.Configuration
{
    public class SiloConnectionOptions : SiloConnectionOptions.ISiloConnectionBuilderOptions
    {
        private readonly ConnectionBuilderDelegates siloOutboundDelegates = new ConnectionBuilderDelegates();
        private readonly ConnectionBuilderDelegates siloInboundDelegates = new ConnectionBuilderDelegates();
        private readonly ConnectionBuilderDelegates gatewayInboundDelegates = new ConnectionBuilderDelegates();

        public void ConfigureSiloOutboundConnection(Action<IConnectionBuilder> configure) => this.siloOutboundDelegates.Add(configure);

        public void ConfigureSiloInboundConnection(Action<IConnectionBuilder> configure) => this.siloInboundDelegates.Add(configure);

        public void ConfigureGatewayInboundConnection(Action<IConnectionBuilder> configure) => this.gatewayInboundDelegates.Add(configure);

        void ISiloConnectionBuilderOptions.ConfigureSiloOutboundBuilder(IConnectionBuilder builder) => this.siloOutboundDelegates.Invoke(builder);

        void ISiloConnectionBuilderOptions.ConfigureSiloInboundBuilder(IConnectionBuilder builder) => this.siloInboundDelegates.Invoke(builder);

        void ISiloConnectionBuilderOptions.ConfigureGatewayInboundBuilder(IConnectionBuilder builder) => this.gatewayInboundDelegates.Invoke(builder);

        public interface ISiloConnectionBuilderOptions
        {
            public void ConfigureSiloOutboundBuilder(IConnectionBuilder builder);
            public void ConfigureSiloInboundBuilder(IConnectionBuilder builder);
            public void ConfigureGatewayInboundBuilder(IConnectionBuilder builder);
        }
    }
}