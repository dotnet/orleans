using Orleans;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace TestGrainInterfaces
{
    /// <summary>
    /// A grain that models a bank account
    /// </summary>
    public interface IAccountGrain : IGrainWithStringKey
    {
        Task<uint> Balance();

        Task Deposit(uint amount, Guid guid, string desc);

        Task<bool> Withdraw(uint amount, Guid guid, string desc);

        Task<IReadOnlyList<Transaction>> GetTransactionLog();
    }

    // the classes below represent events/transactions on the account
    // all fields are user-defined (none have a special meaning),
    // so these can be any type of object you like, as long as they are serializable
    // (so they can be sent over the wire and persisted in a log).

    [Serializable]
    [GenerateSerializer]
    public abstract class Transaction
    {
        /// <summary> A unique identifier for this transaction  </summary>
        [Id(0)]
        public Guid Guid { get; set; }

        /// <summary> A description for this transaction  </summary>
        [Id(1)]
        public String Description { get; set; }

        /// <summary> time on which the request entered the system  </summary>
        [Id(2)]
        public DateTime IssueTime { get; set; }
    }

    [Serializable]
    [GenerateSerializer]
    public class DepositTransaction : Transaction
    {
        [Id(0)]
        public uint DepositAmount { get; set; }
    }

    [Serializable]
    [GenerateSerializer]
    public class WithdrawalTransaction : Transaction
    {
        [Id(0)]
        public uint WithdrawalAmount { get; set; }
    }

}
