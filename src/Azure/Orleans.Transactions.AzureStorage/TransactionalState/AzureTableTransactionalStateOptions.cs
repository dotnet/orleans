using Orleans.Transactions.AzureStorage;

namespace Orleans.Configuration
{
    /// <summary>
    /// Configuration options for Azure Table Storage transactional state storage.
    /// </summary>
    public class AzureTableTransactionalStateOptions : AzureStorageOperationOptions
    {
        /// <summary>
        /// Gets or sets the name of the table where transactional state is stored.
        /// </summary>
        public override string TableName { get; set; } = "TransactionalState";

        /// <summary>
        /// Gets or sets the silo lifecycle stage at which the storage provider is initialized.
        /// </summary>
        public int InitStage { get; set; } = DEFAULT_INIT_STAGE;

        /// <summary>
        /// The default silo lifecycle stage at which the storage provider is initialized.
        /// </summary>
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
