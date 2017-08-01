using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;

namespace Orleans.Streams
{
    internal class StaticClusterConfigDeploymentBalancer : DeploymentBasedQueueBalancer
    {
        public StaticClusterConfigDeploymentBalancer(
            ISiloStatusOracle siloStatusOracle,
            ClusterConfiguration clusterConfiguration)
            : base(siloStatusOracle, new StaticClusterDeploymentConfiguration(clusterConfiguration), true)
        { }
    }

    internal class DynamicClusterConfigDeploymentBalancer : DeploymentBasedQueueBalancer
    {
        public DynamicClusterConfigDeploymentBalancer(
            ISiloStatusOracle siloStatusOracle,
            ClusterConfiguration clusterConfiguration)
            : base(siloStatusOracle, new StaticClusterDeploymentConfiguration(clusterConfiguration), false)
        { }
    }

    internal class DynamicAzureDeploymentBalancer : DeploymentBasedQueueBalancer
    {
        public DynamicAzureDeploymentBalancer(
            ISiloStatusOracle siloStatusOracle,
            IServiceProvider serviceProvider)
            : base(siloStatusOracle, DeploymentBasedQueueBalancerUtils.CreateDeploymentConfigForAzure(serviceProvider), false)
        { }
    }

    internal class StaticAzureDeploymentBalancer : DeploymentBasedQueueBalancer
    {
        public StaticAzureDeploymentBalancer(
            ISiloStatusOracle siloStatusOracle,
            IServiceProvider serviceProvider)
            : base(siloStatusOracle, DeploymentBasedQueueBalancerUtils.CreateDeploymentConfigForAzure(serviceProvider), true)
        { }
    }

    internal static class DeploymentBasedQueueBalancerUtils
    {
        public static IDeploymentConfiguration CreateDeploymentConfigForAzure(IServiceProvider svp)
        {
            Logger logger = LogManager.GetLogger(typeof(DeploymentBasedQueueBalancer).Name, LoggerType.Runtime);
            return AssemblyLoader.LoadAndCreateInstance<IDeploymentConfiguration>(Constants.ORLEANS_AZURE_UTILS_DLL, logger, svp);
        }
    }

    /// <summary>
    /// DeploymentBasedQueueBalancer is a stream queue balancer that uses deployment information to
    /// help balance queue distribution.
    /// DeploymentBasedQueueBalancer uses the deployment configuration to determine how many silos
    /// to expect and uses a silo status oracle to determine which of the silos are available.  With
    /// this information it tries to balance the queues using a best fit resource balancing algorithm.
    /// </summary>
    internal class DeploymentBasedQueueBalancer : ISiloStatusListener, IStreamQueueBalancer
    {
        private TimeSpan siloMaturityPeriod;
        private readonly ISiloStatusOracle siloStatusOracle;
        private readonly IDeploymentConfiguration deploymentConfig;
        private ReadOnlyCollection<QueueId> allQueues;
        private readonly List<IStreamQueueBalanceListener> queueBalanceListeners;
        private readonly bool isFixed;
        private bool isStarting;

        public DeploymentBasedQueueBalancer(
            ISiloStatusOracle siloStatusOracle,
            IDeploymentConfiguration deploymentConfig,
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

            this.siloStatusOracle = siloStatusOracle;
            this.deploymentConfig = deploymentConfig;
            queueBalanceListeners = new List<IStreamQueueBalanceListener>();
            this.isFixed = isFixed;
            isStarting = true;

            // register for notification of changes to silo status for any silo in the cluster
            this.siloStatusOracle.SubscribeToSiloStatusEvents(this);
        }

        public Task Initialize(string strProviderName,
            IStreamQueueMapper queueMapper,
            TimeSpan siloMaturityPeriod)
        {
            if (queueMapper == null)
            {
                throw new ArgumentNullException("queueMapper");
            }
            this.allQueues = new ReadOnlyCollection<QueueId>(queueMapper.GetAllQueues().ToList());
            this.siloMaturityPeriod = siloMaturityPeriod;
            NotifyAfterStart().Ignore();
            return Task.CompletedTask;
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
            if (!isStarting)
            {
                // notify, uncoditionaly, and deal with changes in GetMyQueues()
                NotifyListeners().Ignore();
            }
        }

        public IEnumerable<QueueId> GetMyQueues()
        {
            BestFitBalancer<string, QueueId> balancer = GetBalancer();
            bool useIdealDistribution = isFixed || isStarting;
            Dictionary<string, List<QueueId>> distribution = useIdealDistribution
                ? balancer.IdealDistribution
                : balancer.GetDistribution(GetActiveSilos(siloStatusOracle));

            List<QueueId> myQueues;
            if (distribution.TryGetValue(siloStatusOracle.SiloName, out myQueues))
            {
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

        public bool UnSubscribeFromQueueDistributionChangeEvents(IStreamQueueBalanceListener observer)
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

        private static List<string> GetActiveSilos(ISiloStatusOracle siloStatusOracle)
        {
            var activeSiloNames = new List<string>();
            foreach (var kvp in siloStatusOracle.GetApproximateSiloStatuses(true))
            {
                string siloName;
                if (siloStatusOracle.TryGetSiloName(kvp.Key, out siloName))
                {
                    activeSiloNames.Add(siloName);
                }
            }
            return activeSiloNames;
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
