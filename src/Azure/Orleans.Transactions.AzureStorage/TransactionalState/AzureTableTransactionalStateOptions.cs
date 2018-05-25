

namespace Orleans.Configuration
{
    public class AzureTableTransactionalStateOptions
    {
        /// <summary>
        /// Azure storage connection string
        /// </summary>
        [RedactConnectionString]
        public string ConnectionString { get; set; }

        /// <summary>
        /// Azure table where transactional grain state will be stored
        /// </summary>
        public string TableName { get; set; } = "TransactionalState";

        /// <summary>
        /// Stage of silo lifecycle where storage should be initialized.  Storage must be initialzed prior to use.
        /// </summary>
        public int InitStage { get; set; } = DEFAULT_INIT_STAGE;
        public const int DEFAULT_INIT_STAGE = ServiceLifecycleStage.ApplicationServices;
    }
}
