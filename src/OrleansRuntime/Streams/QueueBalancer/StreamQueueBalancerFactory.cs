using System;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Streams
{
    /// <summary>
    /// Stream queue balancer factory
    /// </summary>
    internal class StreamQueueBalancerFactory
    {
        /// <summary>
        /// Create stream queue balancer by type requested
        /// </summary>
        /// <param name="balancerType">queue balancer type to create</param>
        /// <param name="strProviderName">name of requesting stream provider</param>
        /// <param name="siloStatusOracle">membership services interface.</param>
        /// <param name="clusterConfiguration">cluster configuration</param>
        /// <param name="runtime">stream provider runtime environment to run in</param>
        /// <returns>Constructed stream queue balancer</returns>
        public static IStreamQueueBalancer Create(
            Type balancerType,
            string strProviderName,
            ISiloStatusOracle siloStatusOracle,
            ClusterConfiguration clusterConfiguration,
            IStreamProviderRuntime runtime)
        {
            if (string.IsNullOrWhiteSpace(strProviderName))
            {
                throw new ArgumentNullException("strProviderName");
            }
            if (siloStatusOracle == null)
            {
                throw new ArgumentNullException("siloStatusOracle");
            }
            if (clusterConfiguration == null)
            {
                throw new ArgumentNullException("clusterConfiguration");
            }
            if (balancerType == null || balancerType == StreamQueueBalancerType.ConsistentRingBalancer)
            {
                // Consider: for now re-use the same ConsistentRingProvider with 1 equally devided range. Remove later.
                IConsistentRingProviderForGrains ringProvider = runtime.GetConsistentRingProvider(0, 1);
                return new ConsistentRingQueueBalancer(ringProvider);
            }
            else if (balancerType == StreamQueueBalancerType.DynamicAzureDeploymentBalancer)
            {
                Logger logger = LogManager.GetLogger(typeof(StreamQueueBalancerFactory).Name, LoggerType.Runtime);
                var wrapper = AssemblyLoader.LoadAndCreateInstance<IDeploymentConfiguration>(Constants.ORLEANS_AZURE_UTILS_DLL, logger, runtime.ServiceProvider);
                return new DynamicAzureDeploymentBalancer(siloStatusOracle, wrapper);
            }
            else if (balancerType == StreamQueueBalancerType.StaticAzureDeploymentBalancer)
            {
                Logger logger = LogManager.GetLogger(typeof(StreamQueueBalancerFactory).Name, LoggerType.Runtime);
                var wrapper = AssemblyLoader.LoadAndCreateInstance<IDeploymentConfiguration>(Constants.ORLEANS_AZURE_UTILS_DLL, logger, runtime.ServiceProvider);
                return new StaticAzureDeploymentBalancer(siloStatusOracle, wrapper);
            }
            else if (balancerType == StreamQueueBalancerType.DynamicClusterConfigDeploymentBalancer)
            {
                IDeploymentConfiguration deploymentConfiguration = new StaticClusterDeploymentConfiguration(clusterConfiguration);
                return new DynamicClusterConfigDeploymentBalancer(siloStatusOracle, deploymentConfiguration);
            }
            else if (balancerType == StreamQueueBalancerType.StaticClusterConfigDeploymentBalancer)
            {
                IDeploymentConfiguration deploymentConfiguration = new StaticClusterDeploymentConfiguration(clusterConfiguration);
                return new StaticClusterConfigDeploymentBalancer(siloStatusOracle, deploymentConfiguration);
            }
            else
            {
                var serviceProvider = runtime.ServiceProvider;
                try
                {
                    var balancer = (IStreamQueueBalancer)serviceProvider.GetRequiredService(balancerType);
                    return balancer;
                }
                catch (Exception)
                {
                    string error = $"Unsupported balancerType for stream provider. BalancerType: {balancerType}, StreamProvider: {strProviderName}";
                    throw new ArgumentOutOfRangeException("balancerType", error);
                }
            }
        }
    }
}
