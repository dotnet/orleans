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
        RequiresNew,
        Required,
        NotSupported
    }
}