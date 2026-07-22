namespace Orleans.Transactions;

/// <summary>
/// Service key used to resolve the transactions subsystem's <see cref="System.TimeProvider"/> from keyed dependency
/// injection. See <see cref="Orleans.Runtime.TimeProviderNames"/> for the core runtime areas.
/// </summary>
public static class TransactionTimeProviderNames
{
    /// <summary>
    /// The clock used by the transactions subsystem.
    /// </summary>
    public const string Transactions = "Orleans.Transactions";
}
