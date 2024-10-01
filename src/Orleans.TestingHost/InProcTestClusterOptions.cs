using System;
using System.Collections.Generic;
using Microsoft.Extensions.Hosting;
using Orleans.Configuration;
using Orleans.Hosting;

namespace Orleans.TestingHost;

/// <summary>
/// Configuration options for test clusters.
/// </summary>
public sealed class InProcessTestClusterOptions
{
    /// <summary>
    /// Gets or sets the cluster identifier.
    /// </summary>
    /// <seealso cref="ClusterOptions.ClusterId"/>
    /// <value>The cluster identifier.</value>
    public string ClusterId { get; set; }

    /// <summary>
    /// Gets or sets the service identifier.
    /// </summary>
    /// <seealso cref="ClusterOptions.ServiceId"/>
    /// <value>The service identifier.</value>
    public string ServiceId { get; set; }

    /// <summary>
    /// Gets or sets the base silo port, which is the port for the first silo. Other silos will use subsequent ports.
    /// </summary>
    /// <value>The base silo port.</value>
    internal int BaseSiloPort { get; set; }

    /// <summary>
    /// Gets or sets the base gateway port, which is the gateway port for the first silo. Other silos will use subsequent ports.
    /// </summary>
    /// <value>The base gateway port.</value>
    internal int BaseGatewayPort { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to use test cluster membership.
    /// </summary>
    /// <value><see langword="true" /> if test cluster membership should be used; otherwise, <see langword="false" />.</value>
    internal bool UseTestClusterMembership { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to use the real environment statistics.
    /// </summary>
    public bool UseRealEnvironmentStatistics { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to initialize the client immediately on deployment.
    /// </summary>
    /// <value><see langword="true" /> if the client should be initialized immediately on deployment; otherwise, <see langword="false" />.</value>
    public bool InitializeClientOnDeploy { get; set; }

    /// <summary>
    /// Gets or sets the initial silos count.
    /// </summary>
    /// <value>The initial silos count.</value>
    public short InitialSilosCount { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to configure file logging.
    /// </summary>
    /// <value><see langword="true" /> if file logging should be configured; otherwise, <see langword="false" />.</value>
    public bool ConfigureFileLogging { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to assume homogeneous silos for testing purposes.
    /// </summary>
    /// <value><see langword="true" /> if the cluster should assume homogeneous silos; otherwise, <see langword="false" />.</value>
    public bool AssumeHomogenousSilosForTesting { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether each silo should host a gateway.
    /// </summary>
    /// <value><see langword="true" /> if each silo should host a gateway; otherwise, <see langword="false" />.</value>
    public bool GatewayPerSilo { get; set; } = true;

    /// <summary>
    /// Gets the silo host configuration delegates.
    /// </summary>
    /// <value>The silo host configuration delegates.</value>
    public List<Action<InProcessTestSiloSpecificOptions, IHostApplicationBuilder>> SiloHostConfigurationDelegates { get; } = [];

    /// <summary>
    /// Gets the client host configuration delegates.
    /// </summary>
    /// <value>The client host configuration delegates.</value>
    public List<Action<IHostApplicationBuilder>> ClientHostConfigurationDelegates { get; } = [];
}
