using System;
using Orleans.Runtime;

namespace Orleans.Streams
{
    internal enum StreamQueueBalancerType
    {
        ConsistenRingBalancer // Stream queue balancer that uses consistent ring provider for load balancing
    }

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
        /// <param name="runtime">stream provider runtime environment to run in</param>
        /// <param name="queueMapper">queue mapper of requesting stream provider</param>
        /// <returns>Constructed stream queue balancer</returns>
        public static IStreamQueueBalancer Create(
            StreamQueueBalancerType balancerType,
            string strProviderName,
            IStreamProviderRuntime runtime,
            IStreamQueueMapper queueMapper)
        {
            if (string.IsNullOrWhiteSpace(strProviderName))
            {
                throw new ArgumentNullException("strProviderName");
            }
            if (runtime == null)
            {
                throw new ArgumentNullException("runtime");
            }
            if (queueMapper == null)
            {
                throw new ArgumentNullException("queueMapper");
            }
            switch (balancerType)
            {
                case StreamQueueBalancerType.ConsistenRingBalancer:
                {
                    // Consider: for now re-use the same ConsistentRingProvider with 1 equally devided range. Remove later.
                    IConsistentRingProviderForGrains ringProvider = runtime.GetConsistentRingProvider(0, 1);
                    return new ConsistentRingQueueBalancer(ringProvider, queueMapper);
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
