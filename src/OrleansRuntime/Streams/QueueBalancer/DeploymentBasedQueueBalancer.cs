/*
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
using System.Collections.Generic;
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
        private readonly ISiloStatusOracle siloStatusOracle;
        private readonly IDeploymentConfiguration deploymentConfig;
        private readonly List<QueueId> allQueues;
        private readonly List<IStreamQueueBalanceListener> queueBalanceListeners;
        private readonly string mySiloName;
        private readonly bool isFixed;
        
        public DeploymentBasedQueueBalancer(
            ISiloStatusOracle siloStatusOracle,
            IDeploymentConfiguration deploymentConfig,
            IStreamQueueMapper queueMapper,
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
            allQueues = queueMapper.GetAllQueues().ToList();
            queueBalanceListeners = new List<IStreamQueueBalanceListener>();
            mySiloName = this.siloStatusOracle.SiloName;
            this.isFixed = isFixed;

            // register for notification of changes to silo status for any silo in the cluster
            this.siloStatusOracle.SubscribeToSiloStatusEvents(this);
        }

        /// <summary>
        /// Called when the status of a silo in the cluster changes.
        /// - Notify listeners
        /// </summary>
        /// <param name="updatedSilo">Silo which status has changed</param>
        /// <param name="status">new silo status</param>
        public void SiloStatusChangeNotification(SiloAddress updatedSilo, SiloStatus status)
        {
            NotifyListeners().Ignore();
        }

        public IEnumerable<QueueId> GetMyQueues()
        {
            BestFitBalancer<string, QueueId> balancer = GetBalancer();
            Dictionary<string, List<QueueId>> distribution = isFixed
                ? balancer.IdealDistribution
                : balancer.GetDistribution(GetActiveSilos());
            List<QueueId> queues;
            if (distribution.TryGetValue(mySiloName, out queues))
            {
                return queues;
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
            var allSiloNames = deploymentConfig.GetAllSiloInstanceNames();
            // rebuild balancer with new list of instance names
            return new BestFitBalancer<string, QueueId>(allSiloNames, allQueues);
        }

        private List<string> GetActiveSilos()
        {
            var activeSiloNames = new List<string>();
            foreach(var siloStatus in siloStatusOracle.GetApproximateSiloStatuses(true))
            {
                string siloName;
                if (siloStatusOracle.TryGetSiloName(siloStatus.Key, out siloName))
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
