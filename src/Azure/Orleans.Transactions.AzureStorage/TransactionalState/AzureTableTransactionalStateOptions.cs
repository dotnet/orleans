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

    /// <summary>
    /// Configuration validator for <see cref="AzureTableTransactionalStateOptions"/>.
    /// </summary>
    public class AzureTableTransactionalStateOptionsValidator : AzureStorageOperationOptionsValidator<AzureTableTransactionalStateOptions>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="AzureTableTransactionalStateOptionsValidator"/> class.
        /// </summary>
        /// <param name="options">The option to be validated.</param>
        /// <param name="name">The option name to be validated.</param>
        public AzureTableTransactionalStateOptionsValidator(AzureTableTransactionalStateOptions options, string name) : base(options, name)
        {
        }
    }
}
