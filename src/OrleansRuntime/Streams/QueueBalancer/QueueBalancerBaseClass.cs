using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Streams;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.Streams
{
    /// <summary>
    /// Base class for StreamQueueBalancer
    /// </summary>
    public abstract class QueueBalancerBase : IStreamQueueBalancer
    {
        /// <summary>
        /// A collection for its IStreamQueueBalancerListener 
        /// </summary>
        protected readonly List<IStreamQueueBalanceListener> queueBalanceListeners;
        public QueueBalancerBase()
        {
            this.queueBalanceListeners = new List<IStreamQueueBalanceListener>();
        }
        /// <inheritdoc/>
        public abstract IEnumerable<QueueId> GetMyQueues();
        /// <inheritdoc/>
        public abstract Task Initialize(string strProviderName, IStreamQueueMapper queueMapper, TimeSpan siloMaturityPeriod, IProviderConfiguration providerConfig);
        /// <inheritdoc/>
        public virtual bool SubscribeToQueueDistributionChangeEvents(IStreamQueueBalanceListener observer)
        {
            if (observer == null)
            {
                throw new ArgumentNullException(nameof(observer));
            }
            lock (queueBalanceListeners)
            {
                if (queueBalanceListeners.Contains(observer))
                {
                    return false;
                }
                queueBalanceListeners.Add(observer);
                return true;
            }
        }
        /// <inheritdoc/>
        public virtual bool UnSubscribeFromQueueDistributionChangeEvents(IStreamQueueBalanceListener observer)
        {
            if (observer == null)
            {
                throw new ArgumentNullException(nameof(observer));
            }
            lock (queueBalanceListeners)
            {
                return queueBalanceListeners.Remove(observer);
            }
        }
    }

    internal class QueueBalancerUtilities
    {
        /// <summary>
        /// This method is used in DeploymentBasedQueueBalancer and LeaseBasedQueueBalancer in the same manner. 
        /// Basically, whenever there's a SiloStatusNotification, balancer calls siloStatusOracle asking for active silo information. 
        /// If there's new silo which was not recorded in immatureSilos, then balancer will first mark it as immature and then wait siloMaturityPeriod to mark it as mature. 
        /// If GetActiveSilos is called before it is marked as mature, but siloStatusOracle report those new silo as active, we still think they are immature.
        /// It is a conservative way to deal with inconsistent view in a distributed cluster.Since local siloStatusOracle thinks this silo is active 
        /// doesn't mean other silo thinks it is active too. So we'd better wait for siloMaturityPeriod.
        /// </summary>
        /// <param name="siloStatusOracle"></param>
        /// <param name="immatureSilos"></param>
        /// <returns></returns>
        public static List<string> GetActiveSilos(ISiloStatusOracle siloStatusOracle, ConcurrentDictionary<SiloAddress, bool> immatureSilos)
        {
            var activeSiloNames = new List<string>();
            foreach (var kvp in siloStatusOracle.GetApproximateSiloStatuses(true))
            {
                bool immatureBit;
                if (!(immatureSilos.TryGetValue(kvp.Key, out immatureBit) && immatureBit)) // if not immature now or any more
                {
                    string siloName;
                    if (siloStatusOracle.TryGetSiloName(kvp.Key, out siloName))
                    {
                        activeSiloNames.Add(siloName);
                    }
                }
            }
            return activeSiloNames;
        }
    }
}
