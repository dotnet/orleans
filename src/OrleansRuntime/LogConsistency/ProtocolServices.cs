using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;
using Orleans.Core;
using Orleans.LogConsistency;
using Orleans.MultiCluster;
using Orleans.Runtime;
using Orleans.SystemTargetInterfaces;
using Orleans.GrainDirectory;

namespace Orleans.Runtime.LogConsistency
{
    /// <summary>
    /// Functionality for use by log view adaptors that run distributed protocols. 
    /// This class allows access to these services to providers that cannot see runtime-internals.
    /// It also stores grain-specific information like the grain reference, and caches 
    /// </summary>
    internal class ProtocolServices : ILogConsistencyProtocolServices
    {

        public GrainReference GrainReference { get { return grain.GrainReference; } }

        private Logger log;

        public IMultiClusterRegistrationStrategy RegistrationStrategy { get; private set; }

        private Grain grain;   // links to the grain that owns this service object

        // pseudo-configuration to use if there is no actual multicluster network
        private static MultiClusterConfiguration PseudoMultiClusterConfiguration;

        internal ProtocolServices(Grain gr, Logger log, IMultiClusterRegistrationStrategy strategy)
        {
            this.grain = gr;
            this.log = log;
            this.RegistrationStrategy = strategy;

            if (!Silo.CurrentSilo.HasMultiClusterNetwork)
            {
                // we are creating a default multi-cluster configuration containing exactly one cluster, this one.
               PseudoMultiClusterConfiguration = new MultiClusterConfiguration(
                   DateTime.UtcNow, new string[] { Silo.CurrentSilo.ClusterId }.ToList());
            }
        }

        public async Task<ILogConsistencyProtocolMessage> SendMessage(ILogConsistencyProtocolMessage payload, string clusterId)
        {
            var silo = Silo.CurrentSilo;
            var mycluster = silo.ClusterId;
            var oracle = silo.LocalMultiClusterOracle;

            log?.Verbose3("SendMessage {0}->{1}: {2}", mycluster, clusterId, payload);

            // send the message to ourself if we are the destination cluster
            if (mycluster == clusterId)
            {
                var g = (ILogConsistencyProtocolParticipant)grain;
                // we are on the same scheduler, so we can call the method directly
                return await g.OnProtocolMessageReceived(payload);
            }

            // cannot send to remote instance if there is only one instance
            if (RegistrationStrategy.Equals(GlobalSingleInstanceRegistration.Singleton))
            {
                throw new ProtocolTransportException("cannot send protocol message to remote instance because there is only one global instance");
            }

            if (PseudoMultiClusterConfiguration != null)
                throw new ProtocolTransportException("no such cluster");

            if (log != null && log.IsVerbose3)
            {
                var gws = oracle.GetGateways();
                log.Verbose3("Available Gateways:\n{0}", string.Join("\n", gws.Select((gw) => gw.ToString())));
            }

            var clusterGateway = oracle.GetRandomClusterGateway(clusterId);

            if (clusterGateway == null)
                throw new ProtocolTransportException("no active gateways found for cluster");

            var repAgent = InsideRuntimeClient.Current.InternalGrainFactory.GetSystemTarget<ILogConsistencyProtocolGateway>(Constants.ProtocolGatewayId, clusterGateway);

            // test hook
            var filter = (oracle as MultiClusterNetwork.MultiClusterOracle).ProtocolMessageFilterForTesting;
            if (filter != null && !filter(payload))
                return null;

            try
            {
                var retMessage = await repAgent.RelayMessage(GrainReference.GrainId, payload);
                return retMessage;
            }
            catch (Exception e)
            {
                throw new ProtocolTransportException("failed sending message to cluster", e);
            }
        }

        public bool MultiClusterEnabled
        {
            get
            {
                return (PseudoMultiClusterConfiguration == null);
            }
        }
    
