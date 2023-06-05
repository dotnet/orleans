using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using System.Threading;

namespace Orleans.Runtime.MembershipService
{
    internal class SiloStatusOracle : ISiloStatusOracle
    {
        private readonly ILocalSiloDetails localSiloDetails;
        private readonly MembershipTableManager membershipTableManager;
        private readonly SiloStatusListenerManager listenerManager;
        private readonly ILogger log;
        private readonly object cacheUpdateLock = new object();
        private MembershipTableSnapshot cachedSnapshot;
        private Dictionary<SiloAddress, SiloStatus> siloStatusCache = new Dictionary<SiloAddress, SiloStatus>();
        private Dictionary<SiloAddress, SiloStatus> siloStatusCacheOnlyActive = new Dictionary<SiloAddress, SiloStatus>();

        public SiloStatusOracle(
            ILocalSiloDetails localSiloDetails,
            MembershipTableManager membershipTableManager,
            ILogger<SiloStatusOracle> logger,
            SiloStatusListenerManager listenerManager)
        {
            this.localSiloDetails = localSiloDetails;
            this.membershipTableManager = membershipTableManager;
            this.listenerManager = listenerManager;
            log = logger;
        }

        public SiloStatus CurrentStatus => membershipTableManager.CurrentStatus;
        public string SiloName => localSiloDetails.Name;
        public SiloAddress SiloAddress => localSiloDetails.SiloAddress;
        
        public SiloStatus GetApproximateSiloStatus(SiloAddress silo)
        {
            var status = membershipTableManager.MembershipTableSnapshot.GetSiloStatus(silo);

            if (status == SiloStatus.None)
            {
                if (CurrentStatus == SiloStatus.Active && log.IsEnabled(LogLevel.Debug))
                {
                    log.LogDebug(
                        (int)ErrorCode.Runtime_Error_100209,
                        "The given SiloAddress {SiloAddress} is not registered in this MembershipOracle.",
                        silo);
                }
            }

            return status;
        }

        public Dictionary<SiloAddress, SiloStatus> GetApproximateSiloStatuses(bool onlyActive = false)
        {
            if (ReferenceEquals(cachedSnapshot, membershipTableManager.MembershipTableSnapshot))
            {
                return onlyActive ? siloStatusCacheOnlyActive : siloStatusCache;
            }

            lock (cacheUpdateLock)
            {
                var currentMembership = membershipTableManager.MembershipTableSnapshot;
                if (ReferenceEquals(cachedSnapshot, currentMembership))
                {
                    return onlyActive ? siloStatusCacheOnlyActive : siloStatusCache;
                }

                var newSiloStatusCache = new Dictionary<SiloAddress, SiloStatus>();
                var newSiloStatusCacheOnlyActive = new Dictionary<SiloAddress, SiloStatus>();
                foreach (var entry in currentMembership.Entries)
                {
                    var silo = entry.Key;
                    var status = entry.Value.Status;
                    newSiloStatusCache[silo] = status;
                    if (status == SiloStatus.Active) newSiloStatusCacheOnlyActive[silo] = status;
                }

                Interlocked.Exchange(ref cachedSnapshot, currentMembership);
                siloStatusCache = newSiloStatusCache;
                siloStatusCacheOnlyActive = newSiloStatusCacheOnlyActive;
                return onlyActive ? newSiloStatusCacheOnlyActive : newSiloStatusCache;
            }
        }

        public bool IsDeadSilo(SiloAddress silo)
        {
            if (silo.Equals(SiloAddress)) return false;

            var status = GetApproximateSiloStatus(silo);
            
            return status == SiloStatus.Dead;
        }

        public bool IsFunctionalDirectory(SiloAddress silo)
        {
            if (silo.Equals(SiloAddress)) return true;

            var status = GetApproximateSiloStatus(silo);
            return !status.IsTerminating();
        }

        public bool TryGetSiloName(SiloAddress siloAddress, out string siloName)
        {
            var snapshot = membershipTableManager.MembershipTableSnapshot.Entries;
            if (snapshot.TryGetValue(siloAddress, out var entry))
            {
                siloName = entry.SiloName;
                return true;
            }

            siloName = default;
            return false;
        }

        public bool SubscribeToSiloStatusEvents(ISiloStatusListener listener) => listenerManager.Subscribe(listener);

        public bool UnSubscribeFromSiloStatusEvents(ISiloStatusListener listener) => listenerManager.Unsubscribe(listener);
    
    }
}
