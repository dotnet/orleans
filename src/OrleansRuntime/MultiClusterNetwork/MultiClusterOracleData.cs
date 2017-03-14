using System;
using System.Collections.Generic;
using Orleans.MultiCluster;
using System.Linq;

namespace Orleans.Runtime.MultiClusterNetwork
{
    internal class MultiClusterOracleData
    {
        private volatile MultiClusterData localData;  // immutable, can read without lock

        // for quick access, we precompute available gateways per cluster
        public IReadOnlyDictionary<string, List<SiloAddress>> ActiveGatewaysByCluster
        {
            get { return activeGatewaysByCluster; }
        }
        private volatile IReadOnlyDictionary<string, List<SiloAddress>> activeGatewaysByCluster;

        private readonly HashSet<GrainReference> confListeners;

        private readonly Logger logger;

        internal MultiClusterData Current { get { return localData; } }

        internal MultiClusterOracleData(Logger log)
        {
            logger = log;
            localData = new MultiClusterData();
            activeGatewaysByCluster = new Dictionary<string, List<SiloAddress>>();
            confListeners = new HashSet<GrainReference>();
        }

        private void ComputeAvailableGatewaysPerCluster()
        {
            // organize active gateways by cluster
            var gws = new Dictionary<string, List<SiloAddress>>();
            foreach (var g in localData.Gateways)
                if (g.Value.Status == GatewayStatus.Active)
                {
                    List<SiloAddress> list;
                    if (!gws.TryGetValue(g.Value.ClusterId, out list))
                        list = gws[g.Value.ClusterId] = new List<SiloAddress>();
                    list.Add(g.Key);
                }

            activeGatewaysByCluster = gws;
        }

        internal bool SubscribeToMultiClusterConfigurationEvents(GrainReference observer)
        {
            if (logger.IsVerbose2)
                logger.Verbose2("SubscribeToMultiClusterConfigurationEvents: {0}", observer);

            lock (confListeners)
            {
                if (confListeners.Contains(observer))
                    return false;

                confListeners.Add(observer);
                return true;
            }
        }


        internal bool UnSubscribeFromMultiClusterConfigurationEvents(GrainReference observer)
        {
            if (logger.IsVerbose3)
                logger.Verbose3("UnSubscribeFromMultiClusterConfigurationEvents: {0}", observer);

            lock (confListeners)
            {
                return confListeners.Remove(observer);
            }
        }

        public MultiClusterData ApplyDataAndNotify(MultiClusterData data)
        {
            if (data.IsEmpty)
                return data;

            MultiClusterData delta;
            MultiClusterData prev = this.localData;

            this.localData = prev.Merge(data, out delta);

            if (logger.IsVerbose2)
                logger.Verbose2("ApplyDataAndNotify: delta {0}", delta);

            if (delta.IsEmpty)
                return delta;

            if (delta.Gateways.Count > 0)
            {
                // some gateways have changed
                ComputeAvailableGatewaysPerCluster();
            }

            if (delta.Configuration != null)
            {
                // notify configuration listeners of change

                List<GrainReference> listenersToNotify;
                lock (confListeners)
                {
                    // make a copy under the lock
                    listenersToNotify = confListeners.ToList();
                }
               
                foreach (var listener in listenersToNotify)
                {
                    try
                    {
                        if (logger.IsVerbose2)
                            logger.Verbose2("-NotificationWork: notify IProtocolParticipant {0} of configuration {1}", listener, delta.Configuration);

                        // enqueue conf change event as grain call
                        var g = InsideRuntimeClient.Current.InternalGrainFactory.Cast<ILogConsistencyProtocolParticipant>(listener);
                        g.OnMultiClusterConfigurationChange(delta.Configuration).Ignore();
                    }
                    catch (Exception exc)
                    {
                        logger.Error(ErrorCode.MultiClusterNetwork_LocalSubscriberException,
                            String.Format("IProtocolParticipant {0} threw exception processing configuration {1}",
                            listener, delta.Configuration), exc);
                    }
                }
            }


            return delta;
        }
    }
}
