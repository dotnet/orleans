using Orleans.Concurrency;
using Orleans.EventSourcing;
using Orleans.MultiCluster;
using Orleans.Providers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TestGrainInterfaces;

namespace TestGrains
{
    /// <summary>
    /// An example of a journaled grain that models a bank account.
    /// Uses the default providers for log-consistency and storage.
    /// </summary>
    public class AccountGrain : JournaledGrain<AccountGrain.GrainState, Transaction>, IAccountGrain
    {
        /// <summary>
        /// The state of this grain is just the current balance.
        /// </summary>
        [Serializable]
        public class GrainState
        {
            public uint Balance { get; set; }

            public void Apply(DepositTransaction d)
            {
                Balance = Balance + d.DepositAmount;
            }

            public void Apply(WithdrawalTransaction d)
            {
                if (d.WithdrawalAmount > Balance)
                    throw new InvalidOperationException("we make sure this never happens");

                Balance = Balance - d.WithdrawalAmount;
            }
        }

        public Task<uint> Balance()
        {
            return Task.FromResult(State.Balance);
        }

        public Task Deposit(uint amount, Guid guid, string description)
        {
            RaiseEvent(new DepositTransaction() {
                Guid = guid,
                IssueTime = DateTime.UtcNow,
                DepositAmount = amount,
                Description = description
            });

            // we wait for storage ack
            return ConfirmEvents();
        }

        public Task<bool> Withdraw(uint amount, Guid guid, string description)
        {
            // if the balance is too low, can't withdraw
            // reject it immediately
            if (State.Balance < amount)
                return Task.FromResult(false);

            // use a conditional event for withdrawal
            // (conditional events commit only if the version hasn't already changed in the meantime)
            // this is important so we can guarantee that we never overdraw
            // even if racing with other clusters, of in transient duplicate grain situations
            return RaiseConditionalEvent(new WithdrawalTransaction()
            {
                Guid = guid,
                IssueTime = DateTime.UtcNow,
                WithdrawalAmount = amount,
                Description = description
            });
        }

        public Task<IReadOnlyList<Transaction>> GetTransactionLog()
        {
            return RetrieveConfirmedEvents(0, Version);
        }
    }


    /// A variant of the same grain that does not store events, but uses an IStorageProvider to store the latest snapshot
    /// (so it does not do true event sourcing). 
    [LogConsistencyProvider(ProviderName = "StateStorage")]
    public class AccountGrain_StateStorage : AccountGrain
    {
    }

    /// A variant of the same grain that uses an IStorageProvider, to store the entire log as a single object
    [LogConsistencyProvider(ProviderName = "LogStorage")]
    public class AccountGrain_LogStorage : AccountGrain
    {
    }

    /// A variant of the account grain that uses one instance per cluster
    [OneInstancePerCluster]
    public class AccountGrain_OneInstancePerCluster : AccountGrain
    {
    }

    /// A variant of the account grain that uses a single global instance
    [GlobalSingleInstance]
    public class AccountGrain_SingleGlobalInstance : AccountGrain
    {
    }
}
