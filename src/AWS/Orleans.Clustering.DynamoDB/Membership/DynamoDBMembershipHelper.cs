using Orleans.Configuration;
using System;
using System.Linq;

namespace Orleans.Clustering.DynamoDB
{
    /// <inheritdoc />
    public class DynamoDBMembershipHelper
    {
        private const string AccessKeyPropertyName = "AccessKey";
        private const string SecretKeyPropertyName = "SecretKey";
        private const string ServicePropertyName = "Service";
        private const string TableNamePropertyName = "TableName";
        private const string ReadCapacityUnitsPropertyName = "ReadCapacityUnits";
        private const string WriteCapacityUnitsPropertyName = "WriteCapacityUnits";
        
        /// <summary>
        /// Parse data connection string to fill in fields in <paramref name="options"/>
        /// </summary>
        /// <param name="dataConnectionString"></param>
        /// <param name="options"></param>
        public static void ParseDataConnectionString(string dataConnectionString, DynamoDBClusteringOptions options)
        {
            var parameters = dataConnectionString.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);

            var serviceConfig = Array.Find(parameters, p => p.Contains(ServicePropertyName));
            if (!string.IsNullOrWhiteSpace(serviceConfig))
            {
                var value = serviceConfig.Split(new[] { '=' }, StringSplitOptions.RemoveEmptyEntries);
                if (value.Length == 2 && !string.IsNullOrWhiteSpace(value[1]))
                    options.Service = value[1];
            }

            var tableNameConfig = Array.Find(parameters, p => p.Contains(TableNamePropertyName));
            if (!string.IsNullOrWhiteSpace(tableNameConfig))
            {
                var value = tableNameConfig.Split(new[] { '=' }, StringSplitOptions.RemoveEmptyEntries);
                if (value.Length == 2 && !string.IsNullOrWhiteSpace(value[1]))
                    options.TableName = value[1];
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
        }
    }
}
