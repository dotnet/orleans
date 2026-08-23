using System.Reflection;
using SqsMessage = Amazon.SQS.Model.Message;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers;
using Orleans.Serialization;
using Orleans.Streaming.SQS.Streams;
using Orleans.Streams;
using OrleansAWSUtils.Streams;
using TestExtensions;
using Xunit;

namespace AWSUtils.Tests.Streaming;

[CollectionDefinition(CollectionName, DisableParallelization = true)]
public sealed class SQSStreamProviderBuilderTestCollection
{
    public const string CollectionName = "SQS stream provider builder tests";
}

[Collection(SQSStreamProviderBuilderTestCollection.CollectionName)]
[TestSuite("BVT")]
[TestProvider("SQS")]
[TestArea("Streaming")]
[TestCategory("AWS"), TestCategory("SQS")]
public sealed class SQSStreamProviderBuilderTests
{
    private const string ProviderName = "orders";

    [Fact]
    public void Assembly_RegistersSqsProviderForSiloAndClient()
    {
        var registrations = typeof(SqsStreamProviderBuilder)
            .Assembly
            .GetCustomAttributes<RegisterProviderAttribute>()
            .Where(attribute => attribute.Type == typeof(SqsStreamProviderBuilder))
            .Select(attribute => (attribute.Name, attribute.Kind, attribute.Target))
            .ToHashSet();

        Assert.Equal(4, registrations.Count);
        Assert.Contains(("SQS", "Streaming", "Silo"), registrations);
        Assert.Contains(("SQS", "Streaming", "Client"), registrations);
        Assert.Contains(("AmazonSQS", "Streaming", "Silo"), registrations);
        Assert.Contains(("AmazonSQS", "Streaming", "Client"), registrations);
    }

