using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System.Threading;

namespace Orleans.Runtime.MembershipService
{
    internal class SiloStatusOracleCompatibilityShim : IMembershipOracle
    {
        private readonly ILocalSiloDetails localSiloDetails;
        private readonly MembershipService membershipService;
        private readonly MembershipAgent membershipAgent;
        private readonly SiloStatusListenerManager listenerManager;
        private readonly ILogger log;
        private readonly object cacheUpdateLock = new object();
        private ClusterMembershipSnapshot cachedSnapshot;
        private Dictionary<SiloAddress, SiloStatus> siloStatusCache = new Dictionary<SiloAddress, SiloStatus>();
        private Dictionary<SiloAddress, SiloStatus> siloStatusCacheOnlyActive = new Dictionary<SiloAddress, SiloStatus>();

        public SiloStatusOracleCompatibilityShim(
            ILocalSiloDetails localSiloDetails,
            MembershipService membershipService,
            MembershipAgent membershipAgent,
            ILoggerFactory loggerFactory,
            SiloStatusListenerManager listenerManager)
        {
            this.localSiloDetails = localSiloDetails;
            this.membershipService = membershipService;
            this.membershipAgent = membershipAgent;
            this.listenerManager = listenerManager;
            this.log = loggerFactory.CreateLogger("MembershipOracle");
        }

        public SiloStatus CurrentStatus => this.membershipAgent.ExpectedStatus;
        public string SiloName => this.localSiloDetails.Name;
        public SiloAddress SiloAddress => this.localSiloDetails.SiloAddress;
        
        public SiloStatus GetApproximateSiloStatus(SiloAddress silo)
        {
            var status = this.membershipService.CurrentMembership.GetSiloStatus(silo);

            if (status == SiloStatus.None)
            {
                if (this.CurrentStatus == SiloStatus.Active && this.log.IsEnabled(LogLevel.Debug))
                {
                    this.log.Debug(ErrorCode.Runtime_Error_100209, "-The given siloAddress {0} is not registered in this MembershipOracle.", silo);
                }
            }

            return status;
        }

        public Dictionary<SiloAddress, SiloStatus> GetApproximateSiloStatuses(bool onlyActive = false)
        {
            if (ReferenceEquals(this.cachedSnapshot, this.membershipService.CurrentMembership))
            {
                return onlyActive ? this.siloStatusCacheOnlyActive : this.siloStatusCache;
            }

            lock (this.cacheUpdateLock)
            {
                var currentMembership = this.membershipService.CurrentMembership;
                if (ReferenceEquals(this.cachedSnapshot, currentMembership))
                {
                    return onlyActive ? this.siloStatusCacheOnlyActive : this.siloStatusCache;
                }

                var newSiloStatusCache = new Dictionary<SiloAddress, SiloStatus>();
                var newSiloStatusCacheOnlyActive = new Dictionary<SiloAddress, SiloStatus>();
                foreach (var entry in currentMembership.Members)
                {
                    var silo = entry.Key;
                    var status = entry.Value.Status;
                    newSiloStatusCache[silo] = status;
                    if (status == SiloStatus.Active) newSiloStatusCacheOnlyActive[silo] = status;
                }

                Interlocked.Exchange(ref this.cachedSnapshot, currentMembership);
                this.siloStatusCache = newSiloStatusCache;
                this.siloStatusCacheOnlyActive = newSiloStatusCacheOnlyActive;
                return onlyActive ? newSiloStatusCacheOnlyActive : newSiloStatusCache;
            }
        }

        public bool IsDeadSilo(SiloAddress silo)
        {
            if (silo.Equals(this.SiloAddress)) return false;

            var status = this.GetApproximateSiloStatus(silo);
            
            return status == SiloStatus.Dead;
        }

        public bool IsFunctionalDirectory(SiloAddress silo)
        {
            if (silo.Equals(this.SiloAddress)) return true;

            var status = this.GetApproximateSiloStatus(silo);
            return !status.IsTerminating();
        }

        public bool TryGetSiloName(SiloAddress siloAddress, out string siloName)
        {
            var snapshot = this.membershipService.CurrentMembership.Members;
            if (snapshot.TryGetValue(siloAddress, out var entry))
            {
                siloName = entry.Name;
                return true;
            }

            siloName = default;
            return false;
        }

        public bool SubscribeToSiloStatusEvents(ISiloStatusListener listener) => this.listenerManager.Subscribe(listener);

        public bool UnSubscribeFromSiloStatusEvents(ISiloStatusListener listener) => this.listenerManager.Unsubscribe(listener);

        public IReadOnlyList<SiloAddress> GetApproximateMultiClusterGateways() => throw new NotSupportedException();

        public Task BecomeActive() => throw new NotSupportedException();

        public Task KillMyself() => throw new NotSupportedException();

        public Task ShutDown() => throw new NotSupportedException();

        public Task Start() => throw new NotSupportedException();

        public Task Stop() => throw new NotSupportedException();

        public bool CheckHealth(DateTime lastCheckTime) => true;
    }
}
