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
        private readonly IDelpoymentConfiguration deploymentConfig;
        private readonly IStreamQueueMapper streamQueueMapper;
        private readonly List<IStreamQueueBalanceListener> queueBalanceListeners;
        private readonly string mySiloName;
        private readonly List<string> activeSiloNames;
        private List<string> allSiloNames;
        private BestFitBalancer<string, QueueId> resourceBalancer;

        public DeploymentBasedQueueBalancer(
            ISiloStatusOracle siloStatusOracle,
            IDelpoymentConfiguration deploymentConfig,
            IStreamQueueMapper queueMapper)
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
            streamQueueMapper = queueMapper;
            queueBalanceListeners = new List<IStreamQueueBalanceListener>();
            mySiloName = this.siloStatusOracle.SiloName;
            activeSiloNames = new List<string>();
            allSiloNames = this.deploymentConfig.GetAllSiloInstanceNames();
            List<QueueId> allQueues = streamQueueMapper.GetAllQueues().ToList();
            resourceBalancer = new BestFitBalancer<string, QueueId>(allSiloNames, allQueues);

            // get silo names for all active silos
            foreach (SiloAddress siloAddress in this.siloStatusOracle.GetApproximateSiloStatuses(true).Keys)
            {
                string siloName;
                if (this.siloStatusOracle.TryGetSiloName(siloAddress, out siloName))
                {
                    activeSiloNames.Add(siloName);
                }
            }

            // register for notification of changes to silo status for any silo in the cluster
            this.siloStatusOracle.SubscribeToSiloStatusEvents(this);
        }

        /// <summary>
        /// Called when the status of a silo in the cluster changes.
        /// - Update list of silos if silos have been added or removed from the deployment
        /// - Update the list of active silos
        /// - Notify listeners if necessary 
        /// </summary>
        /// <param name="updatedSilo">Silo which status has changed</param>
        /// <param name="status">new silo status</param>
        public void SiloStatusChangeNotification(SiloAddress updatedSilo, SiloStatus status)
        {
            bool changed = false;

            var newSiloNames = deploymentConfig.GetAllSiloInstanceNames();
            lock (activeSiloNames)
            {
                // if silo names has changed, deployment has been changed so we need to update silo names
                changed |= UpdateAllSiloNames(newSiloNames);
                // if a silo status has changed, update list of active silos
                changed |= UpdateActiveSilos(updatedSilo, status);
            }

            // if no change, don't notify
            if (changed)
            {
                NotifyListenders().Ignore();
            }
        }

        public IEnumerable<QueueId> GetMyQueues()
        {
            lock (activeSiloNames)
            {
                List<QueueId> queues;
                if (resourceBalancer.GetDistribution(activeSiloNames).TryGetValue(mySiloName, out queues))
                {
                    return queues;
                }
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
                if (queueBalanceListeners.Contains(observer)) return false;
                
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
        /// <param name="newSiloNames">new silo names</param>
        /// <returns>bool, true if list of all silo names has changed</returns>
        private bool UpdateAllSiloNames(List<string> newSiloNames)
        {
            // Has configured silo names changed
            if (allSiloNames.Count != newSiloNames.Count ||
                !allSiloNames.ListEquals(newSiloNames))
            {
                // record new list of all instance names
                allSiloNames = newSiloNames;
                // rebuild balancer with new list of instance names
                List<QueueId> allQueues = streamQueueMapper.GetAllQueues().ToList();
                resourceBalancer = new BestFitBalancer<string, QueueId>(allSiloNames, allQueues);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Updates active silo names if necessary 
        /// if silo is active and name is not in active list, add it.
        /// if silo is inactive and name is in active list, remove it.
        /// </summary>
        /// <param name="updatedSilo"></param>
        /// <param name="status"></param>
        /// <returns>bool, true if active silo names changed</returns>
        private bool UpdateActiveSilos(SiloAddress updatedSilo, SiloStatus status)
        {
            bool changed = false;
            string siloName;
            // try to get silo name
            if (siloStatusOracle.TryGetSiloName(updatedSilo, out siloName))
            {
                if (status.Equals(SiloStatus.Active) &&    // if silo state became active
                    !activeSiloNames.Contains(siloName))  // and silo name is not currently in active silo list
                {
                    changed = true;
                    activeSiloNames.Add(siloName); // add silo to list of active silos
                }
                else if (!status.Equals(SiloStatus.Active) &&  // if silo state became not active
                         activeSiloNames.Contains(siloName))  // and silo name is currently in active silo list
                {
                    changed = true;
                    activeSiloNames.Remove(siloName); // remove silo from list of active silos
                }
            }
            return changed;
        }

        private Task NotifyListenders()
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
