using Orleans.Transactions.Abstractions;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Transactions
{
    /// <summary>
    /// Each participant plays a particular role in the commit protocol
    /// </summary>
    internal enum CommitRole
    {
        NotYetDetermined,  // role is known only when prepare message is received from TA
        ReadOnly,          // this participant has not written
        RemoteCommit,      // this participant has written, but is not the TM
        LocalCommit,       // this participant has written, and is the TM
    }

    /// <summary>
    /// Record that is kept for each transaction at each participant
    /// </summary>
    /// <typeparam name="TState">The type of state</typeparam>
    internal class TransactionRecord<TState>
    {
        public TransactionRecord()
        {
        }

        #region execution phase

        // a unique identifier for this transaction
        public Guid TransactionId;

        // the time at which this transaction was started on the TA
        public DateTime Priority;

        // a deadline for the transaction to complete successfully, set by the TA
        public DateTime Deadline;

        // the transaction timestamp as computed by the algorithm
        public DateTime Timestamp;

        // the number of reads and writes that this transaction has performed on this transactional participant
        public int NumberReads;
        public int NumberWrites;

        // the state for this transaction, and the sequence number of this state
        public TState State;
        public long SequenceNumber;
        public bool HasCopiedState;

        public void AddRead()
        {
            NumberReads++;
        }
        public void AddWrite()
        {
            NumberWrites++;
        }

        #endregion

        #region commit phase

        public CommitRole Role;

        // used for readonly and local commit
        public TaskCompletionSource<TransactionalStatus> PromiseForTA;

        // used for local and remote commit
        public ITransactionParticipant TransactionManager;

        // used for local commit
        public List<ITransactionParticipant> WriteParticipants;
        public int WaitCount;
        public DateTime WaitingSince;
        public DateTime? LastConfirmationAttempt;

        // used for remote commit
        public DateTime? LastSent;
        public bool PrepareIsPersisted;
        public TaskCompletionSource<bool> ConfirmationResponsePromise;


        /// <summary>
        /// Indicates whether a transaction record is ready to commit
        /// </summary>
        public bool ReadyToCommit
        {
            get
            {
                switch (Role)
                {
                    case CommitRole.ReadOnly:
                        return true;

                    case CommitRole.LocalCommit:
                        return WaitCount == 0; // received all "Prepared" messages

                    case CommitRole.RemoteCommit:
                        return
                            (ConfirmationResponsePromise != null)  // TM has sent confirm and is waiting for response
                         || (NumberWrites == 0 && LastSent.HasValue);  // this participant did not write and finished prepare

                    default:
                        throw new NotSupportedException($"{Role} is not a supported CommitRole.");
                }
            }
        }

        public bool IsReadOnly
        {
            get
            {
                switch (Role)
                {
                    case CommitRole.ReadOnly:
                        return true;
                    case CommitRole.LocalCommit:
                        return false;
                    case CommitRole.RemoteCommit:
                        return NumberWrites == 0;
                    default:
                        throw new NotSupportedException($"{Role} is not a supported CommitRole.");
                }
            }
        }

        public bool Batchable
        {
            get
            {
                switch (Role)
                {
                    case CommitRole.ReadOnly:
                    case CommitRole.LocalCommit:
                        return true;
                    case CommitRole.RemoteCommit:
                        return NumberWrites == 0;
                    default:
                        throw new NotImplementedException();
                }
            }
        }

        // formatted for debugging commit queue contents
        public override string ToString()
        {
            switch (Role)
            {
                case CommitRole.NotYetDetermined:
                    return $"ND";

                case CommitRole.ReadOnly:
                    return $"RE";

                case CommitRole.LocalCommit:
                    return $"LCE wc={WaitCount} rtb={ReadyToCommit}";

                case CommitRole.RemoteCommit:
                    return $"RCE pip={PrepareIsPersisted} ls={LastSent.HasValue} ro={IsReadOnly} rtb={ReadyToCommit}";

                default:
                    throw new NotSupportedException($"{Role} is not a supported CommitRole.");
            }
        }

        #endregion
    }


}