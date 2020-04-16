using System;
using System.Collections.Generic;

namespace Orleans.Transactions.DeadlockDetection
{
    public readonly struct LockInfo
    {
        public readonly ParticipantId Resource;
        public readonly Guid TxId;
        public readonly bool IsWait;

        private LockInfo(ParticipantId resource, Guid txId, bool isWait)
        {
            this.Resource = resource;
            this.TxId = txId;
            this.IsWait = isWait;
        }

        public static LockInfo ForWait(ParticipantId waitingFor, Guid waiter) =>
            new LockInfo (waitingFor, waiter, true);

        public static LockInfo  ForLock(ParticipantId locked, Guid lockedBy) =>
            new LockInfo (locked, lockedBy, false);

        public static readonly IEqualityComparer<LockInfo> EqualityComparer = new LockKeyEqualityComparer();

        public override string ToString() => this.IsWait ? $"{this.TxId} -W-> {this.Resource}" : $"{this.Resource} -L-> {this.TxId}";

        private class LockKeyEqualityComparer : IEqualityComparer<LockInfo>
        {
            public bool Equals(LockInfo x, LockInfo y) =>
                x.Resource.Equals(y.Resource) && x.TxId.Equals(y.TxId) && x.IsWait == y.IsWait;

            public int GetHashCode(LockInfo  obj)
            {
                unchecked
                {
                    int hashCode = obj.Resource.GetHashCode();
                    hashCode = (hashCode * 397) ^ obj.TxId.GetHashCode();
                    hashCode = (hashCode * 397) ^ obj.IsWait.GetHashCode();
                    return hashCode;
                }
            }
        }

    }
}