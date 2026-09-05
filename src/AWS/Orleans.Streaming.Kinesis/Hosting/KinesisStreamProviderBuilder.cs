using System;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers;
using Orleans.Streaming.Kinesis;
using Orleans.Streams;

[assembly: RegisterProvider("Kinesis", "Streaming", "Silo", typeof(KinesisStreamProviderBuilder))]
[assembly: RegisterProvider("Kinesis", "Streaming", "Client", typeof(KinesisStreamProviderBuilder))]
[assembly: RegisterProvider("AmazonKinesis", "Streaming", "Silo", typeof(KinesisStreamProviderBuilder))]
[assembly: RegisterProvider("AmazonKinesis", "Streaming", "Client", typeof(KinesisStreamProviderBuilder))]
[assembly: RegisterProvider("KinesisStream", "Streaming", "Silo", typeof(KinesisStreamProviderBuilder))]
[assembly: RegisterProvider("KinesisStream", "Streaming", "Client", typeof(KinesisStreamProviderBuilder))]

namespace Orleans.Hosting;

internal sealed class KinesisStreamProviderBuilder : IProviderBuilder<ISiloBuilder>, IProviderBuilder<IClientBuilder>
{
    private const string AwsResourcesConfigurationSection = "AWS:Resources";
    private const string GrainCheckpointStorage = "Grain";
    private const string DynamoDBCheckpointStorage = "DynamoDB";

    public void Configure(ISiloBuilder builder, string? name, IConfigurationSection configurationSection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        builder.AddKinesisStreams(name, configurator =>
        {
            configurator.ConfigureKinesis(GetKinesisOptionsBuilder(name, configurationSection));
            ConfigureCheckpointer(configurator, name, configurationSection);
        });
    }

    public void Configure(IClientBuilder builder, string? name, IConfigurationSection configurationSection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        builder.AddKinesisStreams(name, configurator =>
            configurator.ConfigureKinesis(GetKinesisOptionsBuilder(name, configurationSection)));
    }

