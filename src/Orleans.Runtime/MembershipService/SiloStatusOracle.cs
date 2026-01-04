using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime.MembershipService
{
    internal partial class SiloStatusOracle : ISiloStatusOracle
    {
        private readonly ILocalSiloDetails localSiloDetails;
        private readonly IMembershipManager membershipManager;
        private readonly SiloStatusListenerManager listenerManager;
        private readonly ILogger log;
        private readonly object cacheUpdateLock = new object();
        private MembershipTableSnapshot cachedSnapshot;
        private Dictionary<SiloAddress, SiloStatus> siloStatusCache = new Dictionary<SiloAddress, SiloStatus>();
        private Dictionary<SiloAddress, SiloStatus> siloStatusCacheOnlyActive = new Dictionary<SiloAddress, SiloStatus>();
        private ImmutableArray<SiloAddress> _activeSilos = [];

        public SiloStatusOracle(
            ILocalSiloDetails localSiloDetails,
            IMembershipManager membershipManager,
            ILogger<SiloStatusOracle> logger,
            SiloStatusListenerManager listenerManager)
        {
            this.localSiloDetails = localSiloDetails;
            this.membershipManager = membershipManager;
            this.listenerManager = listenerManager;
            this.log = logger;
        }

        public SiloStatus CurrentStatus => this.membershipManager.LocalSiloStatus;
        public string SiloName => this.localSiloDetails.Name;
        public SiloAddress SiloAddress => this.localSiloDetails.SiloAddress;

        public SiloStatus GetApproximateSiloStatus(SiloAddress silo)
        {
            var status = this.membershipManager.CurrentSnapshot.GetSiloStatus(silo);

            if (status == SiloStatus.None)
            {
                if (this.CurrentStatus == SiloStatus.Active)
                {
                    LogSiloAddressNotRegistered(this.log, silo);
                }
            }

            return status;
        }

        public ImmutableArray<SiloAddress> GetActiveSilos()
        {
            EnsureFreshCache();
            return _activeSilos;
        }

        public Dictionary<SiloAddress, SiloStatus> GetApproximateSiloStatuses(bool onlyActive = false)
        {
            EnsureFreshCache();
            return onlyActive ? this.siloStatusCacheOnlyActive : this.siloStatusCache;
        }

        private void EnsureFreshCache()
        {
            var currentMembership = this.membershipManager.CurrentSnapshot;
            if (ReferenceEquals(this.cachedSnapshot, currentMembership))
            {
                return;
            }

            lock (this.cacheUpdateLock)
            {
                currentMembership = this.membershipManager.CurrentSnapshot;
                if (ReferenceEquals(this.cachedSnapshot, currentMembership))
                {
                    return;
                }

                var newSiloStatusCache = new Dictionary<SiloAddress, SiloStatus>();
                var newSiloStatusCacheOnlyActive = new Dictionary<SiloAddress, SiloStatus>();
                var newActiveSilos = ImmutableArray.CreateBuilder<SiloAddress>();
                foreach (var entry in currentMembership.Entries)
                {
                    var silo = entry.Key;
                    var status = entry.Value.Status;
                    newSiloStatusCache[silo] = status;
                    if (status == SiloStatus.Active)
                    {
                        newSiloStatusCacheOnlyActive[silo] = status;
                        newActiveSilos.Add(silo);
                    }
                }

                Interlocked.Exchange(ref this.cachedSnapshot, currentMembership);
                this.siloStatusCache = newSiloStatusCache;
                this.siloStatusCacheOnlyActive = newSiloStatusCacheOnlyActive;
                _activeSilos = newActiveSilos.ToImmutable();
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
            var snapshot = this.membershipManager.CurrentSnapshot.Entries;
            if (snapshot.TryGetValue(siloAddress, out var entry))
            {
                siloName = entry.SiloName;
                return true;
            }

            siloName = default;
            return false;
        }

        public bool SubscribeToSiloStatusEvents(ISiloStatusListener listener) => this.listenerManager.Subscribe(listener);

        public bool UnSubscribeFromSiloStatusEvents(ISiloStatusListener listener) => this.listenerManager.Unsubscribe(listener);

        [LoggerMessage(
            Level = LogLevel.Debug,
            EventId = (int)ErrorCode.Runtime_Error_100209,
            Message = "The given SiloAddress {SiloAddress} is not registered in this MembershipOracle."
        )]
        private static partial void LogSiloAddressNotRegistered(ILogger logger, SiloAddress siloAddress);
    }
}
