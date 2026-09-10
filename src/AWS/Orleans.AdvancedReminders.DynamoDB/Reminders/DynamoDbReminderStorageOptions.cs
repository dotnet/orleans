namespace Orleans.AdvancedReminders.DynamoDB;

/// <summary>
/// Configuration for Amazon DynamoDB reminder storage.
/// </summary>
public class DynamoDBReminderStorageOptions : DynamoDBClientOptions
{
    /// <summary>
    /// Gets or sets the billing and capacity policy. The default is provisioned capacity with 10 read and 5 write units.
    /// </summary>
    public DynamoDBCapacity Capacity { get; set; } = DynamoDBCapacity.Provisioned(
        DynamoDBStorage.DefaultReadCapacityUnits, DynamoDBStorage.DefaultWriteCapacityUnits);

    /// <summary>
    /// Create the table if it doesn't exist
    /// </summary>
    public bool CreateIfNotExists { get; set; } = true;

    /// <summary>
    /// Update the table if it exists
    /// </summary>
    public bool UpdateIfExists { get; set; } = true;

    /// <summary>
    /// DynamoDB table name.
    /// Defaults to 'OrleansAdvancedReminders'.
    /// </summary>
    public string TableName { get; set; } = "OrleansAdvancedReminders";
}
