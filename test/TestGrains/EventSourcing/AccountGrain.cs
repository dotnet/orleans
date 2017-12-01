using Orleans.Concurrency;
using Orleans.EventSourcing;
using Orleans.MultiCluster;
using Orleans.Providers;
using Orleans.Storage;
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
    /// 
    /// Configured to use the default storage provider.
    /// Configured to use the LogStorage consistency provider.
    /// 
    /// This provider persists all events, and allows us to retrieve them all.
    /// </summary>

    [StorageProvider(ProviderName = "Default")]
    [LogConsistencyProvider(ProviderName = "LogStorage")]

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
                {
                    // the balance is checked before we withdraw,
                    // so this exception is never actually thrown
                    throw new InvalidOperationException("internal error");
                }

                Balance = Balance - d.WithdrawalAmount;
            }
        }

        public Task<uint> Balance()
        {
            return Task.FromResult(State.Balance);
        }

        public async Task Deposit(uint amount, Guid guid, string description)
        {
            // we are queueing the event so it gets retried on races
            EnqueueEvent(new DepositTransaction()
            {
                Guid = guid,
                IssueTime = DateTime.UtcNow,
                DepositAmount = amount,
                Description = description
            });

            // wait for confirmation
            await ConfirmEvents();
        }

        public async Task<bool> Withdraw(uint amount, Guid guid, string description)
        {
            // if the balance is too low, can't withdraw
            if (State.Balance < amount)
            {
                return false;
            }

            try
            {
                await RaiseEvent(new WithdrawalTransaction()
                {
                    Guid = guid,
                    IssueTime = DateTime.UtcNow,
                    WithdrawalAmount = amount,
                    Description = description
                });

                return true;
            }
            catch (LostRaceException)
            {
                // no money was withdrawn.
                return false;
            }
        }

        public Task<IReadOnlyList<Transaction>> GetTransactionLog()
        {
            return RetrieveConfirmedEvents(0, Version);
        }
    }

    
    /// A variant of the same grain that does not persist the log, but only the latest grain state
    /// (so it does not do true event sourcing). 
    [LogConsistencyProvider(ProviderName = "StateStorage")]
    public class AccountGrain_PersistStateOnly : AccountGrain
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