    [Fact]
    public void ConfigureSilo_Region_BindsSqsPartitionAndCacheOptions()
    {
        var builder = CreateSiloBuilder(
            ProviderName,
            [
                ("Region", "us-west-2"),
                ("FifoQueue", "true"),
                ("ReceiveWaitTimeSeconds", "12"),
                ("VisibilityTimeoutSeconds", "45"),
                ("ReceiveMessageAttributes:0", "TraceId"),
                ("ReceiveMessageAttributes:1", "Tenant"),
                ("ReceiveMessageSystemAttributes:0", "SentTimestamp"),
                ("ReceiveMessageSystemAttributes:1", "SequenceNumber"),
                ("PartitionCount", "7"),
                ("CacheSize", "2048"),
            ]);

        ConfigureSilo(builder, ProviderName);

        using var services = builder.Services.BuildServiceProvider();
        var sqsOptions = GetOptions<SqsOptions>(services, ProviderName);
        var partitionOptions = GetOptions<HashRingStreamQueueMapperOptions>(services, ProviderName);
        var cacheOptions = GetOptions<SimpleQueueCacheOptions>(services, ProviderName);

        Assert.Equal("Service=us-west-2", sqsOptions.ConnectionString);
        Assert.True(sqsOptions.FifoQueue);
        Assert.Equal(12, sqsOptions.ReceiveWaitTimeSeconds);
        Assert.Equal(45, sqsOptions.VisibilityTimeoutSeconds);
        Assert.Equal(["TraceId", "Tenant"], sqsOptions.ReceiveMessageAttributes);
        Assert.Equal(["SentTimestamp", "SequenceNumber"], sqsOptions.ReceiveMessageSystemAttributes);
        Assert.Equal(7, partitionOptions.TotalQueueCount);
        Assert.Equal(2048, cacheOptions.CacheSize);
        Assert.DoesNotContain(
            typeof(SqsOptions).GetProperties(),
            property => property.Name.Contains("Credential", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("AccessKey", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("SecretKey", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Profile", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("ServiceEndpoint")]
    [InlineData("Endpoint")]
    public void ConfigureClient_CustomEndpoint_BindsSqsAndPartitionOptions(string endpointKey)
    {
        const string endpoint = "http://127.0.0.1:9324";
        var builder = CreateClientBuilder(
            ProviderName,
            [
                (endpointKey, endpoint),
                ("FifoQueue", "true"),
                ("ReceiveWaitTimeSeconds", "4"),
                ("VisibilityTimeoutSeconds", "30"),
                ("ReceiveMessageAttributes:0", "CorrelationId"),
                ("ReceiveMessageSystemAttributes:0", "ApproximateReceiveCount"),
                ("PartitionCount", "5"),
                ("CacheSize", "999"),
            ]);

        ConfigureClient(builder, ProviderName);

        using var services = builder.Services.BuildServiceProvider();
        var sqsOptions = GetOptions<SqsOptions>(services, ProviderName);
        var partitionOptions = GetOptions<HashRingStreamQueueMapperOptions>(services, ProviderName);

        Assert.Equal($"Service={endpoint}", sqsOptions.ConnectionString);
        Assert.True(sqsOptions.FifoQueue);
        Assert.Equal(4, sqsOptions.ReceiveWaitTimeSeconds);
        Assert.Equal(30, sqsOptions.VisibilityTimeoutSeconds);
        Assert.Equal(["CorrelationId"], sqsOptions.ReceiveMessageAttributes);
        Assert.Equal(["ApproximateReceiveCount"], sqsOptions.ReceiveMessageSystemAttributes);
        Assert.Equal(5, partitionOptions.TotalQueueCount);
        Assert.DoesNotContain(
            builder.Services,
            descriptor => descriptor.ServiceType == typeof(IConfigureOptions<SimpleQueueCacheOptions>));
    }

    [Fact]
    public void ConfigureClient_WhitespaceLocationAliases_UsesConfiguredEndpoint()
    {
        const string endpoint = "http://127.0.0.1:9324";
        var builder = CreateClientBuilder(
            ProviderName,
            [
                ("Region", " "),
                ("ServiceEndpoint", " "),
                ("Endpoint", endpoint),
            ]);

        ConfigureClient(builder, ProviderName);

        using var services = builder.Services.BuildServiceProvider();
        var options = GetOptions<SqsOptions>(services, ProviderName);

        Assert.Equal($"Service={endpoint}", options.ConnectionString);
    }

    [Fact]
    public void ConfigureClient_ConnectionStringWithWhitespaceSegment_IsAccepted()
    {
        var builder = CreateClientBuilder(
            ProviderName,
            [("ConnectionString", "Service=us-east-1; ")]);

        ConfigureClient(builder, ProviderName);

        using var services = builder.Services.BuildServiceProvider();
        var options = GetOptions<SqsOptions>(services, ProviderName);

        Assert.Equal("Service=us-east-1; ", options.ConnectionString);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfigureSiloAndClient_IdenticalConfiguration_ProducesIdenticalQueueTopology(bool fifoQueue)
    {
        const string providerName = "Topology";
        const string serviceId = "topology-service";
        (string Key, string? Value)[] configuration =
        [
            ("Region", "eu-central-1"),
            ("FifoQueue", fifoQueue.ToString()),
            ("PartitionCount", "4"),
        ];
        var siloBuilder = CreateSiloBuilder(providerName, configuration);
        var clientBuilder = CreateClientBuilder(providerName, configuration);

        ConfigureSilo(siloBuilder, providerName);
        ConfigureClient(clientBuilder, providerName);

        using var siloServices = siloBuilder.Services.BuildServiceProvider();
        using var clientServices = clientBuilder.Services.BuildServiceProvider();
        var siloSqsOptions = GetOptions<SqsOptions>(siloServices, providerName);
        var clientSqsOptions = GetOptions<SqsOptions>(clientServices, providerName);
        var siloMapper = CreateQueueMapper(siloServices, providerName);
        var clientMapper = CreateQueueMapper(clientServices, providerName);
        var siloQueueIds = siloMapper.GetAllQueues().Order().ToArray();
        var clientQueueIds = clientMapper.GetAllQueues().Order().ToArray();
        var siloPhysicalNames = GetPhysicalQueueNames(siloQueueIds, siloSqsOptions, serviceId);
        var clientPhysicalNames = GetPhysicalQueueNames(clientQueueIds, clientSqsOptions, serviceId);
        var expectedPhysicalNames = Enumerable.Range(0, 4)
            .Select(index => $"{serviceId}-topology-{index}{(fifoQueue ? ".fifo" : string.Empty)}")
            .Order()
            .ToArray();

        Assert.Equal(4, siloQueueIds.Length);
        Assert.Equal(siloQueueIds, clientQueueIds);
        Assert.Equal(fifoQueue, siloSqsOptions.FifoQueue);
        Assert.Equal(siloSqsOptions.FifoQueue, clientSqsOptions.FifoQueue);
        Assert.Equal(expectedPhysicalNames, siloPhysicalNames);
        Assert.Equal(siloPhysicalNames, clientPhysicalNames);
        Assert.All(siloPhysicalNames, name => Assert.Equal(fifoQueue, name.EndsWith(".fifo", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("ServiceKey")]
    [InlineData("ConnectionName")]
    public void ConfigureClient_ServiceKey_ResolvesReferencedConnectionString(string referenceKey)
    {
        const string serviceKey = "shared-sqs";
        const string connectionString = "Service=http://localhost:9324";
        var builder = CreateClientBuilder(
            ProviderName,
            [(referenceKey, serviceKey)],
            [($"ConnectionStrings:{serviceKey}", connectionString)]);

        ConfigureClient(builder, ProviderName);

        using var services = builder.Services.BuildServiceProvider();
        var options = GetOptions<SqsOptions>(services, ProviderName);

        Assert.Equal(connectionString, options.ConnectionString);
        Assert.DoesNotContain(ProviderName, options.ConnectionString, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/queue/", options.ConnectionString, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConfigureSilo_AwsRegionEnvironment_UsesSdkCredentialChainConfiguration()
    {
        string[] variableNames =
        [
            "AWS_REGION",
            "AWS_DEFAULT_REGION",
            "AWS_PROFILE",
            "AWS_ACCESS_KEY_ID",
            "AWS_SECRET_ACCESS_KEY",
        ];
        var previousValues = variableNames.ToDictionary(name => name, Environment.GetEnvironmentVariable);

        try
        {
            Environment.SetEnvironmentVariable("AWS_REGION", "ap-southeast-2");
            Environment.SetEnvironmentVariable("AWS_DEFAULT_REGION", "ap-southeast-2");
            Environment.SetEnvironmentVariable("AWS_PROFILE", "integration-profile");
            Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", "test-access-key");
            Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", "test-secret-key");
            var configuration = new ConfigurationBuilder()
                .AddEnvironmentVariables()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        [$"Orleans:Streaming:{ProviderName}:ProviderType"] = "SQS",
                    })
                .Build();
            var builder = new TestSiloBuilder(configuration);

            ConfigureSilo(builder, ProviderName);

            using var services = builder.Services.BuildServiceProvider();
            var options = GetOptions<SqsOptions>(services, ProviderName);

            Assert.Equal("Service=ap-southeast-2", options.ConnectionString);
            Assert.DoesNotContain("integration-profile", options.ConnectionString, StringComparison.Ordinal);
            Assert.DoesNotContain("test-access-key", options.ConnectionString, StringComparison.Ordinal);
            Assert.DoesNotContain("test-secret-key", options.ConnectionString, StringComparison.Ordinal);
            Assert.DoesNotContain(
                typeof(SqsOptions).GetProperties(),
                property => property.Name.Contains("Credential", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Contains("AccessKey", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Contains("SecretKey", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Contains("Profile", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            foreach (var (name, value) in previousValues)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    [Theory]
    [InlineData("DataAdapterServiceKey")]
    [InlineData("DataAdapterKey")]
    public void ConfigureSilo_DataAdapterServiceKey_ResolvesKeyedAdapter(string adapterConfigurationKey)
    {
        const string adapterServiceKey = "custom-adapter";
        var defaultAdapter = new FakeSqsDataAdapter("default");
        var keyedAdapter = new FakeSqsDataAdapter("keyed");
        var builder = CreateSiloBuilder(
            ProviderName,
            [
                ("Region", "us-east-1"),
                (adapterConfigurationKey, adapterServiceKey),
            ]);
        builder.Services.AddSingleton<ISQSDataAdapter>(defaultAdapter);
        builder.Services.AddKeyedSingleton<ISQSDataAdapter>(adapterServiceKey, keyedAdapter);
        builder.Services
            .AddLogging()
            .AddSerializer()
            .Configure<ClusterOptions>(options =>
            {
                options.ServiceId = "adapter-test-service";
                options.ClusterId = "adapter-test-cluster";
            });

        ConfigureSilo(builder, ProviderName);

        using var services = builder.Services.BuildServiceProvider();
        var configuredAdapter = services.GetRequiredKeyedService<ISQSDataAdapter>(ProviderName);
        var adapterFactory = SQSAdapterFactory.Create(services, ProviderName);
        var factoryAdapter = typeof(SQSAdapterFactory)
            .GetField("dataAdapter", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(adapterFactory);

        Assert.Same(keyedAdapter, configuredAdapter);
        Assert.NotSame(defaultAdapter, configuredAdapter);
        Assert.Equal("keyed", Assert.IsType<FakeSqsDataAdapter>(configuredAdapter).Id);
        Assert.Same(keyedAdapter, factoryAdapter);
    }

    [Theory]
    [MemberData(nameof(InvalidConfigurations))]
    public void ConfigureSilo_InvalidConfiguration_ThrowsActionableError(
        string caseId,
        string[] providerValues,
        string[] rootValues,
        string expectedMessage)
    {
        var builder = CreateSiloBuilder(
            ProviderName,
            ToPairs(providerValues),
            ToPairs(rootValues));

        var exception = Assert.Throws<OrleansConfigurationException>(() =>
        {
            ConfigureSilo(builder, ProviderName);
            using var services = builder.Services.BuildServiceProvider();
            _ = GetOptions<SqsOptions>(services, ProviderName);
        });

        Assert.NotEmpty(caseId);
        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConfigureClient_MissingServiceLocation_ThrowsActionableError()
    {
        var builder = CreateClientBuilder(ProviderName, []);

        ConfigureClient(builder, ProviderName);

        using var services = builder.Services.BuildServiceProvider();
        var exception = Assert.Throws<OrleansConfigurationException>(
            () => GetOptions<SqsOptions>(services, ProviderName));

        Assert.Contains("SQS streaming", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("service location", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ServiceKey", exception.Message, StringComparison.Ordinal);
        Assert.Contains("AWS_REGION", exception.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string, string[], string[], string> InvalidConfigurations => new()
    {
        {
            "ConnectionStringMalformed",
            ["ConnectionString", "not-a-connection-string"],
            [],
            "key=value"
        },
        {
            "ConnectionStringMissingService",
            ["ConnectionString", "AccessKey=access;SecretKey=secret"],
            [],
            "non-empty Service"
        },
        {
            "ConflictingServiceKeyAndConnectionName",
            ["ServiceKey", "primary", "ConnectionName", "secondary", "Region", "us-east-1"],
            [],
            "ServiceKey and ConnectionName"
        },
        {
            "ConflictingConnectionStringAndLocation",
            ["ConnectionString", "Service=us-east-1", "Region", "us-west-2"],
            [],
            "connection string and a service location"
        },
        {
            "ConflictingRegionAndServiceEndpoint",
            ["Region", "us-east-1", "ServiceEndpoint", "http://localhost:9324"],
            [],
            "multiple values among Service, Region, and ServiceEndpoint"
        },
        {
            "ConflictingServiceAndRegion",
            ["Service", "us-east-1", "Region", "us-west-2"],
            [],
            "multiple values among Service, Region, and ServiceEndpoint"
        },
        {
            "ConflictingServiceAndEndpoint",
            ["Service", "us-east-1", "Endpoint", "http://localhost:9324"],
            [],
            "multiple values among Service, Region, and ServiceEndpoint"
        },
        {
            "ConflictingAwsRegionVariables",
            [],
            ["AWS_REGION", "us-east-1", "AWS_DEFAULT_REGION", "us-west-2"],
            "AWS_REGION and AWS_DEFAULT_REGION"
        },
        {
            "FifoQueueNotBoolean",
            ["Region", "us-east-1", "FifoQueue", "sometimes"],
            [],
            "'FifoQueue' must be true or false"
        },
        {
            "PartitionCountNotInteger",
            ["Region", "us-east-1", "PartitionCount", "many"],
            [],
            "'PartitionCount' must be an integer"
        },
        {
            "PartitionCountNotPositive",
            ["Region", "us-east-1", "PartitionCount", "0"],
            [],
            "'PartitionCount' must be greater than zero"
        },
        {
            "CacheSizeNotInteger",
            ["Region", "us-east-1", "CacheSize", "large"],
            [],
            "'CacheSize' must be an integer"
        },
        {
            "CacheSizeNotPositive",
            ["Region", "us-east-1", "CacheSize", "-1"],
            [],
            "'CacheSize' must be greater than zero"
        },
        {
            "ReceiveWaitTimeSecondsNegative",
            ["Region", "us-east-1", "ReceiveWaitTimeSeconds", "-1"],
            [],
            "'ReceiveWaitTimeSeconds' must be between 0 and 20"
        },
        {
            "ReceiveWaitTimeSecondsAboveMaximum",
            ["Region", "us-east-1", "ReceiveWaitTimeSeconds", "21"],
            [],
            "'ReceiveWaitTimeSeconds' must be between 0 and 20"
        },
        {
            "VisibilityTimeoutSecondsNegative",
            ["Region", "us-east-1", "VisibilityTimeoutSeconds", "-1"],
            [],
            "'VisibilityTimeoutSeconds' must be between 0 and 43200"
        },
        {
            "VisibilityTimeoutSecondsAboveMaximum",
            ["Region", "us-east-1", "VisibilityTimeoutSeconds", "43201"],
            [],
            "'VisibilityTimeoutSeconds' must be between 0 and 43200"
        },
        {
            "ConflictingDataAdapterKeys",
            ["Region", "us-east-1", "DataAdapterKey", "primary", "DataAdapterServiceKey", "secondary"],
            [],
            "DataAdapterKey and DataAdapterServiceKey"
        },
        {
            "DataAdapterKeyMatchesProviderName",
            ["Region", "us-east-1", "DataAdapterKey", ProviderName],
            [],
            "DataAdapterKey must differ from the stream provider name"
        },
        {
            "ConnectionStringAndReference",
            ["ConnectionString", "Service=us-east-1", "ServiceKey", "shared-sqs"],
            ["ConnectionStrings:shared-sqs", "Service=us-west-2"],
            "both a connection reference and ConnectionString"
        },
        {
            "ConnectionReferenceNotFound",
            ["ServiceKey", "missing-sqs"],
            [],
            "connection reference 'missing-sqs' did not resolve"
        },
        {
            "ServiceEndpointNotAbsoluteHttp",
            ["ServiceEndpoint", "localhost:9324"],
            [],
            "ServiceEndpoint values must be absolute HTTP or HTTPS URIs"
        },
        {
            "ConnectionStringDuplicateService",
            ["ConnectionString", "Service=us-east-1;Service=us-west-2"],
            [],
            "property 'Service' is configured more than once"
        },
    };

    private static void ConfigureSilo(TestSiloBuilder builder, string providerName)
        => new SqsStreamProviderBuilder().Configure(
            builder,
            providerName,
            builder.Configuration.GetSection($"Orleans:Streaming:{providerName}"));

    private static void ConfigureClient(TestClientBuilder builder, string providerName)
        => new SqsStreamProviderBuilder().Configure(
            builder,
            providerName,
            builder.Configuration.GetSection($"Orleans:Streaming:{providerName}"));

    private static TestSiloBuilder CreateSiloBuilder(
        string providerName,
        (string Key, string? Value)[] providerValues,
        params (string Key, string? Value)[] rootValues)
        => new(CreateConfiguration(providerName, providerValues, rootValues));

    private static TestClientBuilder CreateClientBuilder(
        string providerName,
        (string Key, string? Value)[] providerValues,
        params (string Key, string? Value)[] rootValues)
        => new(CreateConfiguration(providerName, providerValues, rootValues));

    private static IConfigurationRoot CreateConfiguration(
        string providerName,
        IEnumerable<(string Key, string? Value)> providerValues,
        IEnumerable<(string Key, string? Value)> rootValues)
    {
        var values = providerValues
            .Select(pair => new KeyValuePair<string, string?>(
                $"Orleans:Streaming:{providerName}:{pair.Key}",
                pair.Value))
            .Concat(rootValues.Select(pair => new KeyValuePair<string, string?>(pair.Key, pair.Value)))
            .ToDictionary();
        values[$"Orleans:Streaming:{providerName}:ProviderType"] = "SQS";

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    private static (string Key, string? Value)[] ToPairs(string[] values)
    {
        Assert.Equal(0, values.Length % 2);
        return values
            .Chunk(2)
            .Select(pair => (pair[0], (string?)pair[1]))
            .ToArray();
    }

    private static TOptions GetOptions<TOptions>(IServiceProvider services, string providerName)
        where TOptions : class
        => services.GetRequiredService<IOptionsMonitor<TOptions>>().Get(providerName);

    private static HashRingBasedStreamQueueMapper CreateQueueMapper(IServiceProvider services, string providerName)
        => new(GetOptions<HashRingStreamQueueMapperOptions>(services, providerName), providerName);

    private static string[] GetPhysicalQueueNames(
        IEnumerable<QueueId> queueIds,
        SqsOptions options,
        string serviceId)
    {
        var storageType = typeof(SqsStreamProviderBuilder).Assembly.GetType(
            "OrleansAWSUtils.Storage.SQSStorage",
            throwOnError: true)!;
        var constructQueueName = storageType.GetMethod(
            "ConstructQueueName",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        return queueIds
            .Select(queueId => (string)constructQueueName.Invoke(null, [queueId.ToString(), options, serviceId])!)
            .Order()
            .ToArray();
    }

    private sealed class TestSiloBuilder(IConfiguration configuration) : ISiloBuilder
    {
        public IServiceCollection Services { get; } = CreateServices(configuration);

        public IConfiguration Configuration { get; } = configuration;
    }

    private sealed class TestClientBuilder(IConfiguration configuration) : IClientBuilder
    {
        public IServiceCollection Services { get; } = CreateServices(configuration);

        public IConfiguration Configuration { get; } = configuration;
    }

    private static ServiceCollection CreateServices(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddSingleton<IConfiguration>(configuration);
        return services;
    }

    private sealed class FakeSqsDataAdapter(string id) : ISQSDataAdapter
    {
        public string Id { get; } = id;

        public IBatchContainer FromQueueMessage(SqsMessage queueMessage, long sequenceId)
            => throw new NotSupportedException();

        public SqsMessage ToQueueMessage<T>(
            Orleans.Runtime.StreamId streamId,
            IEnumerable<T> events,
            StreamSequenceToken? token,
            Dictionary<string, object>? requestContext)
            => throw new NotSupportedException();
    }
}