        public string MyClusterId
        {
            get
            {
                return Silo.CurrentSilo.ClusterId;
            }
        }

        public MultiClusterConfiguration MultiClusterConfiguration
        {
            get
            {
                return PseudoMultiClusterConfiguration ?? Silo.CurrentSilo.LocalMultiClusterOracle.GetMultiClusterConfiguration();
            }
        }

        public IEnumerable<string> GetRemoteInstances()
        {
            if (PseudoMultiClusterConfiguration == null
                && RegistrationStrategy != ClusterLocalRegistration.Singleton)
            {
                var myclusterid = Silo.CurrentSilo.ClusterId;

                foreach (var cluster in Silo.CurrentSilo.LocalMultiClusterOracle.GetMultiClusterConfiguration().Clusters)
                {
                    if (cluster != myclusterid)
                        yield return cluster;
                }
            }
        }

        public void SubscribeToMultiClusterConfigurationChanges()
        {
            if (PseudoMultiClusterConfiguration == null)
            {
                // subscribe this grain to configuration change events
                Silo.CurrentSilo.LocalMultiClusterOracle.SubscribeToMultiClusterConfigurationEvents(GrainReference);
            }
        }

        public void UnsubscribeFromMultiClusterConfigurationChanges()
        {
            if (PseudoMultiClusterConfiguration == null)
            {
                // unsubscribe this grain from configuration change events
                Silo.CurrentSilo.LocalMultiClusterOracle.UnSubscribeFromMultiClusterConfigurationEvents(GrainReference);
            }

        }


        public IEnumerable<string> ActiveClusters
        {
            get
            {
                if (PseudoMultiClusterConfiguration != null)
                    return PseudoMultiClusterConfiguration.Clusters;
                else
                    return Silo.CurrentSilo.LocalMultiClusterOracle.GetActiveClusters();
            }
        }

        public void ProtocolError(string msg, bool throwexception)
        {

            log?.Error((int)(throwexception ? ErrorCode.LogConsistency_ProtocolFatalError : ErrorCode.LogConsistency_ProtocolError),
                string.Format("{0}{1} Protocol Error: {2}",
                    grain.GrainReference,
                    PseudoMultiClusterConfiguration == null ? "" : (" " + Silo.CurrentSilo.ClusterId),
                    msg));

            if (!throwexception)
                return;

            if (PseudoMultiClusterConfiguration != null)
                throw new OrleansException(string.Format("{0} (grain={1})", msg, grain.GrainReference));
            else
                throw new OrleansException(string.Format("{0} (grain={1}, cluster={2})", msg, grain.GrainReference, Silo.CurrentSilo.ClusterId));
        }

        public void CaughtException(string where, Exception e)
        {
            log?.Error((int)ErrorCode.LogConsistency_CaughtException,
               string.Format("{0}{1} Exception Caught at {2}",
                   grain.GrainReference,
                   PseudoMultiClusterConfiguration == null ? "" : (" " + Silo.CurrentSilo.ClusterId),
                   where),e);
        }

        public void CaughtUserCodeException(string callback, string where, Exception e)
        {
            log?.Warn((int)ErrorCode.LogConsistency_UserCodeException,
                string.Format("{0}{1} Exception caught in user code for {2}, called from {3}",
                   grain.GrainReference,
                   PseudoMultiClusterConfiguration == null ? "" : (" " + Silo.CurrentSilo.ClusterId),
                   callback,
                   where), e);
        }

        public void Log(Severity severity, string format, params object[] args)
        {
            if (log != null && log.SeverityLevel >= severity)
            {
                var msg = string.Format("{0}{1} {2}",
                        grain.GrainReference,
                        PseudoMultiClusterConfiguration != null ? "" : (" " + Silo.CurrentSilo.ClusterId),
                        string.Format(format, args));
                log.Log(0, severity, msg, EmptyObjectArray, null);
            }
        }

        private static readonly object[] EmptyObjectArray = new object[0];
    }

}
