﻿/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

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
                    TraceLogger logger = TraceLogger.GetLogger(typeof(StreamQueueBalancerFactory).Name, TraceLogger.LoggerType.Runtime);
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
