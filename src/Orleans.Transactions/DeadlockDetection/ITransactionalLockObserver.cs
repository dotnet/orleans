using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Providers;
using Orleans.Services;

namespace Orleans.Transactions.DeadlockDetection
{
    public struct LockInfo
    {
        public bool IsWait { get; set; }
        public Guid TransactionId { get; set; }
        public ParticipantId ResourceId { get; set; }
    }

    public class LockSnapshot
    {
        public bool IsLocallyDeadlocked { get; set; }

        // Should be ignored when IsLocallyDeadlocked is true
        public List<LockInfo> Snapshot { get; } = new List<LockInfo>();
    }

    public interface ITransactionalLockObserver : IControllable
    {
        IDisposable OnResourceRequested(Guid transactionId, ParticipantId resourceId);

        void OnResourceRequestCancelled(Guid transactionId, ParticipantId resourceId);

        IDisposable OnResourceLocked(Guid transactionId, ParticipantId resourceId, bool isReadOnly);

        void OnResourceUnlocked(Guid transactionId, ParticipantId resourceId);

        Task StartDeadlockDetection(ParticipantId lockedResource, IEnumerable<Guid> lockedByTransactions);

        LockSnapshot CreateSnapshot(ParticipantId lockedResource, IEnumerable<Guid> lockedByTransactions);
    }
}