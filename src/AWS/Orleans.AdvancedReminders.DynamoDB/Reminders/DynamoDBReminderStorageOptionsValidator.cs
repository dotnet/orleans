using Microsoft.Extensions.Options;

namespace Orleans.AdvancedReminders.DynamoDB;

internal sealed class DynamoDBReminderStorageOptionsValidator : IConfigurationValidator
{
    private readonly DynamoDBReminderStorageOptions _options;

    public DynamoDBReminderStorageOptionsValidator(IOptions<DynamoDBReminderStorageOptions> options)
    {
        _options = options.Value;
    }

    public void ValidateConfiguration()
    {
        if (_options.Capacity is null)
        {
            throw new OrleansConfigurationException($"{nameof(DynamoDBReminderStorageOptions)}.{nameof(DynamoDBReminderStorageOptions.Capacity)} is required.");
        }
    }
}
