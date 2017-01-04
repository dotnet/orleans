using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Orleans.Docker
{
    internal class DockerMembershipOracle : IMembershipOracle
    {
        private readonly Dictionary<SiloAddress, SiloEntry> silos = new Dictionary<SiloAddress, SiloEntry>();
        private readonly ILocalSiloDetails localSiloDetails;
        private readonly Logger log;
        private readonly GlobalConfiguration globalConfig;

        // Cached collection of active silos.
        private volatile Dictionary<SiloAddress, SiloStatus> activeSilosCache;

        // Cached collection of silos.
        private volatile Dictionary<SiloAddress, SiloStatus> allSilosCache;

        private Timer timer;
        private DateTime lastRefreshTime;

        /// <summary>
        /// Initialize a new instance of the <see cref="DockerMembershipOracle"/> class
        /// </summary>
        /// <param name="localSiloDetails">The silo which this instance will provide membership information for.</param>
        /// <param name="globalConfig">The cluster configuration.</param>
        /// <param name="loggerFactory">The logger factory.</param>
        public DockerMembershipOracle(
            ILocalSiloDetails localSiloDetails,
            GlobalConfiguration globalConfig,
            Func<string, Logger> loggerFactory)
        {
            this.log = loggerFactory("MembershipOracle");
            this.localSiloDetails = localSiloDetails;
            this.globalConfig = globalConfig;
            this.silos[SiloAddress] = new SiloEntry(SiloStatus.Created, SiloName);
        }

        /// <summary>
        /// Status of this silo.
        /// </summary>
        public SiloStatus CurrentStatus => GetApproximateSiloStatus(SiloAddress);

        /// <summary>
        /// Address of this silo.
        /// </summary>
        public SiloAddress SiloAddress => localSiloDetails.SiloAddress;

        /// <summary>
        /// Name of this silo.
        /// </summary>
        public string SiloName => this.localSiloDetails.Name;

        public Task BecomeActive()
        {
            throw new NotImplementedException();
        }

        public bool CheckHealth(DateTime lastCheckTime)
        {
            throw new NotImplementedException();
        }

        public IReadOnlyList<SiloAddress> GetApproximateMultiClusterGateways()
        {
            throw new NotImplementedException();
        }

        public SiloStatus GetApproximateSiloStatus(SiloAddress siloAddress)
        {
            throw new NotImplementedException();
        }

        public Dictionary<SiloAddress, SiloStatus> GetApproximateSiloStatuses(bool onlyActive = false)
        {
            throw new NotImplementedException();
        }

        public bool IsDeadSilo(SiloAddress silo)
        {
            throw new NotImplementedException();
        }

        public bool IsFunctionalDirectory(SiloAddress siloAddress)
        {
            throw new NotImplementedException();
        }

        public Task KillMyself()
        {
            throw new NotImplementedException();
        }

        public Task ShutDown()
        {
            throw new NotImplementedException();
        }

        public Task Start()
        {
            throw new NotImplementedException();
        }

        public Task Stop()
        {
            throw new NotImplementedException();
        }

        public bool SubscribeToSiloStatusEvents(ISiloStatusListener observer)
        {
            throw new NotImplementedException();
        }

        public bool TryGetSiloName(SiloAddress siloAddress, out string siloName)
        {
            throw new NotImplementedException();
        }

        public bool UnSubscribeFromSiloStatusEvents(ISiloStatusListener observer)
        {
            throw new NotImplementedException();
        }

        private class SiloEntry
        {
            public SiloEntry(SiloStatus status, string name)
            {
                Status = status;
                Name = name;
            }

            public SiloStatus Status { get; }
            public string Name { get; }

            /// <summary>
            /// Gets or sets a value indicating whether this entry was updated.
            /// </summary>
            public bool Refreshed { get; set; }
        }
    }
}
