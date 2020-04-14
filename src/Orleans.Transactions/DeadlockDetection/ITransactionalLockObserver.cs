using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Providers;
using Orleans.Services;

namespace Orleans.Transactions.DeadlockDetection
{

    internal class LockSnapshot
    {
        public bool IsLocallyDeadlocked { get; set; }

        // Should be ignored when IsLocallyDeadlocked is true
        public List<LockInfo> Snapshot { get; } = new List<LockInfo>();
    }

    internal interface ITransactionalLockObserver : IControllable
    {
        void OnResourceRequested(Guid transactionId, ParticipantId resourceId);

        void OnResourceLocked(Guid transactionId, ParticipantId resourceId, bool isReadOnly);

        void OnResourceUnlocked(Guid transactionId, ParticipantId resourceId);

        Task StartDeadlockDetection(ParticipantId lockedResource, IEnumerable<Guid> lockedByTransactions);
    }
}