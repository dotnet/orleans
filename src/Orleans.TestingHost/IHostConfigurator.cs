﻿using Microsoft.Extensions.Hosting;

namespace Orleans.TestingHost
{
    /// <summary>
    /// Allows implementations to configure the host builder when starting up each silo in the test cluster.
    /// </summary>
    public interface IHostConfigurator
    {
        /// <summary>
        /// Configures the host builder.
        /// </summary>
        void Configure(IHostBuilder hostBuilder);
    }
}