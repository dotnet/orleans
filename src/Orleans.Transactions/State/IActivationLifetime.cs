namespace Orleans.Transactions.State
{
    internal interface IActivationLifetime
    {
        CancellationToken OnDeactivating { get; }

        IDisposable BlockDeactivation();
    }
}
