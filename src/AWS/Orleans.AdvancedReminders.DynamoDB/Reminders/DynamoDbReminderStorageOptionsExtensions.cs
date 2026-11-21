using System;

namespace Orleans.AdvancedReminders.DynamoDB;

/// <summary>
/// Configuration for Amazon DynamoDB reminder storage.
/// </summary>
public static class DynamoDBReminderStorageOptionsExtensions
{
    private const string AccessKeyPropertyName = "AccessKey";
    private const string SecretKeyPropertyName = "SecretKey";
    private const string TokenPropertyName = "Token";
    private const string ServicePropertyName = "Service";
    private const string CapacityPropertyName = nameof(DynamoDBReminderStorageOptions.Capacity);
    private const string CreateIfNotExistsPropertyName = "CreateIfNotExists";
    private const string UpdateIfExistsPropertyName = "UpdateIfExists";

    /// <summary>
    /// Configures this instance using the provided connection string.
    /// </summary>
    public static void ParseConnectionString(this DynamoDBReminderStorageOptions options, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(connectionString);
        var parameters = connectionString.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var parameter in parameters)
        {
            ThrowIfLegacyCapacityOption(parameter.Split(new[] { '=' }, 2)[0]);
        }

        var capacityConfig = Array.Find(parameters, p => p.StartsWith($"{CapacityPropertyName}=", StringComparison.OrdinalIgnoreCase));
        if (capacityConfig is not null)
        {
            options.Capacity = DynamoDBCapacity.Parse(capacityConfig[(capacityConfig.IndexOf('=') + 1)..]);
        }

        var serviceConfig = Array.Find(parameters, p => p.StartsWith($"{ServicePropertyName}=", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(serviceConfig))
        {
            var value = serviceConfig.Split(new[] { '=' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (value.Length == 2 && !string.IsNullOrWhiteSpace(value[1]))
                options.Service = value[1];
        }

        var secretKeyConfig = Array.Find(parameters, p => p.StartsWith($"{SecretKeyPropertyName}=", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(secretKeyConfig))
        {
            var value = secretKeyConfig.Split(new[] { '=' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (value.Length == 2 && !string.IsNullOrWhiteSpace(value[1]))
                options.SecretKey = value[1];
        }

        var accessKeyConfig = Array.Find(parameters, p => p.StartsWith($"{AccessKeyPropertyName}=", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(accessKeyConfig))
        {
            var value = accessKeyConfig.Split(new[] { '=' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (value.Length == 2 && !string.IsNullOrWhiteSpace(value[1]))
                options.AccessKey = value[1];
        }

        var tokenConfig = Array.Find(parameters, p => p.StartsWith($"{TokenPropertyName}=", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(tokenConfig))
        {
            var value = tokenConfig.Split(new[] { '=' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (value.Length == 2 && !string.IsNullOrWhiteSpace(value[1]))
                options.Token = value[1];
        }

        var createIfNotExistsPropertyNameConfig = Array.Find(parameters, p => p.StartsWith($"{CreateIfNotExistsPropertyName}=", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(createIfNotExistsPropertyNameConfig))
        {
            var value = createIfNotExistsPropertyNameConfig.Split(new[] { '=' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (value.Length == 2 && !string.IsNullOrWhiteSpace(value[1]))
                options.CreateIfNotExists = bool.Parse(value[1]);
        }

        var updateIfExistsPropertyNameConfig = Array.Find(parameters, p => p.StartsWith($"{UpdateIfExistsPropertyName}=", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(updateIfExistsPropertyNameConfig))
        {
            var value = updateIfExistsPropertyNameConfig.Split(new[] { '=' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (value.Length == 2 && !string.IsNullOrWhiteSpace(value[1]))
                options.UpdateIfExists = bool.Parse(value[1]);
        }
    }

    internal static void ThrowIfLegacyCapacityOption(string key)
    {
        if (key.Equals("ReadCapacityUnits", StringComparison.OrdinalIgnoreCase)
            || key.Equals("WriteCapacityUnits", StringComparison.OrdinalIgnoreCase)
            || key.Equals("UseProvisionedThroughput", StringComparison.OrdinalIgnoreCase))
        {
            throw new OrleansConfigurationException($"Use Capacity=OnDemand or Capacity=Provisioned(<read units>,<write units>) instead of {key}.");
        }
    }
}
