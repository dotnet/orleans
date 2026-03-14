using System;
using System.Linq;

namespace Orleans.Configuration
{
    /// <summary>
    /// Configuration for Amazon DynamoDB reminder storage.
    /// </summary>
    public static class DynamoDBReminderStorageOptionsExtensions
    {
        private const string AccessKeyPropertyName = "AccessKey";
        private const string SecretKeyPropertyName = "SecretKey";
        private const string ServicePropertyName = "Service";
        private const string ReadCapacityUnitsPropertyName = "ReadCapacityUnits";
        private const string WriteCapacityUnitsPropertyName = "WriteCapacityUnits";
        private const string UseProvisionedThroughputPropertyName = "UseProvisionedThroughput";
        private const string CreateIfNotExistsPropertyName = "CreateIfNotExists";
        private const string UpdateIfExistsPropertyName = "UpdateIfExists";

        /// <summary>
        /// Configures this instance using the provided connection string.
        /// </summary>
        public static void ParseConnectionString(this DynamoDBReminderStorageOptions options, string connectionString)
        {
            var parameters = connectionString.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);

            var serviceConfig = Array.Find(parameters, p => p.Contains(ServicePropertyName));
            if (!string.IsNullOrWhiteSpace(serviceConfig))
            {
                var value = serviceConfig.Split(new[] { '=' }, StringSplitOptions.RemoveEmptyEntries);
                if (value.Length == 2 && !string.IsNullOrWhiteSpace(value[1]))
                    options.Service = value[1];
            }

            var secretKeyConfig = Array.Find(parameters, p => p.Contains(SecretKeyPropertyName));
            if (!string.IsNullOrWhiteSpace(secretKeyConfig))
            {
                var value = secretKeyConfig.Split(new[] { '=' }, StringSplitOptions.RemoveEmptyEntries);
                if (value.Length == 2 && !string.IsNullOrWhiteSpace(value[1]))
                    options.SecretKey = value[1];
            }

            var accessKeyConfig = Array.Find(parameters, p => p.Contains(AccessKeyPropertyName));
            if (!string.IsNullOrWhiteSpace(accessKeyConfig))
            {
                var value = accessKeyConfig.Split(new[] { '=' }, StringSplitOptions.RemoveEmptyEntries);
                if (value.Length == 2 && !string.IsNullOrWhiteSpace(value[1]))
                    options.AccessKey = value[1];
            }

            var readCapacityUnitsConfig = Array.Find(parameters, p => p.Contains(ReadCapacityUnitsPropertyName));
            if (!string.IsNullOrWhiteSpace(readCapacityUnitsConfig))
            {
                var value = readCapacityUnitsConfig.Split(new[] { '=' }, StringSplitOptions.RemoveEmptyEntries);
                if (value.Length == 2 && !string.IsNullOrWhiteSpace(value[1]))
                    options.ReadCapacityUnits = int.Parse(value[1]);
            }

            var writeCapacityUnitsConfig = Array.Find(parameters, p => p.Contains(WriteCapacityUnitsPropertyName));
            if (!string.IsNullOrWhiteSpace(writeCapacityUnitsConfig))
            {
                var value = writeCapacityUnitsConfig.Split(new[] { '=' }, StringSplitOptions.RemoveEmptyEntries);
                if (value.Length == 2 && !string.IsNullOrWhiteSpace(value[1]))
                    options.WriteCapacityUnits = int.Parse(value[1]);
            }

            var useProvisionedThroughputConfig = Array.Find(parameters, p => p.Contains(UseProvisionedThroughputPropertyName));
            if (!string.IsNullOrWhiteSpace(useProvisionedThroughputConfig))
            {
                var value = useProvisionedThroughputConfig.Split(new[] { '=' }, StringSplitOptions.RemoveEmptyEntries);
                if (value.Length == 2 && !string.IsNullOrWhiteSpace(value[1]))
                    options.UseProvisionedThroughput = bool.Parse(value[1]);
            }

            var createIfNotExistsPropertyNameConfig = Array.Find(parameters, p => p.Contains(CreateIfNotExistsPropertyName));
            if (!string.IsNullOrWhiteSpace(createIfNotExistsPropertyNameConfig))
            {
                var value = createIfNotExistsPropertyNameConfig.Split(new[] { '=' }, StringSplitOptions.RemoveEmptyEntries);
                if (value.Length == 2 && !string.IsNullOrWhiteSpace(value[1]))
                    options.CreateIfNotExists = bool.Parse(value[1]);
            }

            var updateIfExistsPropertyNameConfig = Array.Find(parameters, p => p.Contains(UpdateIfExistsPropertyName));
            if (!string.IsNullOrWhiteSpace(updateIfExistsPropertyNameConfig))
            {
                var value = updateIfExistsPropertyNameConfig.Split(new[] { '=' }, StringSplitOptions.RemoveEmptyEntries);
                if (value.Length == 2 && !string.IsNullOrWhiteSpace(value[1]))
                    options.UpdateIfExists = bool.Parse(value[1]);
            }
        }
    }
}