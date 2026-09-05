using System.Reflection;
using Amazon.Runtime;
using Amazon.SQS;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers;
using Orleans.Streaming.SQS.Streams;
using Orleans.Streams;
using OrleansAWSUtils.Storage;
using OrleansAWSUtils.Streams;
using SqsMessage = Amazon.SQS.Model.Message;
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
    public void SqsProvider_UsesAwsSdkV4()
        => Assert.Equal(4, typeof(AmazonSQSClient).Assembly.GetName().Version?.Major);

    [Fact]
    public async Task AspireAwsSdkResource_ConfiguresRegionWithoutCredentialMaterial()
    {
        await using var app = await SqsAspireTestApp.CreateAsync(
            ProviderName,
            [],
            awsProfile: "integration-profile",
            awsRegion: "ap-southeast-2");
        using var host = await app.BuildSiloHostAsync();
        var options = GetOptions<SqsOptions>(host.Services, ProviderName);

        Assert.Equal("Service=ap-southeast-2", options.ConnectionString);
        Assert.DoesNotContain("integration-profile", options.ConnectionString, StringComparison.Ordinal);
        Assert.DoesNotContain(
            typeof(SqsOptions).GetProperties(),
            property => property.Name.Contains("Credential", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("AccessKey", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("SecretKey", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Profile", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("ServiceKey")]
    [InlineData("ConnectionName")]
    public async Task AspireConnectionResource_ResolvesReferencedConnectionString(string referenceKey)
    {
        const string serviceKey = "shared-sqs";
        const string connectionString = "Service=http://localhost:9324";
        await using var app = await SqsAspireTestApp.CreateAsync(
            ProviderName,
            [(referenceKey, serviceKey)],
            [($"ConnectionStrings:{serviceKey}", connectionString)]);
        using var host = await app.BuildClientHostAsync();
        var options = GetOptions<SqsOptions>(host.Services, ProviderName);

        Assert.Equal(connectionString, options.ConnectionString);
        Assert.DoesNotContain(ProviderName, options.ConnectionString, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/queue/", options.ConnectionString, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AspireConnectionResource_EquivalentReferenceAliasesResolve()
    {
        const string serviceKey = "shared-sqs";
        const string connectionString = "Service=http://localhost:9324";
        await using var app = await SqsAspireTestApp.CreateAsync(
            ProviderName,
            [
                ("ServiceKey", serviceKey.ToUpperInvariant()),
                ("ConnectionName", serviceKey),
            ],
            [($"ConnectionStrings:{serviceKey}", connectionString)]);
        using var host = await app.BuildClientHostAsync();
        var options = GetOptions<SqsOptions>(host.Services, ProviderName);

        Assert.Equal(connectionString, options.ConnectionString);
    }

    [Fact]
    public async Task AspireConnectionResource_CustomEndpointPreservesExplicitCredentials()
    {
        const string connectionString =
            "Service=https://sqs.example.com;AccessKey=access;SecretKey=secret;SessionToken=token";
        await using var app = await SqsAspireTestApp.CreateAsync(
            ProviderName,
            [("ConnectionString", connectionString)]);
        using var host = await app.BuildClientHostAsync();
        var options = GetOptions<SqsOptions>(host.Services, ProviderName);

        Assert.Equal(connectionString, options.ConnectionString);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SqsStorage_CustomEndpointUsesExplicitCredentials(bool useSessionToken)
    {
        var connectionString = "Service=https://sqs.example.com;AccessKey=access;SecretKey=secret";
        if (useSessionToken)
        {
            connectionString += ";SessionToken=token";
        }

        var storage = new SQSStorage(
            NullLoggerFactory.Instance,
            "queue",
            new SqsOptions { ConnectionString = connectionString },
            "service");
        var client = Assert.IsType<AmazonSQSClient>(
            typeof(SQSStorage)
                .GetField("sqsClient", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(storage));
        var credentials = Assert.IsAssignableFrom<AWSCredentials>(
            typeof(AmazonServiceClient)
                .GetProperty("ExplicitAWSCredentials", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(client));
        var immutableCredentials = credentials.GetCredentials();

        Assert.Equal("access", immutableCredentials.AccessKey);
        Assert.Equal("secret", immutableCredentials.SecretKey);
        Assert.Equal(useSessionToken ? "token" : string.Empty, immutableCredentials.Token);
    }

    [Theory]
    [InlineData("DataAdapterServiceKey")]
    [InlineData("DataAdapterKey")]
    public async Task AspireGeneratedConfiguration_ResolvesKeyedDataAdapter(string adapterConfigurationKey)
    {
        const string adapterServiceKey = "custom-adapter";
        var defaultAdapter = new FakeSqsDataAdapter("default");
        var keyedAdapter = new FakeSqsDataAdapter("keyed");
        await using var app = await SqsAspireTestApp.CreateAsync(
            ProviderName,
            [(adapterConfigurationKey, adapterServiceKey)],
            awsRegion: "us-east-1");
        using var host = await app.BuildSiloHostAsync(services =>
        {
            services.AddSingleton<ISQSDataAdapter>(defaultAdapter);
            services.AddKeyedSingleton<ISQSDataAdapter>(adapterServiceKey, keyedAdapter);
        });
        var configuredAdapter = host.Services.GetRequiredKeyedService<ISQSDataAdapter>(ProviderName);
        var adapterFactory = SQSAdapterFactory.Create(host.Services, ProviderName);
        var factoryAdapter = typeof(SQSAdapterFactory)
            .GetField("dataAdapter", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(adapterFactory);

        Assert.Same(keyedAdapter, configuredAdapter);
        Assert.NotSame(defaultAdapter, configuredAdapter);
        Assert.Same(keyedAdapter, factoryAdapter);
    }

    [Fact]
    public async Task AspireGeneratedConfiguration_OrdersIndexedAttributeListsNumerically()
    {
        await using var app = await SqsAspireTestApp.CreateAsync(
            ProviderName,
            [
                ("Region", "us-east-1"),
                ("ReceiveMessageAttributes:0", "zero"),
                ("ReceiveMessageAttributes:1", "one"),
                ("ReceiveMessageAttributes:10", "ten"),
                ("ReceiveMessageAttributes:2", "two"),
                ("ReceiveMessageSystemAttributes:0", "system-zero"),
                ("ReceiveMessageSystemAttributes:1", "system-one"),
                ("ReceiveMessageSystemAttributes:10", "system-ten"),
                ("ReceiveMessageSystemAttributes:2", "system-two"),
            ]);
        using var host = await app.BuildSiloHostAsync();
        var options = GetOptions<SqsOptions>(host.Services, ProviderName);

        Assert.Equal(["zero", "one", "two", "ten"], options.ReceiveMessageAttributes);
        Assert.Equal(
            ["system-zero", "system-one", "system-two", "system-ten"],
            options.ReceiveMessageSystemAttributes);
    }

    [Theory]
    [MemberData(nameof(InvalidConfigurations))]
    public async Task AspireGeneratedConfiguration_InvalidSiloConfigurationThrowsActionableError(
        string caseId,
        string[] providerValues,
        string[] rootValues,
        string expectedMessage)
    {
        await using var app = await SqsAspireTestApp.CreateAsync(
            ProviderName,
            ToPairs(providerValues),
            ToPairs(rootValues));

        var exception = await Assert.ThrowsAsync<OrleansConfigurationException>(async () =>
        {
            using var host = await app.BuildSiloHostAsync();
            _ = GetOptions<SqsOptions>(host.Services, ProviderName);
        });

        Assert.NotEmpty(caseId);
        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AspireGeneratedConfiguration_MissingServiceLocationThrowsActionableError()
    {
        await using var app = await SqsAspireTestApp.CreateAsync(ProviderName, []);
        using var host = await app.BuildClientHostAsync();

        var exception = Assert.Throws<OrleansConfigurationException>(
            () => GetOptions<SqsOptions>(host.Services, ProviderName));

        Assert.Contains("SQS streaming", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("service location", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ServiceKey", exception.Message, StringComparison.Ordinal);
        Assert.Contains("AWS_REGION", exception.Message, StringComparison.Ordinal);
        Assert.Contains("AWS_DEFAULT_REGION", exception.Message, StringComparison.Ordinal);
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
            "ConnectionStringMissingSecretKey",
            ["ConnectionString", "Service=us-east-1;AccessKey=access"],
            [],
            "AccessKey and SecretKey together"
        },
        {
            "ConnectionStringMissingAccessKey",
            ["ConnectionString", "Service=us-east-1;SecretKey=secret"],
            [],
            "AccessKey and SecretKey together"
        },
        {
            "ConnectionStringSessionTokenWithoutCredentials",
            ["ConnectionString", "Service=us-east-1;SessionToken=token"],
            [],
            "SessionToken"
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
            "ConflictingServiceEndpointAliases",
            [
                "ServiceEndpoint", "http://localhost:9324",
                "Endpoint", "http://localhost:9325",
            ],
            [],
            "ServiceEndpoint and Endpoint"
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
            "ServiceNotAbsoluteHttp",
            ["Service", "localhost:9324"],
            [],
            "service endpoint values must be absolute HTTP or HTTPS URIs"
        },
        {
            "ConnectionStringServiceNotAbsoluteHttp",
            ["ConnectionString", "Service=localhost:9324"],
            [],
            "service endpoint values must be absolute HTTP or HTTPS URIs"
        },
        {
            "ConnectionStringDuplicateService",
            ["ConnectionString", "Service=us-east-1;Service=us-west-2"],
            [],
            "property 'Service' is configured more than once"
        },
    };

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
