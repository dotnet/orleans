using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Runtime;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Orleans.Streams
{
    /// <summary>
    /// DeploymentBasedQueueBalancer is a stream queue balancer that uses deployment information to
    /// help balance queue distribution.
    /// DeploymentBasedQueueBalancer uses the deployment configuration to determine how many silos
    /// to expect and uses a silo status oracle to determine which of the silos are available.  With
    /// this information it tries to balance the queues using a best fit resource balancing algorithm.
    /// </summary>
    public class DeploymentBasedQueueBalancer : QueueBalancerBase, IStreamQueueBalancer
    {
        private readonly ISiloStatusOracle siloStatusOracle;
        private readonly IDeploymentConfiguration deploymentConfig;
        private readonly DeploymentBasedQueueBalancerOptions options;
        private readonly ConcurrentDictionary<SiloAddress, bool> immatureSilos;
        private List<QueueId> allQueues;
        private bool isStarting;

        public DeploymentBasedQueueBalancer(
            ISiloStatusOracle siloStatusOracle,
            IDeploymentConfiguration deploymentConfig,
            DeploymentBasedQueueBalancerOptions options,
            IServiceProvider services,
            ILogger<DeploymentBasedQueueBalancer> logger)
            : base (services, logger)
        {
            this.siloStatusOracle = siloStatusOracle ?? throw new ArgumentNullException(nameof(siloStatusOracle));
            this.deploymentConfig = deploymentConfig ?? throw new ArgumentNullException(nameof(deploymentConfig));
            this.options = options;

            isStarting = true;

            // record all already active silos as already mature. 
            // Even if they are not yet, they will be mature by the time I mature myself (after I become !isStarting).
            immatureSilos = new ConcurrentDictionary<SiloAddress, bool>(
                from s in siloStatusOracle.GetApproximateSiloStatuses(true).Keys
                where !s.Equals(siloStatusOracle.SiloAddress)
                select new KeyValuePair<SiloAddress, bool>(s, false));
        }

        public static IStreamQueueBalancer Create(IServiceProvider services, string name, IDeploymentConfiguration deploymentConfiguration)
        {
            var options = services.GetRequiredService<IOptionsMonitor<DeploymentBasedQueueBalancerOptions>>().Get(name);
            return ActivatorUtilities.CreateInstance<DeploymentBasedQueueBalancer>(services, options, deploymentConfiguration);
        }

        public override Task Initialize(IStreamQueueMapper queueMapper)
        {
            if (queueMapper == null)
            {
                throw new ArgumentNullException("queueMapper");
            }
            this.allQueues = queueMapper.GetAllQueues().ToList();
            NotifyAfterStart().Ignore();
            return base.Initialize(queueMapper);
        }
        
        private async Task NotifyAfterStart()
        {
            await Task.Delay(this.options.SiloMaturityPeriod);
            isStarting = false;
            await NotifyListeners();
        }

        private async Task RecordImmatureSilo(SiloAddress updatedSilo)
        {
            immatureSilos[updatedSilo] = true;      // record as immature
            await Task.Delay(this.options.SiloMaturityPeriod);
            immatureSilos[updatedSilo] = false;     // record as mature
        }

        public override IEnumerable<QueueId> GetMyQueues()
        {
            BestFitBalancer<string, QueueId> balancer = GetBalancer();
            bool useIdealDistribution = this.options.IsFixed || isStarting;
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

        /// <summary>
        /// Checks to see if deployment configuration has changed, by adding or removing silos.
        /// If so, it updates the list of all silo names and creates a new resource balancer.
        /// This should occur rarely.
        /// </summary>
        private BestFitBalancer<string, QueueId> GetBalancer()
        {
            var allSiloNames = deploymentConfig.GetAllSiloNames();
            // rebuild balancer with new list of instance names
            return new BestFitBalancer<string, QueueId>(allSiloNames, allQueues);
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

        protected override void OnClusterMembershipChange(HashSet<SiloAddress> activeSilos)
        {
            SignalClusterChange(activeSilos).Ignore();
        }

        private async Task SignalClusterChange(HashSet<SiloAddress> activeSilos)
        {
            List<Task> tasks = new List<Task>();
            // look at all currently active silos not including myself
            foreach (var silo in activeSilos)
            {
                if (!silo.Equals(siloStatusOracle.SiloAddress) && !immatureSilos.ContainsKey(silo))
                {
                    tasks.Add(RecordImmatureSilo(silo));
                }
            }
            if (!isStarting)
            {
                // notify, uncoditionaly, and deal with changes in GetMyQueues()
                await NotifyListeners();
            }
            if (tasks.Count > 0)
            {
                await Task.WhenAll(tasks);
                await this.NotifyListeners(); // notify, uncoditionaly, and deal with changes it in GetMyQueues()
            }
        }
    }
}
