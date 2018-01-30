﻿using Microsoft.Extensions.Configuration;

namespace Orleans.TestingHost
{
    /// <summary>
    /// Allows implementations to configure the client builder when starting up each silo in the test cluster.
    /// </summary>
    public interface IClientBuilderConfigurator
    {
        /// <summary>
        /// Configures the client builder
        /// </summary>
        void Configure(IConfiguration configuration, IClientBuilder clientBuilder);
    }
}