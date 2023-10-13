using Orleans.EventSourcing;
using Orleans.Providers;
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
        [Orleans.GenerateSerializer]
        public class GrainState
        {
            [Orleans.Id(0)]
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

    
    /// A variant of the same grain that does not persist the log, but only the latest grain state
    /// (so it does not do true event sourcing). 
    [LogConsistencyProvider(ProviderName = "StateStorage")]
    public class AccountGrain_PersistStateOnly : AccountGrain
    {
    }
}
