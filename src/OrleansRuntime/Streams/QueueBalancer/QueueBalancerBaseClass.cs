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
    public abstract class QueueBalancerBaseClass : IStreamQueueBalancer
    {
        protected readonly List<IStreamQueueBalanceListener> queueBalanceListeners;
        public QueueBalancerBaseClass()
        {
            this.queueBalanceListeners = new List<IStreamQueueBalanceListener>();
        }
        public abstract IEnumerable<QueueId> GetMyQueues();
        public abstract Task Initialize(string strProviderName, IStreamQueueMapper queueMapper, TimeSpan siloMaturityPeriod);
        public virtual bool SubscribeToQueueDistributionChangeEvents(IStreamQueueBalanceListener observer)
        {
            if (observer == null)
            {
                throw new ArgumentNullException("observer");
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
        public virtual bool UnSubscribeFromQueueDistributionChangeEvents(IStreamQueueBalanceListener observer)
        {
            if (observer == null)
            {
                throw new ArgumentNullException("observer");
            }
            lock (queueBalanceListeners)
            {
                return queueBalanceListeners.Contains(observer) && queueBalanceListeners.Remove(observer);
            }
        }


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
