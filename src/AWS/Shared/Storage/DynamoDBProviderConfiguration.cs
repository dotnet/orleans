using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Configuration;
using Orleans.Runtime;

#if CLUSTERING_DYNAMODB
namespace Orleans.Clustering.DynamoDB
#elif PERSISTENCE_DYNAMODB
namespace Orleans.Persistence.DynamoDB
#elif REMINDERS_DYNAMODB
namespace Orleans.Reminders.DynamoDB
#else
// No default namespace intentionally to cause compile errors if something is not defined
#endif
{
    internal sealed class DynamoDBProviderConfiguration
    {
        private const string AwsResourcesConfigurationSection = "AWS:Resources";
        private readonly IConfigurationSection _providerSection;
        private readonly IConfiguration _configuration;
        private readonly IReadOnlyDictionary<string, string> _connectionValues;
        private readonly string? _referenceName;

        private DynamoDBProviderConfiguration(
            IConfigurationSection providerSection,
            IConfiguration configuration,
            IReadOnlyDictionary<string, string> connectionValues,
            string? referenceName)
        {
            _providerSection = providerSection;
            _configuration = configuration;
            _connectionValues = connectionValues;
            _referenceName = referenceName;
        }

        public static DynamoDBProviderConfiguration Create(
            IConfigurationSection providerSection,
            IConfiguration configuration)
        {
            var serviceKey = GetNonEmpty(providerSection["ServiceKey"]);
            var connectionName = GetNonEmpty(providerSection["ConnectionName"]);
            if (serviceKey is not null
                && connectionName is not null
                && !string.Equals(serviceKey, connectionName, StringComparison.OrdinalIgnoreCase))
            {
                throw new OrleansConfigurationException(
                    "DynamoDB provider configuration cannot specify different ServiceKey and ConnectionName values.");
            }

            var referenceName = serviceKey ?? connectionName;
            var connectionString = GetNonEmpty(providerSection["ConnectionString"]);
            if (connectionString is null && referenceName is not null)
            {
                connectionString = configuration.GetConnectionString(referenceName);
            }

            return new(
                providerSection,
                configuration,
                ParseConnectionString(connectionString),
                referenceName);
        }

        public void ConfigureClientOptions(DynamoDBClientOptions options)
        {
            SetIfPresent(value => options.AccessKey = value, GetValue(nameof(options.AccessKey)));
            SetIfPresent(value => options.SecretKey = value, GetValue(nameof(options.SecretKey)));
            SetIfPresent(value => options.Token = value, GetValue(nameof(options.Token), "SessionToken"));
            var profileName = GetValue(nameof(options.ProfileName), "Profile")
                ?? GetNonEmpty(_configuration["AWS:Profile"])
                ?? GetNonEmpty(_configuration["AWS_PROFILE"]);
            SetIfPresent(value => options.ProfileName = value, profileName);

            var service = GetValue(nameof(options.Service), "ServiceURL", "Endpoint", "Region")
                ?? GetNonEmpty(_configuration["AWS_ENDPOINT_URL_DYNAMODB"])
                ?? GetNonEmpty(_configuration["AWS:Region"])
                ?? GetNonEmpty(_configuration["AWS_REGION"])
                ?? GetNonEmpty(_configuration["AWS_DEFAULT_REGION"]);
            SetIfPresent(value => options.Service = value, service);
        }

        public string? GetValue(params string[] names)
        {
            foreach (var name in names)
            {
                var value = GetProviderValue(name);
                if (value is not null)
                {
                    return value;
                }
            }

            return null;
        }

        public static void ValidateClientOptions(DynamoDBClientOptions options, string providerName)
        {
            if (string.IsNullOrWhiteSpace(options.Service))
            {
                throw new OrleansConfigurationException(
                    $"The {nameof(DynamoDBClientOptions.Service)} is required for {providerName}.");
            }

            ValidateCredentials(options, providerName);
        }

        public static void ValidateTableOptions(
            DynamoDBClientOptions options,
            string? tableName,
            bool useProvisionedThroughput,
            int readCapacityUnits,
            int writeCapacityUnits,
            string providerName,
            bool requireService = true)
        {
            if (requireService)
            {
                ValidateClientOptions(options, providerName);
            }
            else
            {
                ValidateCredentials(options, providerName);
            }

            if (string.IsNullOrWhiteSpace(tableName))
            {
                throw new OrleansConfigurationException($"The TableName is required for {providerName}.");
            }

            if (useProvisionedThroughput && readCapacityUnits <= 0)
            {
                throw new OrleansConfigurationException(
                    $"ReadCapacityUnits must be greater than zero when provisioned throughput is enabled for {providerName}.");
            }

            if (useProvisionedThroughput && writeCapacityUnits <= 0)
            {
                throw new OrleansConfigurationException(
                    $"WriteCapacityUnits must be greater than zero when provisioned throughput is enabled for {providerName}.");
            }
        }

        private static void ValidateCredentials(DynamoDBClientOptions options, string providerName)
        {
            var hasAccessKey = !string.IsNullOrEmpty(options.AccessKey);
            var hasSecretKey = !string.IsNullOrEmpty(options.SecretKey);
            if (hasAccessKey != hasSecretKey)
            {
                throw new OrleansConfigurationException(
                    $"The {nameof(DynamoDBClientOptions.AccessKey)} and {nameof(DynamoDBClientOptions.SecretKey)} " +
                    $"must either both be configured or both be omitted for {providerName}.");
            }

            if (!string.IsNullOrEmpty(options.Token) && !hasAccessKey)
            {
                throw new OrleansConfigurationException(
                    $"The {nameof(DynamoDBClientOptions.Token)} requires explicit credentials for {providerName}.");
            }

            if (hasAccessKey && !string.IsNullOrEmpty(options.ProfileName))
            {
                throw new OrleansConfigurationException(
                    $"Explicit credentials and {nameof(DynamoDBClientOptions.ProfileName)} cannot both be configured for {providerName}.");
            }
        }

        private string? GetProviderValue(string name)
        {
            var value = GetNonEmpty(_providerSection[name])
                ?? GetNonEmpty(_providerSection[$"ConnectionProperties:{name}"])
                ?? GetNonEmpty(_providerSection[$"Resource:{name}"])
                ?? GetNonEmpty(_providerSection[$"AWS:{name}"]);
            if (value is not null)
            {
                return value;
            }

            if (_referenceName is not null)
            {
                value = GetNonEmpty(_configuration[$"{AwsResourcesConfigurationSection}:{_referenceName}:{name}"])
                    ?? GetNonEmpty(_configuration[$"{EncodeEnvironmentVariableName(_referenceName)}_{name.ToUpperInvariant()}"]);
                if (value is not null)
                {
                    return value;
                }
            }

            return _connectionValues.TryGetValue(name, out value) ? GetNonEmpty(value) : null;
        }

        private static IReadOnlyDictionary<string, string> ParseConnectionString(string? connectionString)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return result;
            }

            if (!connectionString.Contains(';', StringComparison.Ordinal)
                && Uri.TryCreate(connectionString, UriKind.Absolute, out var serviceUri))
            {
                result[nameof(DynamoDBClientOptions.Service)] = serviceUri.AbsoluteUri;
                return result;
            }

            foreach (var parameter in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = parameter.IndexOf('=');
                if (separator <= 0 || separator == parameter.Length - 1)
                {
                    continue;
                }

                var key = parameter[..separator].Trim();
                var value = parameter[(separator + 1)..].Trim();
                if (key.Length == 0 || value.Length == 0)
                {
                    continue;
                }

                if (key.Equals("ServiceURL", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("Endpoint", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("Region", StringComparison.OrdinalIgnoreCase))
                {
                    key = nameof(DynamoDBClientOptions.Service);
                }
                else if (key.Equals("SessionToken", StringComparison.OrdinalIgnoreCase))
                {
                    key = nameof(DynamoDBClientOptions.Token);
                }
                else if (key.Equals("Profile", StringComparison.OrdinalIgnoreCase))
                {
                    key = nameof(DynamoDBClientOptions.ProfileName);
                }

                result[key] = value;
            }

            return result;
        }

        private static string EncodeEnvironmentVariableName(string name)
        {
            var builder = new StringBuilder(name.Length + 1);
            if (char.IsAsciiDigit(name[0]))
            {
                builder.Append('_');
            }

            foreach (var character in name)
            {
                builder.Append(char.IsAsciiLetterOrDigit(character) ? char.ToUpperInvariant(character) : '_');
            }

            return builder.ToString();
        }

        private static string? GetNonEmpty(string? value)
            => string.IsNullOrWhiteSpace(value) ? null : value;

        private static void SetIfPresent(Action<string> setter, string? value)
        {
            if (value is not null)
            {
                setter(value);
            }
        }
    }
}
