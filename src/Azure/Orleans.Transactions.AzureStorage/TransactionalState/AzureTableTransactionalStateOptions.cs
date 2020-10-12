using Orleans.Transactions.AzureStorage;

namespace Orleans.Configuration
{
    public class AzureTableTransactionalStateOptions : AzureStorageOperationOptions
    {
        /// <summary>
        /// Azure table where transactional grain state will be stored
        /// </summary>
        public override string TableName { get; set; } = "TransactionalState";

        /// <summary>
        /// Stage of silo lifecycle where storage should be initialized.  Storage must be initialized prior to use.
        /// </summary>
        public int InitStage { get; set; } = DEFAULT_INIT_STAGE;
        public const int DEFAULT_INIT_STAGE = ServiceLifecycleStage.ApplicationServices;
    }
}