    private static Action<OptionsBuilder<KinesisStreamOptions>> GetKinesisOptionsBuilder(
        string name,
        IConfigurationSection configurationSection)
    {
        return optionsBuilder => optionsBuilder.Configure<IConfiguration>((options, configuration) =>
        {
            var streamArn = configurationSection["StreamArn"]
                ?? GetAwsResourceProperty(configuration, configurationSection, "StreamArn");
            var streamName = configurationSection[nameof(options.StreamName)];
            var region = configurationSection[nameof(options.Region)];

            if (!string.IsNullOrWhiteSpace(streamArn))
            {
                var resource = ParseKinesisStreamArn(streamArn, name);
                streamName ??= resource.StreamName;
                region ??= resource.Region;
            }

            var connectionString = GetConnectionString(configuration, configurationSection, name);
            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                options.ConnectionString = connectionString;
            }

            if (string.IsNullOrWhiteSpace(streamName))
            {
                throw new OrleansConfigurationException(
                    $"Kinesis stream provider '{name}' requires StreamName or a referenced AWS Aspire StreamArn.");
            }

            options.StreamName = streamName;
            options.Region = region
                ?? options.Region
                ?? configuration["AWS:Region"]
                ?? configuration["AWS_REGION"]
                ?? configuration["AWS_DEFAULT_REGION"];
            options.Service = configurationSection[nameof(options.Service)]
                ?? options.Service
                ?? configuration["AWS_ENDPOINT_URL_KINESIS"];
            options.AccessKey = configurationSection[nameof(options.AccessKey)] ?? options.AccessKey;
            options.SecretKey = configurationSection[nameof(options.SecretKey)] ?? options.SecretKey;

            if (GetTimeSpan(configurationSection, nameof(options.GetRecordsInterval), name) is { } getRecordsInterval)
            {
                options.GetRecordsInterval = getRecordsInterval;
            }

            if (GetTimeSpan(configurationSection, nameof(options.TopologyCheckInterval), name) is { } topologyCheckInterval)
            {
                options.TopologyCheckInterval = topologyCheckInterval;
            }
        });
    }

    private static void ConfigureCheckpointer(
        SiloKinesisStreamConfigurator configurator,
        string name,
        IConfigurationSection configurationSection)
    {
        var checkpointSection = configurationSection.GetSection("Checkpoint");
        var checkpointStorage = checkpointSection["Type"] ?? GrainCheckpointStorage;
        if (string.Equals(checkpointStorage, GrainCheckpointStorage, StringComparison.OrdinalIgnoreCase))
        {
            configurator.UseGrainCheckpointer(optionsBuilder =>
                optionsBuilder.Configure(options =>
                {
                    options.CheckpointComparer = StreamCheckpointComparers.Numeric;

                    var storageProviderName = checkpointSection[nameof(options.StorageProviderName)];
                    if (!string.IsNullOrWhiteSpace(storageProviderName))
                    {
                        options.StorageProviderName = storageProviderName;
                    }

                    if (GetTimeSpan(checkpointSection, nameof(options.PersistInterval), name) is { } persistInterval)
                    {
                        options.PersistInterval = persistInterval;
                    }
                }));
            return;
        }

        if (string.Equals(checkpointStorage, DynamoDBCheckpointStorage, StringComparison.OrdinalIgnoreCase))
        {
            configurator.UseDynamoDBCheckpointer(optionsBuilder =>
                optionsBuilder.Configure<IConfiguration>((options, configuration) =>
                    ConfigureDynamoDBCheckpointer(options, configuration, checkpointSection, name)));
            return;
        }

        throw new OrleansConfigurationException(
            $"Kinesis stream provider '{name}' has unsupported checkpoint type '{checkpointStorage}'. " +
            $"Use '{GrainCheckpointStorage}' or '{DynamoDBCheckpointStorage}'.");
    }

    private static void ConfigureDynamoDBCheckpointer(
        DynamoDBStreamQueueCheckpointerOptions options,
        IConfiguration configuration,
        IConfigurationSection checkpointSection,
        string name)
    {
        var serviceKey = checkpointSection["ServiceKey"];
        var resourceConfigSection = checkpointSection["ResourceConfigSection"];
        var tableName = checkpointSection[nameof(options.TableName)]
            ?? GetAwsResourceProperty(configuration, checkpointSection, "TableName");
        if ((!string.IsNullOrWhiteSpace(serviceKey) || !string.IsNullOrWhiteSpace(resourceConfigSection))
            && string.IsNullOrWhiteSpace(tableName))
        {
            var resourceReference = serviceKey ?? resourceConfigSection;
            throw new OrleansConfigurationException(
                $"Kinesis stream provider '{name}' references DynamoDB checkpoint resource '{resourceReference}', " +
                "but its AWS Aspire TableName output is missing.");
        }

        options.TableName = tableName ?? options.TableName;
        options.Service = checkpointSection[nameof(options.Service)]
            ?? checkpointSection["Region"]
            ?? configuration["AWS_ENDPOINT_URL_DYNAMODB"]
            ?? configuration["AWS:Region"]
            ?? configuration["AWS_REGION"]
            ?? configuration["AWS_DEFAULT_REGION"]
            ?? options.Service;
        options.AccessKey = checkpointSection[nameof(options.AccessKey)] ?? options.AccessKey;
        options.SecretKey = checkpointSection[nameof(options.SecretKey)] ?? options.SecretKey;
        options.Token = checkpointSection[nameof(options.Token)] ?? options.Token;
        options.ProfileName = checkpointSection[nameof(options.ProfileName)] ?? options.ProfileName;

        if (GetBoolean(checkpointSection, nameof(options.CreateIfNotExists), name) is { } createIfNotExists)
        {
            options.CreateIfNotExists = createIfNotExists;
        }

        if (GetBoolean(checkpointSection, nameof(options.UseProvisionedThroughput), name) is { } useProvisionedThroughput)
        {
            options.UseProvisionedThroughput = useProvisionedThroughput;
        }

        if (GetInt32(checkpointSection, nameof(options.ReadCapacityUnits), name) is { } readCapacityUnits)
        {
            options.ReadCapacityUnits = readCapacityUnits;
        }

        if (GetInt32(checkpointSection, nameof(options.WriteCapacityUnits), name) is { } writeCapacityUnits)
        {
            options.WriteCapacityUnits = writeCapacityUnits;
        }

        if (GetTimeSpan(checkpointSection, nameof(options.PersistInterval), name) is { } persistInterval)
        {
            options.PersistInterval = persistInterval;
        }

        if (GetTimeSpan(checkpointSection, nameof(options.InitializationTimeout), name) is { } initializationTimeout)
        {
            options.InitializationTimeout = initializationTimeout;
        }
    }

    private static int? GetInt32(IConfigurationSection section, string key, string providerName)
    {
        var value = section[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
        {
            throw CreateInvalidValueException(section, key, value, providerName, "an integer");
        }

        return result;
    }

    private static bool? GetBoolean(IConfigurationSection section, string key, string providerName)
    {
        var value = section[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!bool.TryParse(value, out var result))
        {
            throw CreateInvalidValueException(section, key, value, providerName, "a Boolean");
        }

        return result;
    }

    private static TimeSpan? GetTimeSpan(IConfigurationSection section, string key, string providerName)
    {
        var value = section[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var result))
        {
            throw CreateInvalidValueException(section, key, value, providerName, "a time span");
        }

        return result;
    }

    private static OrleansConfigurationException CreateInvalidValueException(
        IConfigurationSection section,
        string key,
        string value,
        string providerName,
        string expectedType)
        => new(
            $"Kinesis stream provider '{providerName}' configuration section '{section.Path}' setting '{key}' " +
            $"must be {expectedType}, but the configured value is '{value}'.");

    private static string? GetConnectionString(
        IConfiguration configuration,
        IConfigurationSection configurationSection,
        string name)
    {
        var connectionString = configurationSection["ConnectionString"];
        var connectionName = configurationSection["ConnectionName"];
        if (string.IsNullOrWhiteSpace(connectionString) && !string.IsNullOrWhiteSpace(connectionName))
        {
            connectionString = configuration.GetConnectionString(connectionName);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new OrleansConfigurationException(
                    $"Kinesis stream provider '{name}' references connection string '{connectionName}', but it is missing.");
            }
        }

        return connectionString;
    }

    private static string? GetAwsResourceProperty(
        IConfiguration configuration,
        IConfigurationSection providerSection,
        string propertyName)
    {
        var resourceConfigurationSection = providerSection["ResourceConfigSection"];
        if (string.IsNullOrWhiteSpace(resourceConfigurationSection))
        {
            var serviceKey = providerSection["ServiceKey"];
            if (string.IsNullOrWhiteSpace(serviceKey))
            {
                return null;
            }

            resourceConfigurationSection = $"{AwsResourcesConfigurationSection}:{serviceKey}";
        }

        return configuration[$"{resourceConfigurationSection}:{propertyName}"];
    }

    private static (string Region, string StreamName) ParseKinesisStreamArn(string streamArn, string name)
    {
        var parts = streamArn.Split(':', 6);
        if (parts.Length != 6
            || !string.Equals(parts[0], "arn", StringComparison.Ordinal)
            || !string.Equals(parts[2], "kinesis", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(parts[3])
            || !parts[5].StartsWith("stream/", StringComparison.Ordinal)
            || parts[5].Length == "stream/".Length)
        {
            throw new OrleansConfigurationException(
                $"Kinesis stream provider '{name}' has invalid StreamArn '{streamArn}'.");
        }

        return (parts[3], parts[5]["stream/".Length..]);
    }
}
