using System;
using System.Linq;
using Orleans.Configuration;

namespace Orleans.Clustering.DynamoDB
{
    /// <inheritdoc/>
    public class DynamoDBGatewayListProviderHelper
    {
        private const string AccessKeyPropertyName = "AccessKey";
        private const string SecretKeyPropertyName = "SecretKey";
        private const string ServicePropertyName = "Service";
        private const string ReadCapacityUnitsPropertyName = "ReadCapacityUnits";
        private const string WriteCapacityUnitsPropertyName = "WriteCapacityUnits";

        /// <summary>
        /// Parse data connection string to fill in fields in <paramref name="options"/>
        /// </summary>
        /// <param name="dataConnectionString"></param>
        /// <param name="options"></param>
        internal static void ParseDataConnectionString(string dataConnectionString, DynamoDBGatewayOptions options)
        {
            var parameters = dataConnectionString.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);

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
        }
    }
}
