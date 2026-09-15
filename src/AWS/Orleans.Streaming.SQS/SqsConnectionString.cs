using System;
using System.Collections.Generic;
using Orleans.Configuration;

namespace Orleans.Streaming.SQS;

internal static class SqsConnectionString
{
    public static IReadOnlyDictionary<string, string> Parse(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new OrleansConfigurationException("SQS streaming requires a non-empty connection string.");
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in connectionString.Split(
            ';',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = segment.IndexOf('=');
            if (separator <= 0 || string.IsNullOrWhiteSpace(segment[(separator + 1)..]))
            {
                throw new OrleansConfigurationException(
                    "SQS streaming connection strings use non-empty key=value segments separated by semicolons.");
            }

            var key = segment[..separator].Trim();
            var value = segment[(separator + 1)..].Trim();
            if (key.Length == 0)
            {
                throw new OrleansConfigurationException(
                    "SQS streaming connection string property names must be non-empty.");
            }

            if (!result.TryAdd(key, value))
            {
                throw new OrleansConfigurationException(
                    $"SQS streaming connection string property '{key}' is configured more than once.");
            }
        }

        return result;
    }

    public static void ValidateCredentials(IReadOnlyDictionary<string, string> properties)
    {
        var hasAccessKey = properties.ContainsKey("AccessKey");
        var hasSecretKey = properties.ContainsKey("SecretKey");
        var hasSessionToken = properties.ContainsKey("SessionToken");

        if (hasSessionToken && (!hasAccessKey || !hasSecretKey))
        {
            throw new OrleansConfigurationException(
                "SQS streaming connection string property 'SessionToken' requires both AccessKey and SecretKey.");
        }

        if (hasAccessKey != hasSecretKey)
        {
            throw new OrleansConfigurationException(
                "SQS streaming connection strings must configure AccessKey and SecretKey together.");
        }
    }
}
