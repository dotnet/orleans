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

            var serviceConfig = parameters.Where(p => p.Contains(ServicePropertyName)).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(serviceConfig))
            {
                var value = serviceConfig.Split(new[] { '=' }, StringSplitOptions.RemoveEmptyEntries);
                if (value.Length == 2 && !string.IsNullOrWhiteSpace(value[1]))
                    options.Service = value[1];
            }

            var secretKeyConfig = parameters.Where(p => p.Contains(SecretKeyPropertyName)).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(secretKeyConfig))
            {
                var value = secretKeyConfig.Split(new[] { '=' }, StringSplitOptions.RemoveEmptyEntries);
                if (value.Length == 2 && !string.IsNullOrWhiteSpace(value[1]))
                    options.SecretKey = value[1];
            }

            var accessKeyConfig = parameters.Where(p => p.Contains(AccessKeyPropertyName)).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(accessKeyConfig))
            {
                var value = accessKeyConfig.Split(new[] { '=' }, StringSplitOptions.RemoveEmptyEntries);
                if (value.Length == 2 && !string.IsNullOrWhiteSpace(value[1]))
                    options.AccessKey = value[1];
            }

            var readCapacityUnitsConfig = parameters.Where(p => p.Contains(ReadCapacityUnitsPropertyName)).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(readCapacityUnitsConfig))
            {
                var value = readCapacityUnitsConfig.Split(new[] { '=' }, StringSplitOptions.RemoveEmptyEntries);
                if (value.Length == 2 && !string.IsNullOrWhiteSpace(value[1]))
                    options.ReadCapacityUnits = int.Parse(value[1]);
            }

            var writeCapacityUnitsConfig = parameters.Where(p => p.Contains(WriteCapacityUnitsPropertyName)).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(writeCapacityUnitsConfig))
            {
                var value = writeCapacityUnitsConfig.Split(new[] { '=' }, StringSplitOptions.RemoveEmptyEntries);
                if (value.Length == 2 && !string.IsNullOrWhiteSpace(value[1]))
                    options.WriteCapacityUnits = int.Parse(value[1]);
            }

            var useProvisionedThroughputConfig = parameters.Where(p => p.Contains(UseProvisionedThroughputPropertyName)).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(useProvisionedThroughputConfig))
            {
                var value = useProvisionedThroughputConfig.Split(new[] { '=' }, StringSplitOptions.RemoveEmptyEntries);
                if (value.Length == 2 && !string.IsNullOrWhiteSpace(value[1]))
                    options.UseProvisionedThroughput = bool.Parse(value[1]);
            }

            var createIfNotExistsPropertyNameConfig = parameters.Where(p => p.Contains(CreateIfNotExistsPropertyName)).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(createIfNotExistsPropertyNameConfig))
            {
                var value = createIfNotExistsPropertyNameConfig.Split(new[] { '=' }, StringSplitOptions.RemoveEmptyEntries);
                if (value.Length == 2 && !string.IsNullOrWhiteSpace(value[1]))
                    options.CreateIfNotExists = bool.Parse(value[1]);
            }

            var updateIfExistsPropertyNameConfig = parameters.Where(p => p.Contains(UpdateIfExistsPropertyName)).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(updateIfExistsPropertyNameConfig))
            {
                var value = updateIfExistsPropertyNameConfig.Split(new[] { '=' }, StringSplitOptions.RemoveEmptyEntries);
                if (value.Length == 2 && !string.IsNullOrWhiteSpace(value[1]))
                    options.UpdateIfExists = bool.Parse(value[1]);
            }
        }
    }
}