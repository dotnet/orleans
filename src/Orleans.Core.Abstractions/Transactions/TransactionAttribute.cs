using System;

namespace Orleans
{
    /// <summary>
    /// The TransactionAttribute attribute is used to mark methods that start and join transactions.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class TransactionAttribute : Attribute
    {
        public TransactionAttribute(TransactionOption requirement)
        {
            Requirement = requirement;
            ReadOnly = false;
        }

        public TransactionOption Requirement { get; set; }
        public bool ReadOnly { get; set; }
    }

    public enum TransactionOption
    {
        NotSupported, // Logic is not transactional.  If called within the context of a transaction, the context will not be passed ot the call.
        Required,     // Logic requires a transaction.  If called within the context of a transaction, it will use that context, else it will create a new context.
        RequiresNew,  // Logic is transactional and will always create a new transaction context, even if called within an existing transaction context.
    }
}