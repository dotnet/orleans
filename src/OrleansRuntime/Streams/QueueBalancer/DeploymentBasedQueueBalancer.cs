using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.Streams
{
    /// <summary>
    /// DeploymentBasedQueueBalancer is a stream queue balancer that uses deployment information to
    /// help balance queue distribution.
    /// DeploymentBasedQueueBalancer uses the deployment configuration to determine how many silos
    /// to expect and uses a silo status oracle to determine which of the silos are available.  With
    /// this information it tries to balance the queues using a best fit resource balancing algorithm.
    /// </summary>
    internal class DeploymentBasedQueueBalancer : ISiloStatusListener, IStreamQueueBalancer
    {
        private readonly TimeSpan siloMaturityPeriod;
        private readonly ISiloStatusOracle siloStatusOracle;
        private readonly IDeploymentConfiguration deploymentConfig;
        private readonly ReadOnlyCollection<QueueId> allQueues;
        private readonly List<IStreamQueueBalanceListener> queueBalanceListeners;
        private readonly ConcurrentDictionary<SiloAddress, bool> immatureSilos;
        private readonly bool isFixed;
        private bool isStarting;

        
        public DeploymentBasedQueueBalancer(
            ISiloStatusOracle siloStatusOracle,
            IDeploymentConfiguration deploymentConfig,
            IStreamQueueMapper queueMapper,
            TimeSpan maturityPeriod,
            bool isFixed)
        {
            if (siloStatusOracle == null)
            {
                throw new ArgumentNullException("siloStatusOracle");
            }
            if (deploymentConfig == null)
            {
                throw new ArgumentNullException("deploymentConfig");
            }
            if (queueMapper == null)
            {
                throw new ArgumentNullException("queueMapper");
            }

            this.siloStatusOracle = siloStatusOracle;
            this.deploymentConfig = deploymentConfig;
            allQueues = new ReadOnlyCollection<QueueId>(queueMapper.GetAllQueues().ToList());
            queueBalanceListeners = new List<IStreamQueueBalanceListener>();
            immatureSilos = new ConcurrentDictionary<SiloAddress, bool>();
            this.isFixed = isFixed;
            siloMaturityPeriod = maturityPeriod;
            isStarting = true;

            // register for notification of changes to silo status for any silo in the cluster
            this.siloStatusOracle.SubscribeToSiloStatusEvents(this);

            // record all already active silos as already mature. 
            // Even if they are not yet, they will be mature by the time I mature myself (after I become !isStarting).
            foreach (var silo in siloStatusOracle.GetApproximateSiloStatuses(true).Keys.Where(s => !s.Equals(siloStatusOracle.SiloAddress)))
            {
                immatureSilos[silo] = false;     // record as mature
            }

            NotifyAfterStart().Ignore();
        }

        private async Task NotifyAfterStart()
        {
            await Task.Delay(siloMaturityPeriod);
            isStarting = false;
            await NotifyListeners();
        }

        /// <summary>
        /// Called when the status of a silo in the cluster changes.
        /// - Notify listeners
        /// </summary>
        /// <param name="updatedSilo">Silo which status has changed</param>
        /// <param name="status">new silo status</param>
        public void SiloStatusChangeNotification(SiloAddress updatedSilo, SiloStatus status)
        {
            if (status == SiloStatus.Dead)
            {
                // just clean up garbage from immatureSilos.
                bool ignore;
                immatureSilos.TryRemove(updatedSilo, out ignore);
            }
            SiloStatusChangeNotification().Ignore();
        }

        private async Task SiloStatusChangeNotification()
        {
            List<Task> tasks = new List<Task>();
            // look at all currently active silos not including myself
            foreach (var silo in siloStatusOracle.GetApproximateSiloStatuses(true).Keys.Where(s => !s.Equals(siloStatusOracle.SiloAddress)))
            {
                bool ignore;
                if (!immatureSilos.TryGetValue(silo, out ignore))
                {
                    tasks.Add(RecordImmatureSilo(silo));
                }
            }
            if (!isStarting)
            {
                // notify, uncoditionaly, and deal with changes in GetMyQueues()
                NotifyListeners().Ignore();
            }
            if (tasks.Count > 0)
            {
                await Task.WhenAll(tasks);
                await NotifyListeners(); // notify, uncoditionaly, and deal with changes it in GetMyQueues()
            }
        }

        private async Task RecordImmatureSilo(SiloAddress updatedSilo)
        {
            immatureSilos[updatedSilo] = true;      // record as immature
            await Task.Delay(siloMaturityPeriod);
            immatureSilos[updatedSilo] = false;     // record as mature
        }

        public IEnumerable<QueueId> GetMyQueues()
        {
            BestFitBalancer<string, QueueId> balancer = GetBalancer();
            bool useIdealDistribution = isFixed || isStarting;
            Dictionary<string, List<QueueId>> distribution = useIdealDistribution
                ? balancer.IdealDistribution
                : balancer.GetDistribution(GetActiveSilos(siloStatusOracle, immatureSilos));

            List<QueueId> myQueues;
            if (distribution.TryGetValue(siloStatusOracle.SiloName, out myQueues))
            {
                if (!useIdealDistribution)
                {
                    HashSet<QueueId> queuesOfImmatureSilos = GetQueuesOfImmatureSilos(siloStatusOracle, immatureSilos, balancer.IdealDistribution);
                    // filter queues that belong to immature silos
                    myQueues.RemoveAll(queue => queuesOfImmatureSilos.Contains(queue));
                }
                return myQueues;
            }
            return Enumerable.Empty<QueueId>();
        }

        public bool SubscribeToQueueDistributionChangeEvents(IStreamQueueBalanceListener observer)
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

        public bool UnSubscribeToQueueDistributionChangeEvents(IStreamQueueBalanceListener observer)
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
        
        /// <summary>
        /// Checks to see if deployment configuration has changed, by adding or removing silos.
        /// If so, it updates the list of all silo names and creates a new resource balancer.
        /// This should occure rarely.
        /// </summary>
        private BestFitBalancer<string, QueueId> GetBalancer()
        {
            var allSiloNames = deploymentConfig.GetAllSiloNames();
            // rebuild balancer with new list of instance names
            return new BestFitBalancer<string, QueueId>(allSiloNames, allQueues);
        }

        private static List<string> GetActiveSilos(ISiloStatusOracle siloStatusOracle, ConcurrentDictionary<SiloAddress, bool> immatureSilos)
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

        private static HashSet<QueueId> GetQueuesOfImmatureSilos(ISiloStatusOracle siloStatusOracle, 
            ConcurrentDictionary<SiloAddress, bool> immatureSilos, 
            Dictionary<string, List<QueueId>> idealDistribution)
        {
            HashSet<QueueId> queuesOfImmatureSilos = new HashSet<QueueId>();
            foreach (var silo in immatureSilos.Where(s => s.Value)) // take only those from immature set that have their immature status bit set
            {
                string siloName;
                if (siloStatusOracle.TryGetSiloName(silo.Key, out siloName))
                {
                    List<QueueId> queues;
                    if (idealDistribution.TryGetValue(siloName, out queues))
                    {
                        queuesOfImmatureSilos.UnionWith(queues);
                    }
                }
            }
            return queuesOfImmatureSilos;
        }

        private Task NotifyListeners()
        {
            List<IStreamQueueBalanceListener> queueBalanceListenersCopy;
            lock (queueBalanceListeners)
            {
                queueBalanceListenersCopy = queueBalanceListeners.ToList(); // make copy
            }
            var notificatioTasks = new List<Task>(queueBalanceListenersCopy.Count);
            foreach (IStreamQueueBalanceListener listener in queueBalanceListenersCopy)
            {
                notificatioTasks.Add(listener.QueueDistributionChangeNotification());
            }
            return Task.WhenAll(notificatioTasks);
        }
    }
}
