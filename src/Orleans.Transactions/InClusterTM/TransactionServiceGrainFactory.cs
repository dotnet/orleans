
namespace Orleans.Transactions
{
    internal class TransactionServiceGrainFactory
    {
        private readonly IGrainFactory grainFactory;

        public TransactionServiceGrainFactory(IGrainFactory grainFactory)
        {
            this.grainFactory = grainFactory;
        }

        public ITransactionManagerService CreateTransactionManagerService()
        {
            return this.grainFactory.GetGrain<ITransactionManagerGrain>(0);
        }
    }
}
