namespace Orleans.Transactions.TestKit
{
    public interface ITransactionFaultInjector
    {
        void BeforeStore();
        void AfterStore();
    }
}
