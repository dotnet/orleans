using Orleans.GrainDirectory.AzureStorage;

namespace Orleans.Configuration
{
    /// <summary>
    /// Configures the Azure Table Storage grain directory.
    /// </summary>
    public class AzureTableGrainDirectoryOptions : AzureStorageOperationOptions
    {
        /// <summary>
        /// Gets or sets the Azure Table Storage table name.
        /// </summary>
        public override string TableName { get; set; } = DEFAULT_TABLE_NAME;

        /// <summary>
        /// The default Azure Table Storage table name.
        /// </summary>
        public const string DEFAULT_TABLE_NAME = "GrainDirectory";
    }

    /// <summary>
    /// Validates <see cref="AzureTableGrainDirectoryOptions"/>.
    /// </summary>
    public class AzureTableGrainDirectoryOptionsValidator : AzureStorageOperationOptionsValidator<AzureTableGrainDirectoryOptions>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="AzureTableGrainDirectoryOptionsValidator"/> class.
        /// </summary>
        /// <param name="options">The options to validate.</param>
        /// <param name="name">The name of the grain directory.</param>
        public AzureTableGrainDirectoryOptionsValidator(AzureTableGrainDirectoryOptions options, string name) : base(options, name)
        {
        }
    }
}
