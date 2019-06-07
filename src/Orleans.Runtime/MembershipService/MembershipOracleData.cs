using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime.MembershipService
{
    internal class MembershipTableCache
    {
        private readonly object updateLock = new object();
        private MembershipTableData table;
        
        internal SiloAddress MyAddress { get; }
        internal SiloStatus CurrentStatus { get; private set; }

        internal MembershipTableCache(ILocalSiloDetails siloDetails)
        {
            this.MyAddress = siloDetails.SiloAddress;
            this.CurrentStatus = SiloStatus.Created;
        }

        internal void UpdateMyStatusLocal(SiloStatus status)
        {
            lock (this.updateLock)
            {
                this.CurrentStatus = status;
            }
        }

        internal Dictionary<SiloAddress, SiloStatus> GetSiloStatuses(Func<SiloStatus, bool> filter, bool includeMyself)
        {
            var cached = this.table;
            if (cached == null)
            {
                var status = this.CurrentStatus;
                if (includeMyself && filter(status))
                {
                    return new Dictionary<SiloAddress, SiloStatus> { [this.MyAddress] = status };
                }

                return new Dictionary<SiloAddress, SiloStatus>();
            }

            return this.table.GetSiloStatuses(filter, includeMyself, this.MyAddress);
        }

        internal bool Update(MembershipTableData updated)
        {
            lock (this.updateLock)
            {
                if (this.table != null && this.table.Version.Version > updated.Version.Version) return false;
                this.table = updated;
                return true;
            }
        }

        public override string ToString()
        {
            var cached = this.table;
            var silos = Utils.EnumerableToString(cached.Members, pair => $"SiloAddress={pair.Item1.SiloAddress} Status={pair.Item1.Status}");
            return $"CurrentSiloStatus = {this.CurrentStatus}, {cached.Members.Count} silos: {silos}.";
        }
    }
}
