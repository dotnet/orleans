using System;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;

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
        /// <param name="queueMapper">queue mapper of requesting stream provider</param>
        /// <param name="siloMaturityPeriod">Maturity Period of a silo for queue rebalancing purposes</param>
        /// <returns>Constructed stream queue balancer</returns>
        public static IStreamQueueBalancer Create(
            StreamQueueBalancerType balancerType,
            string strProviderName,
            ISiloStatusOracle siloStatusOracle,
            ClusterConfiguration clusterConfiguration,
            IStreamProviderRuntime runtime,
            IStreamQueueMapper queueMapper,
            TimeSpan siloMaturityPeriod)
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
            if (runtime == null)
            {
                throw new ArgumentNullException("runtime");
            }
            if (queueMapper == null)
            {
                throw new ArgumentNullException("queueMapper");
            }
            bool isFixed;
            switch (balancerType)
            {
                case StreamQueueBalancerType.ConsistentRingBalancer:
                {
                    // Consider: for now re-use the same ConsistentRingProvider with 1 equally devided range. Remove later.
                    IConsistentRingProviderForGrains ringProvider = runtime.GetConsistentRingProvider(0, 1);
                    return new ConsistentRingQueueBalancer(ringProvider, queueMapper);
                }
                case StreamQueueBalancerType.DynamicAzureDeploymentBalancer:
                case StreamQueueBalancerType.StaticAzureDeploymentBalancer:
                {
                    Logger logger = LogManager.GetLogger(typeof(StreamQueueBalancerFactory).Name, LoggerType.Runtime);
                    var wrapper = AssemblyLoader.LoadAndCreateInstance<IDeploymentConfiguration>(Constants.ORLEANS_AZURE_UTILS_DLL, logger);
                    isFixed = balancerType == StreamQueueBalancerType.StaticAzureDeploymentBalancer;
                    return new DeploymentBasedQueueBalancer(siloStatusOracle, wrapper, queueMapper, siloMaturityPeriod, isFixed);
                }
                case StreamQueueBalancerType.DynamicClusterConfigDeploymentBalancer:
                case StreamQueueBalancerType.StaticClusterConfigDeploymentBalancer:
                {
                    IDeploymentConfiguration deploymentConfiguration = new StaticClusterDeploymentConfiguration(clusterConfiguration);
                    isFixed = balancerType == StreamQueueBalancerType.StaticClusterConfigDeploymentBalancer;
                    return new DeploymentBasedQueueBalancer(siloStatusOracle, deploymentConfiguration, queueMapper, siloMaturityPeriod, isFixed);
                }
                default:
                {
                    string error = string.Format("Unsupported balancerType for stream provider. BalancerType: {0}, StreamProvider: {1}", balancerType, strProviderName);
                    throw new ArgumentOutOfRangeException("balancerType", error);
                }
            }
        }
    }
}
