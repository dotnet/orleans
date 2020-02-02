using System;
using Microsoft.AspNetCore.Connections;

namespace Orleans.Configuration
{
    public class ClientConnectionOptions
    {
        private readonly ConnectionBuilderDelegates delegates = new ConnectionBuilderDelegates();

        public void ConfigureConnection(Action<IConnectionBuilder> configure) => this.delegates.Add(configure);

        internal void ConfigureConnectionBuilder(IConnectionBuilder builder) => this.delegates.Invoke(builder);
    }
}
