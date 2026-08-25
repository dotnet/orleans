// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Orleans;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers;
using Orleans.Streaming.Kinesis;
using Orleans.Streams;
using TestExtensions;
using Xunit;

namespace Orleans.Streaming.Kinesis.Tests;

/// <summary>
/// Tests for the Aspire app-model integration surface of the Kinesis streaming provider.
/// Verifies that <see cref="KinesisStreamProviderBuilder"/> correctly wires silo and client
/// configuration from structured Aspire-style configuration keys.
/// </summary>
[TestSuite("BVT")]
[TestArea("Streaming")]
[TestProvider("Kinesis")]
[TestCategory("AWS"), TestCategory("Kinesis")]
[Collection(TestEnvironmentFixture.DefaultCollection)]
public sealed class KinesisAspireIntegrationTests
{
    private const string ProviderName = "orders-stream";

    [Fact]
    public void UseOrleans_SiloConfigFromStreamArn_PopulatesRegionAndStreamName()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            [$"Orleans:Streaming:{ProviderName}:ProviderType"] = "Kinesis",
            [$"Orleans:Streaming:{ProviderName}:ServiceKey"] = "kinesis-stream",
            [$"Orleans:Streaming:{ProviderName}:StreamArn"] = "arn:aws:kinesis:us-east-1:123456789012:stream/orders",
        });
        using var host = BuildSiloHost(config);
        var options = Resolve(host);

        Assert.Equal("orders", options.StreamName);
        Assert.Equal("us-east-1", options.Region);
    }

    [Fact]
    public void UseOrleans_SiloConfigFromStreamName_PopulatesStreamName()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            [$"Orleans:Streaming:{ProviderName}:ProviderType"] = "Kinesis",
            [$"Orleans:Streaming:{ProviderName}:StreamName"] = "explicit-stream",
            [$"Orleans:Streaming:{ProviderName}:Region"] = "eu-west-1",
        });
        using var host = BuildSiloHost(config);
        var options = Resolve(host);

        Assert.Equal("explicit-stream", options.StreamName);
        Assert.Equal("eu-west-1", options.Region);
    }

    [Fact]
    public void UseOrleans_ServiceKeyResolvesAWSResourceStreamArn()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            [$"Orleans:Streaming:{ProviderName}:ProviderType"] = "Kinesis",
            [$"Orleans:Streaming:{ProviderName}:ServiceKey"] = "my-kinesis",
            [$"AWS:Resources:my-kinesis:StreamArn"] = "arn:aws:kinesis:ap-southeast-2:111122223333:stream/events",
        });
        using var host = BuildSiloHost(config);
        var options = Resolve(host);

        Assert.Equal("events", options.StreamName);
        Assert.Equal("ap-southeast-2", options.Region);
    }

    [Fact]
    public void UseOrleans_ConnectionNameResolvesConnectionString()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            [$"Orleans:Streaming:{ProviderName}:ProviderType"] = "Kinesis",
            [$"Orleans:Streaming:{ProviderName}:ConnectionName"] = "kinesis-conn",
            [$"Orleans:Streaming:{ProviderName}:StreamName"] = "conn-stream",
            [$"ConnectionStrings:kinesis-conn"] = "http://localhost:4566;testkey;testsecret;us-east-1",
        });
        using var host = BuildSiloHost(config);
        var options = Resolve(host);

        Assert.Equal("conn-stream", options.StreamName);
        Assert.Equal("http://localhost:4566", options.Service);
        Assert.Equal("us-east-1", options.Region);
        Assert.Equal("testkey", options.AccessKey);
        Assert.Equal("testsecret", options.SecretKey);
    }

    [Fact]
    public void UseOrleans_DirectConnectionStringValue_PopulatesAllComponents()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            [$"Orleans:Streaming:{ProviderName}:ProviderType"] = "Kinesis",
            [$"Orleans:Streaming:{ProviderName}:ConnectionString"] = "http://localhost:4566;AKID;SKval;ap-south-1",
            [$"Orleans:Streaming:{ProviderName}:StreamName"] = "direct-stream",
            ["AWS_ENDPOINT_URL_KINESIS"] = "http://localhost:9999",
            ["AWS_REGION"] = "us-east-1",
        });
        using var host = BuildSiloHost(config);
        var options = Resolve(host);

        Assert.Equal("direct-stream", options.StreamName);
        Assert.Equal("http://localhost:4566", options.Service);
        Assert.Equal("ap-south-1", options.Region);
        Assert.Equal("AKID", options.AccessKey);
        Assert.Equal("SKval", options.SecretKey);
    }

    [Fact]
    public void UseOrleans_MissingConnectionName_Throws()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            [$"Orleans:Streaming:{ProviderName}:ProviderType"] = "Kinesis",
            [$"Orleans:Streaming:{ProviderName}:ConnectionName"] = "nonexistent",
            [$"Orleans:Streaming:{ProviderName}:StreamName"] = "some-stream",
        });
        using var host = BuildSiloHost(config);
        var ex = Assert.Throws<OrleansConfigurationException>(() => Resolve(host));
        Assert.Contains("nonexistent", ex.Message);
        Assert.Contains(ProviderName, ex.Message);
    }

    [Theory]
    [InlineData("AWS:Region")]
    [InlineData("AWS_REGION")]
    public void UseOrleans_AwsRegionFallback_PopulatesRegion(string configurationKey)
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            [$"Orleans:Streaming:{ProviderName}:ProviderType"] = "Kinesis",
            [$"Orleans:Streaming:{ProviderName}:StreamName"] = "env-stream",
            [configurationKey] = "sa-east-1",
        });
        using var host = BuildSiloHost(config);
        var options = Resolve(host);

        Assert.Equal("env-stream", options.StreamName);
        Assert.Equal("sa-east-1", options.Region);
    }

    [Fact]
    public void UseOrleans_AwsEndpointUrlKinesisEnvFallback_PopulatesService()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            [$"Orleans:Streaming:{ProviderName}:ProviderType"] = "Kinesis",
            [$"Orleans:Streaming:{ProviderName}:StreamName"] = "ep-stream",
            [$"Orleans:Streaming:{ProviderName}:Region"] = "us-east-1",
            ["AWS_ENDPOINT_URL_KINESIS"] = "http://localstack:4566",
        });
        using var host = BuildSiloHost(config);
        var options = Resolve(host);

        Assert.Equal("http://localstack:4566", options.Service);
        Assert.Equal("ep-stream", options.StreamName);
    }

    [Fact]
    public void UseOrleans_MissingStreamNameAndArn_Throws()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            [$"Orleans:Streaming:{ProviderName}:ProviderType"] = "Kinesis",
            [$"Orleans:Streaming:{ProviderName}:Region"] = "us-east-1",
        });
        using var host = BuildSiloHost(config);
        var ex = Assert.Throws<OrleansConfigurationException>(() => Resolve(host));
        Assert.Contains(ProviderName, ex.Message);
        Assert.Contains("StreamName", ex.Message);
    }

    [Fact]
    public void UseOrleans_InvalidStreamArn_Throws()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            [$"Orleans:Streaming:{ProviderName}:ProviderType"] = "Kinesis",
            [$"Orleans:Streaming:{ProviderName}:StreamArn"] = "arn:aws:sqs:us-east-1:123456:queue/bad",
        });
        using var host = BuildSiloHost(config);
        var ex = Assert.Throws<OrleansConfigurationException>(() => Resolve(host));
        Assert.Contains(ProviderName, ex.Message);
        Assert.Contains("StreamArn", ex.Message);
    }

    [Fact]
    public void UseOrleans_ClientConfigFromStreamArn_PopulatesOptions()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            [$"Orleans:Streaming:{ProviderName}:ProviderType"] = "Kinesis",
            [$"Orleans:Streaming:{ProviderName}:StreamArn"] = "arn:aws:kinesis:us-west-2:999888777666:stream/client-orders",
        }, isClient: true);
        using var host = BuildClientHost(config);
        var options = Resolve(host);

        Assert.Equal("client-orders", options.StreamName);
        Assert.Equal("us-west-2", options.Region);
    }

    [Fact]
    public void UseOrleans_SiloDefaultCheckpointerIsGrain()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            [$"Orleans:Streaming:{ProviderName}:ProviderType"] = "Kinesis",
            [$"Orleans:Streaming:{ProviderName}:StreamName"] = "ckpt-stream",
            [$"Orleans:Streaming:{ProviderName}:Region"] = "us-east-1",
        });
        using var host = BuildSiloHost(config);

        Assert.IsType<GrainStreamQueueCheckpointerFactory>(
            host.Services.GetRequiredKeyedService<IStreamQueueCheckpointerFactory>(ProviderName));
        var grainOptions = host.Services
            .GetRequiredService<IOptionsMonitor<GrainStreamQueueCheckpointerOptions>>()
            .Get(ProviderName);
        Assert.Same(StreamCheckpointComparers.Numeric, grainOptions.CheckpointComparer);
        Assert.Equal("PubSubStore", grainOptions.StorageProviderName);
    }

    [Fact]
    public void UseOrleans_DynamoDBCheckpointerFromConfig()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            [$"Orleans:Streaming:{ProviderName}:ProviderType"] = "Kinesis",
            [$"Orleans:Streaming:{ProviderName}:StreamName"] = "dynamo-stream",
            [$"Orleans:Streaming:{ProviderName}:Region"] = "us-east-1",
            [$"Orleans:Streaming:{ProviderName}:Checkpoint:Type"] = "DynamoDB",
            [$"Orleans:Streaming:{ProviderName}:Checkpoint:Service"] = "us-west-2",
            [$"Orleans:Streaming:{ProviderName}:Checkpoint:TableName"] = "MyCheckpoints",
            [$"Orleans:Streaming:{ProviderName}:Checkpoint:PersistInterval"] = "00:00:10",
        });
        using var host = BuildSiloHost(config);

        Assert.IsType<DynamoDBStreamQueueCheckpointerFactory>(
            host.Services.GetRequiredKeyedService<IStreamQueueCheckpointerFactory>(ProviderName));
        var dynamoOptions = host.Services
            .GetRequiredService<IOptionsMonitor<DynamoDBStreamQueueCheckpointerOptions>>()
            .Get(ProviderName);
        Assert.Equal("us-west-2", dynamoOptions.Service);
        Assert.Equal("MyCheckpoints", dynamoOptions.TableName);
        Assert.Equal(TimeSpan.FromSeconds(10), dynamoOptions.PersistInterval);
    }

    [Fact]
    public void UseOrleans_DynamoDBCheckpointerUsesAwsRegionFallback()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            [$"Orleans:Streaming:{ProviderName}:ProviderType"] = "Kinesis",
            [$"Orleans:Streaming:{ProviderName}:StreamName"] = "dynamo-stream",
            [$"Orleans:Streaming:{ProviderName}:Region"] = "us-east-1",
            [$"Orleans:Streaming:{ProviderName}:Checkpoint:Type"] = "DynamoDB",
            [$"Orleans:Streaming:{ProviderName}:Checkpoint:TableName"] = "MyCheckpoints",
            ["AWS:Region"] = "eu-west-1",
        });
        using var host = BuildSiloHost(config);
        var dynamoOptions = host.Services
            .GetRequiredService<IOptionsMonitor<DynamoDBStreamQueueCheckpointerOptions>>()
            .Get(ProviderName);

        Assert.Equal("eu-west-1", dynamoOptions.Service);
    }

    [Fact]
    public void UseOrleans_UnsupportedCheckpointerType_Throws()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            [$"Orleans:Streaming:{ProviderName}:ProviderType"] = "Kinesis",
            [$"Orleans:Streaming:{ProviderName}:StreamName"] = "bad-ckpt",
            [$"Orleans:Streaming:{ProviderName}:Region"] = "us-east-1",
            [$"Orleans:Streaming:{ProviderName}:Checkpoint:Type"] = "Redis",
        });
        var ex = Assert.Throws<OrleansConfigurationException>(() => BuildSiloHost(config));
        Assert.Contains("Redis", ex.Message);
        Assert.Contains(ProviderName, ex.Message);
    }

    [Fact]
    public void UseOrleans_GrainCheckpointerPersistIntervalFromConfig()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            [$"Orleans:Streaming:{ProviderName}:ProviderType"] = "Kinesis",
            [$"Orleans:Streaming:{ProviderName}:StreamName"] = "grain-ckpt",
            [$"Orleans:Streaming:{ProviderName}:Region"] = "us-east-1",
            [$"Orleans:Streaming:{ProviderName}:Checkpoint:Type"] = "Grain",
            [$"Orleans:Streaming:{ProviderName}:Checkpoint:PersistInterval"] = "00:00:30",
        });
        using var host = BuildSiloHost(config);
        var grainOptions = host.Services
            .GetRequiredService<IOptionsMonitor<GrainStreamQueueCheckpointerOptions>>()
            .Get(ProviderName);

        Assert.Equal(TimeSpan.FromSeconds(30), grainOptions.PersistInterval);
        Assert.Same(StreamCheckpointComparers.Numeric, grainOptions.CheckpointComparer);
    }

    [Fact]
    public void RegisterProviderAttribute_Present_ForKinesisAlias()
    {
        var assembly = typeof(KinesisAdapterFactory).Assembly;
        var attrs = assembly.GetCustomAttributes(typeof(RegisterProviderAttribute), false)
            .Cast<RegisterProviderAttribute>()
            .ToList();

        Assert.Single(attrs, a => a.Name == "Kinesis" && a.Target == "Silo" && a.Kind == "Streaming");
        Assert.Single(attrs, a => a.Name == "Kinesis" && a.Target == "Client" && a.Kind == "Streaming");
    }

    [Fact]
    public void RegisterProviderAttribute_StableAliases()
    {
        var assembly = typeof(KinesisAdapterFactory).Assembly;
        var streamingAliases = assembly.GetCustomAttributes(typeof(RegisterProviderAttribute), false)
            .Cast<RegisterProviderAttribute>()
            .Where(a => a.Kind == "Streaming" && a.Target == "Silo")
            .Select(a => a.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Contains("AmazonKinesis", streamingAliases);
        Assert.Contains("KinesisStream", streamingAliases);
        Assert.Contains("Kinesis", streamingAliases);
        Assert.Equal(3, streamingAliases.Count);
    }

    [Theory]
    [InlineData("Kinesis")]
    [InlineData("AmazonKinesis")]
    [InlineData("KinesisStream")]
    public void UseOrleans_AllProviderAliasesResolve(string providerType)
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            [$"Orleans:Streaming:{ProviderName}:ProviderType"] = providerType,
            [$"Orleans:Streaming:{ProviderName}:StreamName"] = "alias-stream",
            [$"Orleans:Streaming:{ProviderName}:Region"] = "us-east-1",
        });
        using var host = BuildSiloHost(config);
        var options = Resolve(host);

        Assert.Equal("alias-stream", options.StreamName);
        Assert.Equal("us-east-1", options.Region);
    }

    [Fact]
    public void UseOrleans_ExplicitProviderTypeOverride_UsesSpecifiedAlias()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            [$"Orleans:Streaming:{ProviderName}:ProviderType"] = "AmazonKinesis",
            [$"Orleans:Streaming:{ProviderName}:StreamName"] = "override-stream",
            [$"Orleans:Streaming:{ProviderName}:Region"] = "eu-central-1",
        });
        using var host = BuildSiloHost(config);
        var options = Resolve(host);

        Assert.Equal("override-stream", options.StreamName);
        Assert.Equal("eu-central-1", options.Region);
        Assert.IsType<GrainStreamQueueCheckpointerFactory>(
            host.Services.GetRequiredKeyedService<IStreamQueueCheckpointerFactory>(ProviderName));
    }

    [Fact]
    public void UseOrleans_SecretKeyNotExposedInOptionsToString()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            [$"Orleans:Streaming:{ProviderName}:ProviderType"] = "Kinesis",
            [$"Orleans:Streaming:{ProviderName}:StreamName"] = "secret-stream",
            [$"Orleans:Streaming:{ProviderName}:ConnectionString"] = "http://localhost:4566;AKID;SuperSecret;us-east-1",
        });
        using var host = BuildSiloHost(config);
        var options = Resolve(host);

        Assert.Equal("SuperSecret", options.SecretKey);
        Assert.Equal("AKID", options.AccessKey);
        var formatted = options.ToString();
        Assert.DoesNotContain("SuperSecret", formatted);
    }

    [Fact]
    public void UseOrleans_DynamoDBCheckpointerServiceKeyResolvesTableName()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            [$"Orleans:Streaming:{ProviderName}:ProviderType"] = "Kinesis",
            [$"Orleans:Streaming:{ProviderName}:StreamName"] = "ckpt-svc",
            [$"Orleans:Streaming:{ProviderName}:Region"] = "us-east-1",
            [$"Orleans:Streaming:{ProviderName}:Checkpoint:Type"] = "DynamoDB",
            [$"Orleans:Streaming:{ProviderName}:Checkpoint:ServiceKey"] = "dynamo-table",
            [$"AWS:Resources:dynamo-table:TableName"] = "ResolvedCheckpoints",
        });
        using var host = BuildSiloHost(config);
        var dynamoOptions = host.Services
            .GetRequiredService<IOptionsMonitor<DynamoDBStreamQueueCheckpointerOptions>>()
            .Get(ProviderName);

        Assert.Equal("ResolvedCheckpoints", dynamoOptions.TableName);
    }

    [Fact]
    public void UseOrleans_DynamoDBCheckpointerServiceKeyWithMissingTableName_Throws()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            [$"Orleans:Streaming:{ProviderName}:ProviderType"] = "Kinesis",
            [$"Orleans:Streaming:{ProviderName}:StreamName"] = "ckpt-missing",
            [$"Orleans:Streaming:{ProviderName}:Region"] = "us-east-1",
            [$"Orleans:Streaming:{ProviderName}:Checkpoint:Type"] = "DynamoDB",
            [$"Orleans:Streaming:{ProviderName}:Checkpoint:ServiceKey"] = "missing-table",
        });
        using var host = BuildSiloHost(config);
        var ex = Assert.ThrowsAny<Exception>(() =>
            host.Services
                .GetRequiredService<IOptionsMonitor<DynamoDBStreamQueueCheckpointerOptions>>()
                .Get(ProviderName));
        Assert.Contains("missing-table", ex.ToString());
        Assert.Contains(ProviderName, ex.ToString());
    }

    [Fact]
    public async Task AspireAppModel_OfficialAwsGeneratedContract_IsReproducedWithCoreAspire()
    {
        await using var app = await KinesisAspireTestApp.CreateAsync();
        var topology = app.Topology;

        Assert.Equal("orders-kinesis", topology.StackName);
        Assert.Equal("orders-v1", topology.ClusterId);
        Assert.Equal("orders-service", topology.ServiceId);
        Assert.Equal("Orders", topology.ProviderName);
        Assert.Equal("us-west-2", topology.Region);
        Assert.Equal("orders-stream", topology.Stream.ResourceName);
        Assert.Equal("orleans-orders", topology.Stream.StreamName);
        Assert.Equal(4, topology.Stream.ShardCount);
        Assert.Equal("PROVISIONED", topology.Stream.CapacityMode);
        Assert.Equal(24, topology.Stream.RetentionHours);
        Assert.Equal("RETAIN", topology.Stream.RemovalPolicy);
        AssertTable(
            topology.PubSubTable,
            "orders-pubsub",
            "orleans-orders-pubsub",
            "GrainReference",
            "GrainType");
        AssertTable(
            topology.CheckpointTable,
            "orders-checkpoints",
            "orleans-orders-checkpoints",
            "CheckpointNamespace",
            "Partition");

        var stream = Assert.Single(app.Model.Resources.OfType<KinesisStreamResource>());
        var tables = app.Model.Resources
            .OfType<DynamoDbTableResource>()
            .OrderBy(resource => resource.Name, StringComparer.Ordinal)
            .ToArray();
        Assert.Same(topology.Stream, stream.Specification);
        Assert.Equal(["orders-checkpoints", "orders-pubsub"], tables.Select(resource => resource.Name));
        Assert.Same(topology.CheckpointTable, tables[0].Specification);
        Assert.Same(topology.PubSubTable, tables[1].Specification);

        var silo = await app.ResolveEnvironmentAsync(KinesisAspireResourceRole.Silo);
        var client = await app.ResolveEnvironmentAsync(KinesisAspireResourceRole.Client);
        AssertConfiguration(ExpectedSiloConfiguration(topology), silo);
        AssertConfiguration(ExpectedClientConfiguration(topology), client);
        Assert.DoesNotContain(
            client.Keys,
            key => key.Contains(":Checkpoint:", StringComparison.Ordinal)
                || key.StartsWith("Orleans:GrainStorage:", StringComparison.Ordinal)
                || key.Contains("orders-pubsub", StringComparison.Ordinal)
                || key.Contains("orders-checkpoints", StringComparison.Ordinal));

        string[] forbiddenIdentities =
        [
            "Aspire.Hosting.AWS",
            "CDK",
            "CloudFormation",
            "Lambda",
            "SSO",
        ];
        foreach (var resource in app.Model.Resources)
        {
            var identity = resource.GetType().AssemblyQualifiedName ?? resource.GetType().FullName ?? string.Empty;
            Assert.DoesNotContain(
                forbiddenIdentities,
                forbidden => identity.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task AspireGeneratedConfiguration_ActivatesKinesisProviderOnSilo()
    {
        await using var app = await KinesisAspireTestApp.CreateAsync();
        using var host = await app.CreateSiloHost();

        var options = host.Services
            .GetRequiredService<IOptionsMonitor<KinesisStreamOptions>>()
            .Get(app.ProviderName);
        Assert.Equal("orleans-orders", options.StreamName);
        Assert.Equal("us-west-2", options.Region);
        var environment = await app.ResolveEnvironmentAsync(KinesisAspireResourceRole.Silo);
        Assert.Equal(app.Topology.StreamArn, environment["AWS:Resources:orders-stream:StreamArn"]);

        Assert.NotNull(host.Services.GetRequiredKeyedService<IStreamProvider>(app.ProviderName));
        Assert.IsType<KinesisAdapterFactory>(
            host.Services.GetRequiredKeyedService<IQueueAdapterFactory>(app.ProviderName));
        Assert.IsType<DynamoDBStreamQueueCheckpointerFactory>(
            host.Services.GetRequiredKeyedService<IStreamQueueCheckpointerFactory>(app.ProviderName));

        var checkpoint = host.Services
            .GetRequiredService<IOptionsMonitor<DynamoDBStreamQueueCheckpointerOptions>>()
            .Get(app.ProviderName);
        Assert.Equal("orleans-orders-checkpoints", checkpoint.TableName);
        Assert.Equal("us-west-2", checkpoint.Service);
        Assert.False(checkpoint.CreateIfNotExists);
        Assert.False(checkpoint.UseProvisionedThroughput);

        var pubSub = host.Services
            .GetRequiredService<IOptionsMonitor<DynamoDBStorageOptions>>()
            .Get("PubSubStore");
        Assert.Equal("orleans-orders-pubsub", pubSub.TableName);
        Assert.Equal("orders-service", pubSub.ServiceId);
        Assert.False(pubSub.CreateIfNotExists);
        Assert.False(pubSub.UpdateIfExists);
        Assert.False(pubSub.UseProvisionedThroughput);
    }

    [Fact]
    public async Task AspireGeneratedConfiguration_ActivatesKinesisProviderOnClient()
    {
        await using var app = await KinesisAspireTestApp.CreateAsync();
        using var host = await app.CreateClientHost();

        var options = host.Services
            .GetRequiredService<IOptionsMonitor<KinesisStreamOptions>>()
            .Get(app.ProviderName);
        Assert.Equal("orleans-orders", options.StreamName);
        Assert.Equal("us-west-2", options.Region);
        Assert.NotNull(host.Services.GetRequiredKeyedService<IStreamProvider>(app.ProviderName));
        Assert.IsType<KinesisAdapterFactory>(
            host.Services.GetRequiredKeyedService<IQueueAdapterFactory>(app.ProviderName));
        Assert.Null(host.Services.GetKeyedService<IStreamQueueCheckpointerFactory>(app.ProviderName));

        var environment = await app.ResolveEnvironmentAsync(KinesisAspireResourceRole.Client);
        AssertConfiguration(ExpectedClientConfiguration(app.Topology), environment);
        Assert.DoesNotContain(environment.Keys, key => key.Contains("Checkpoint", StringComparison.Ordinal));
        Assert.DoesNotContain(environment.Keys, key => key.Contains("PubSub", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StreamingEnvironmentScope_PreservesTopologyAndAwsConfiguration()
    {
        await using var app = await KinesisAspireTestApp.CreateAsync();
        var generated = await app.GetSiloEnvironmentAsync();
        const string absentKey = "AWS__Profile";
        var sentinelValues = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["AWS_REGION"] = "sentinel-region-\u2603",
            ["Orleans__ClusterId"] = "sentinel-cluster-\u2602-value",
            [absentKey] = null,
        };
        var originalValues = sentinelValues.Keys.ToDictionary(
            key => key,
            Environment.GetEnvironmentVariable,
            StringComparer.Ordinal);

        try
        {
            SetEnvironment(sentinelValues);
            using (await app.CreateEnvironmentScopeAsync(KinesisAspireResourceRole.Silo))
            {
                AssertActiveEnvironment(generated);
            }

            AssertEnvironment(sentinelValues);

            SetEnvironment(sentinelValues);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                using var scope = await app.CreateEnvironmentScopeAsync(KinesisAspireResourceRole.Silo);
                AssertActiveEnvironment(generated);
                throw new InvalidOperationException("intentional scope failure");
            });
            Assert.Equal("intentional scope failure", exception.Message);
            AssertEnvironment(sentinelValues);

            var completeGenerated = await app.GetSiloEnvironmentIncludingClusteringAsync();
            string[] ambientAwsVariables =
            [
                "AWS_REGION",
                "AWS_DEFAULT_REGION",
                "AWS_PROFILE",
                "AWS__Region",
                "AWS__Profile",
                "AWS_ENDPOINT_URL_KINESIS",
                "AWS_ENDPOINT_URL_DYNAMODB",
                "AWS_ACCESS_KEY_ID",
                "AWS_SECRET_ACCESS_KEY",
                "AWS_SESSION_TOKEN",
            ];
            var touchedKeys = completeGenerated.Keys
                .Concat(ambientAwsVariables)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var completeOriginalValues = touchedKeys.ToDictionary(
                key => key,
                Environment.GetEnvironmentVariable,
                StringComparer.Ordinal);
            var completeSeedValues = touchedKeys.ToDictionary(
                key => key,
                key => key is "AWS_ENDPOINT_URL_DYNAMODB" or "AWS_SECRET_ACCESS_KEY"
                    ? null
                    : $"sentinel-{key}-\u2603",
                StringComparer.Ordinal);
            var completeActiveValues = touchedKeys.ToDictionary(
                key => key,
                key => completeGenerated.TryGetValue(key, out var value) ? value : null,
                StringComparer.Ordinal);

            try
            {
                SetEnvironment(completeSeedValues);
                using (await app.CreateEnvironmentScopeAsync(KinesisAspireResourceRole.Silo))
                {
                    AssertEnvironment(completeActiveValues);
                }

                AssertEnvironment(completeSeedValues);

                SetEnvironment(completeSeedValues);
                var completeBodyException = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                {
                    using var scope = await app.CreateEnvironmentScopeAsync(KinesisAspireResourceRole.Silo);
                    AssertEnvironment(completeActiveValues);
                    throw new InvalidOperationException("intentional complete-scope failure");
                });
                Assert.Equal("intentional complete-scope failure", completeBodyException.Message);
                AssertEnvironment(completeSeedValues);

                SetEnvironment(completeSeedValues);
                var constructionException = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    app.CreateSiloHost(_ =>
                        throw new InvalidOperationException("intentional host-construction failure")));
                Assert.Equal("intentional host-construction failure", constructionException.Message);
                AssertEnvironment(completeSeedValues);
            }
            finally
            {
                SetEnvironment(completeOriginalValues);
            }
        }
        finally
        {
            SetEnvironment(originalValues);
        }
    }

    [Fact]
    public void Assembly_RegistersStableKinesisAliasesForSiloAndClient()
    {
        var aliases = typeof(KinesisAdapterFactory).Assembly
            .GetCustomAttributes(typeof(RegisterProviderAttribute), false)
            .Cast<RegisterProviderAttribute>()
            .Where(attribute => attribute.Kind == "Streaming")
            .GroupBy(attribute => attribute.Target, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(attribute => attribute.Name).Order(StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);
        string[] expected = ["AmazonKinesis", "Kinesis", "KinesisStream"];

        Assert.Equal(2, aliases.Count);
        Assert.Equal(expected, aliases["Silo"]);
        Assert.Equal(expected, aliases["Client"]);
        Assert.Equal(3, aliases["Silo"].Length);
        Assert.Equal(3, aliases["Client"].Length);
    }

    [Fact]
    public async Task AspireGeneratedTopology_PreservesPhysicalKinesisAndDynamoDbIdentity()
    {
        await using var app = await KinesisAspireTestApp.CreateAsync();
        var topology = app.Topology;
        var siloEnvironment = await app.ResolveEnvironmentAsync(KinesisAspireResourceRole.Silo);
        using var silo = await app.CreateSiloHost();
        using var client = await app.CreateClientHost();

        Assert.Equal("orders-stream", topology.Stream.ResourceName);
        Assert.Equal("orleans-orders", topology.Stream.StreamName);
        Assert.Equal(
            "arn:aws:kinesis:us-west-2:<account>:stream/orleans-orders",
            topology.StreamArn);
        Assert.Equal(topology.StreamArn, siloEnvironment["AWS:Resources:orders-stream:StreamArn"]);
        var siloOptions = silo.Services
            .GetRequiredService<IOptionsMonitor<KinesisStreamOptions>>()
            .Get(topology.ProviderName);
        var clientOptions = client.Services
            .GetRequiredService<IOptionsMonitor<KinesisStreamOptions>>()
            .Get(topology.ProviderName);
        Assert.Equal("orleans-orders", siloOptions.StreamName);
        Assert.Equal("orleans-orders", clientOptions.StreamName);
        Assert.Equal("us-west-2", siloOptions.Region);
        Assert.Equal("us-west-2", clientOptions.Region);

        AssertTable(
            topology.PubSubTable,
            "orders-pubsub",
            "orleans-orders-pubsub",
            "GrainReference",
            "GrainType");
        AssertTable(
            topology.CheckpointTable,
            "orders-checkpoints",
            "orleans-orders-checkpoints",
            "CheckpointNamespace",
            "Partition");
        Assert.NotEqual(topology.PubSubTable.ResourceName, topology.CheckpointTable.ResourceName);
        Assert.NotEqual(topology.PubSubTable.TableName, topology.CheckpointTable.TableName);
        Assert.Equal(
            "orleans-orders-pubsub",
            siloEnvironment["AWS:Resources:orders-pubsub:TableName"]);
        Assert.Equal(
            "orleans-orders-checkpoints",
            siloEnvironment["AWS:Resources:orders-checkpoints:TableName"]);

        var pubSub = silo.Services
            .GetRequiredService<IOptionsMonitor<DynamoDBStorageOptions>>()
            .Get("PubSubStore");
        var checkpoint = silo.Services
            .GetRequiredService<IOptionsMonitor<DynamoDBStreamQueueCheckpointerOptions>>()
            .Get(topology.ProviderName);
        Assert.Equal(topology.PubSubTable.TableName, pubSub.TableName);
        Assert.Equal(topology.ServiceId, pubSub.ServiceId);
        Assert.False(pubSub.CreateIfNotExists);
        Assert.False(pubSub.UpdateIfExists);
        Assert.False(pubSub.UseProvisionedThroughput);
        Assert.Equal(topology.CheckpointTable.TableName, checkpoint.TableName);
        Assert.Equal(topology.Region, checkpoint.Service);
        Assert.False(checkpoint.CreateIfNotExists);
        Assert.False(checkpoint.UseProvisionedThroughput);
    }

    [Fact]
    public async Task AspirePublishConfiguration_PreservesProviderLocalAwsIdentity()
    {
        await using var app = await KinesisAspireTestApp.CreateAsync();
        var configuration = await app.ResolveEnvironmentAsync(
            KinesisAspireResourceRole.Silo,
            DistributedApplicationOperation.Publish);

        Assert.DoesNotContain("AWS_PROFILE", configuration.Keys);
        Assert.DoesNotContain("AWS_REGION", configuration.Keys);
        Assert.DoesNotContain("AWS:Profile", configuration.Keys);
        Assert.DoesNotContain("AWS:Region", configuration.Keys);
        Assert.Equal("us-west-2", configuration["Orleans:Streaming:Orders:Region"]);
        Assert.Equal("us-west-2", configuration["Orleans:Streaming:Orders:Checkpoint:Region"]);
        Assert.Equal("orders-service", configuration["Orleans:GrainStorage:PubSubStore:ServiceId"]);
    }

    [Fact]
    public async Task AspireAwsIntegration_UsesAwsSdkV4AndExpectedEnvironment()
    {
        await using var app = await KinesisAspireTestApp.CreateAsync();
        var silo = await app.ResolveEnvironmentAsync(KinesisAspireResourceRole.Silo);
        var client = await app.ResolveEnvironmentAsync(KinesisAspireResourceRole.Client);

        Assert.Equal(4, typeof(Amazon.Kinesis.IAmazonKinesis).Assembly.GetName().Version?.Major);
        Assert.Equal(4, typeof(Amazon.DynamoDBv2.IAmazonDynamoDB).Assembly.GetName().Version?.Major);
        foreach (var environment in new[] { silo, client })
        {
            Assert.Equal(environment["AWS:Profile"], environment["AWS_PROFILE"]);
            Assert.Equal(environment["AWS:Region"], environment["AWS_REGION"]);
            Assert.Equal("orders-profile", environment["AWS_PROFILE"]);
            Assert.Equal("us-west-2", environment["AWS_REGION"]);
            Assert.DoesNotContain(
                environment.Keys,
                key => key.Contains("AWS_ACCESS_KEY_ID", StringComparison.OrdinalIgnoreCase)
                    || key.Contains("AWS_SECRET_ACCESS_KEY", StringComparison.OrdinalIgnoreCase)
                    || key.Contains("AWS_SESSION_TOKEN", StringComparison.OrdinalIgnoreCase)
                    || key.Contains("ENDPOINT", StringComparison.OrdinalIgnoreCase)
                    || key.Contains("RESOURCE_URL", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Theory]
    [InlineData("MissingStreamIdentity")]
    [InlineData("MalformedStreamArn")]
    [InlineData("UnresolvedConnectionOrTableReference")]
    [InlineData("UnsupportedCheckpointType")]
    [InlineData("InvalidCreateIfNotExists")]
    [InlineData("ConflictingDirectAndReferenceInputs")]
    public void AspireGeneratedConfiguration_InvalidOrAmbiguousInput_HasDeterministicOutcome(
        string scenario)
    {
        const string provider = "Orders";
        switch (scenario)
        {
            case "MissingStreamIdentity":
            {
                var config = BuildConfig(new Dictionary<string, string?>
                {
                    [$"Orleans:Streaming:{provider}:ProviderType"] = "Kinesis",
                    [$"Orleans:Streaming:{provider}:Region"] = "us-west-2",
                }, providerName: provider);
                using var host = BuildSiloHost(config);
                var exception = Assert.Throws<OrleansConfigurationException>(() => Resolve(host, provider));
                Assert.Contains(provider, exception.Message);
                Assert.Contains("requires StreamName", exception.Message);
                Assert.Contains("StreamArn", exception.Message);
                break;
            }
            case "MalformedStreamArn":
            {
                const string invalidArn = "arn:aws:sqs:us-west-2:123456789012:queue/not-kinesis";
                var config = BuildConfig(new Dictionary<string, string?>
                {
                    [$"Orleans:Streaming:{provider}:ProviderType"] = "Kinesis",
                    [$"Orleans:Streaming:{provider}:StreamArn"] = invalidArn,
                }, providerName: provider);
                using var host = BuildSiloHost(config);
                var exception = Assert.Throws<OrleansConfigurationException>(() => Resolve(host, provider));
                Assert.Contains(provider, exception.Message);
                Assert.Contains(invalidArn, exception.Message);
                Assert.Contains("StreamArn", exception.Message);
                break;
            }
            case "UnresolvedConnectionOrTableReference":
            {
                const string unresolved = "missing-connection-or-table";
                var connectionConfig = BuildConfig(new Dictionary<string, string?>
                {
                    [$"Orleans:Streaming:{provider}:ProviderType"] = "Kinesis",
                    [$"Orleans:Streaming:{provider}:StreamName"] = "orleans-orders",
                    [$"Orleans:Streaming:{provider}:ConnectionName"] = unresolved,
                }, providerName: provider);
                using (var host = BuildSiloHost(connectionConfig))
                {
                    var exception = Assert.Throws<OrleansConfigurationException>(() => Resolve(host, provider));
                    Assert.Contains(provider, exception.Message);
                    Assert.Contains(unresolved, exception.Message);
                    Assert.Contains("connection string", exception.Message, StringComparison.OrdinalIgnoreCase);
                }

                var tableConfig = BuildConfig(new Dictionary<string, string?>
                {
                    [$"Orleans:Streaming:{provider}:ProviderType"] = "Kinesis",
                    [$"Orleans:Streaming:{provider}:StreamName"] = "orleans-orders",
                    [$"Orleans:Streaming:{provider}:Region"] = "us-west-2",
                    [$"Orleans:Streaming:{provider}:Checkpoint:Type"] = "DynamoDB",
                    [$"Orleans:Streaming:{provider}:Checkpoint:ServiceKey"] = unresolved,
                }, providerName: provider);
                using var tableHost = BuildSiloHost(tableConfig);
                var tableException = Assert.Throws<OrleansConfigurationException>(() =>
                    tableHost.Services
                        .GetRequiredService<IOptionsMonitor<DynamoDBStreamQueueCheckpointerOptions>>()
                        .Get(provider));
                Assert.Equal(
                    $"Kinesis stream provider '{provider}' references DynamoDB checkpoint resource '{unresolved}', " +
                    "but its AWS Aspire TableName output is missing.",
                    tableException.Message);
                break;
            }
            case "UnsupportedCheckpointType":
            {
                const string unsupported = "Redis";
                var config = BuildConfig(new Dictionary<string, string?>
                {
                    [$"Orleans:Streaming:{provider}:ProviderType"] = "Kinesis",
                    [$"Orleans:Streaming:{provider}:StreamName"] = "orleans-orders",
                    [$"Orleans:Streaming:{provider}:Region"] = "us-west-2",
                    [$"Orleans:Streaming:{provider}:Checkpoint:Type"] = unsupported,
                }, providerName: provider);
                var exception = Assert.Throws<OrleansConfigurationException>(() => BuildSiloHost(config));
                Assert.Contains(provider, exception.Message);
                Assert.Contains(unsupported, exception.Message);
                Assert.Contains("DynamoDB", exception.Message);
                break;
            }
            case "InvalidCreateIfNotExists":
            {
                const string invalidBoolean = "definitely-not-a-bool";
                var config = BuildConfig(new Dictionary<string, string?>
                {
                    [$"Orleans:Streaming:{provider}:ProviderType"] = "Kinesis",
                    [$"Orleans:Streaming:{provider}:StreamName"] = "orleans-orders",
                    [$"Orleans:Streaming:{provider}:Region"] = "us-west-2",
                    [$"Orleans:Streaming:{provider}:Checkpoint:Type"] = "DynamoDB",
                    [$"Orleans:Streaming:{provider}:Checkpoint:TableName"] = "orleans-orders-checkpoints",
                    [$"Orleans:Streaming:{provider}:Checkpoint:CreateIfNotExists"] = invalidBoolean,
                }, providerName: provider);
                using var host = BuildSiloHost(config);
                var exception = Assert.Throws<OrleansConfigurationException>(() =>
                    host.Services
                        .GetRequiredService<IOptionsMonitor<DynamoDBStreamQueueCheckpointerOptions>>()
                        .Get(provider));
                Assert.Contains(nameof(DynamoDBStreamQueueCheckpointerOptions.CreateIfNotExists), exception.Message);
                Assert.Contains(invalidBoolean, exception.Message);
                Assert.Contains(provider, exception.Message);
                break;
            }
            case "ConflictingDirectAndReferenceInputs":
            {
                var directConfig = BuildConfig(new Dictionary<string, string?>
                {
                    [$"Orleans:Streaming:{provider}:ProviderType"] = "Kinesis",
                    [$"Orleans:Streaming:{provider}:ServiceKey"] = "service-stream",
                    [$"Orleans:Streaming:{provider}:ResourceConfigSection"] = "AWS:Resources:section-stream",
                    [$"Orleans:Streaming:{provider}:StreamArn"] =
                        "arn:aws:kinesis:us-east-1:123456789012:stream/direct-arn-stream",
                    [$"Orleans:Streaming:{provider}:StreamName"] = "explicit-stream",
                    [$"Orleans:Streaming:{provider}:Region"] = "eu-central-1",
                    [$"Orleans:Streaming:{provider}:ConnectionString"] =
                        "http://direct:4566;direct-access;direct-secret;ap-south-1",
                    [$"Orleans:Streaming:{provider}:ConnectionName"] = "ignored-connection",
                    ["AWS:Resources:service-stream:StreamArn"] =
                        "arn:aws:kinesis:us-west-1:123456789012:stream/service-stream",
                    ["AWS:Resources:section-stream:StreamArn"] =
                        "arn:aws:kinesis:us-west-2:123456789012:stream/section-stream",
                    ["ConnectionStrings:ignored-connection"] =
                        "http://ignored:4566;ignored-access;ignored-secret;ca-central-1",
                }, providerName: provider);
                using (var host = BuildSiloHost(directConfig))
                {
                    var options = Resolve(host, provider);
                    Assert.Equal("explicit-stream", options.StreamName);
                    Assert.Equal("eu-central-1", options.Region);
                    Assert.Equal("http://direct:4566", options.Service);
                    Assert.Equal("direct-access", options.AccessKey);
                    Assert.Equal("direct-secret", options.SecretKey);
                }

                var sectionConfig = BuildConfig(new Dictionary<string, string?>
                {
                    [$"Orleans:Streaming:{provider}:ProviderType"] = "Kinesis",
                    [$"Orleans:Streaming:{provider}:ServiceKey"] = "service-stream",
                    [$"Orleans:Streaming:{provider}:ResourceConfigSection"] = "AWS:Resources:section-stream",
                    ["AWS:Resources:service-stream:StreamArn"] =
                        "arn:aws:kinesis:us-west-1:123456789012:stream/service-stream",
                    ["AWS:Resources:section-stream:StreamArn"] =
                        "arn:aws:kinesis:us-west-2:123456789012:stream/section-stream",
                }, providerName: provider);
                using (var host = BuildSiloHost(sectionConfig))
                {
                    var options = Resolve(host, provider);
                    Assert.Equal("section-stream", options.StreamName);
                    Assert.Equal("us-west-2", options.Region);
                }

                AssertRegionFallback(provider, "AWS:Region", "eu-west-1");
                AssertRegionFallback(provider, "AWS_REGION", "ap-northeast-1");
                AssertRegionFallback(provider, "AWS_DEFAULT_REGION", "sa-east-1");

                var endpointConfig = BuildConfig(new Dictionary<string, string?>
                {
                    [$"Orleans:Streaming:{provider}:ProviderType"] = "Kinesis",
                    [$"Orleans:Streaming:{provider}:StreamName"] = "orleans-orders",
                    [$"Orleans:Streaming:{provider}:Checkpoint:Type"] = "DynamoDB",
                    [$"Orleans:Streaming:{provider}:Checkpoint:TableName"] = "checkpoints",
                    ["AWS_ENDPOINT_URL_KINESIS"] = "http://kinesis-endpoint:4566",
                    ["AWS_ENDPOINT_URL_DYNAMODB"] = "http://dynamodb-endpoint:4566",
                    ["AWS:Region"] = "us-west-2",
                }, providerName: provider);
                using (var host = BuildSiloHost(endpointConfig))
                {
                    Assert.Equal("http://kinesis-endpoint:4566", Resolve(host, provider).Service);
                    var checkpoint = host.Services
                        .GetRequiredService<IOptionsMonitor<DynamoDBStreamQueueCheckpointerOptions>>()
                        .Get(provider);
                    Assert.Equal("http://dynamodb-endpoint:4566", checkpoint.Service);
                }

                var directArnConflictConfig = BuildConfig(new Dictionary<string, string?>
                {
                    [$"Orleans:Streaming:{provider}:ProviderType"] = "Kinesis",
                    [$"Orleans:Streaming:{provider}:ServiceKey"] = "referenced-stream",
                    [$"Orleans:Streaming:{provider}:StreamArn"] =
                        "arn:aws:kinesis:eu-north-1:123456789012:stream/direct-stream",
                    ["AWS:Resources:referenced-stream:StreamArn"] =
                        "arn:aws:kinesis:us-west-1:123456789012:stream/referenced-stream",
                }, providerName: provider);
                using (var host = BuildSiloHost(directArnConflictConfig))
                {
                    var options = Resolve(host, provider);
                    Assert.Equal("direct-stream", options.StreamName);
                    Assert.Equal("eu-north-1", options.Region);
                }

                var simultaneousRegionConfig = BuildConfig(new Dictionary<string, string?>
                {
                    [$"Orleans:Streaming:{provider}:ProviderType"] = "Kinesis",
                    [$"Orleans:Streaming:{provider}:StreamName"] = "region-precedence-stream",
                    [$"Orleans:Streaming:{provider}:Checkpoint:Type"] = "DynamoDB",
                    [$"Orleans:Streaming:{provider}:Checkpoint:TableName"] = "region-precedence-checkpoints",
                    ["AWS:Region"] = "eu-west-1",
                    ["AWS_REGION"] = "ap-northeast-1",
                    ["AWS_DEFAULT_REGION"] = "sa-east-1",
                }, providerName: provider);
                using (var host = BuildSiloHost(simultaneousRegionConfig))
                {
                    var options = Resolve(host, provider);
                    var checkpoint = host.Services
                        .GetRequiredService<IOptionsMonitor<DynamoDBStreamQueueCheckpointerOptions>>()
                        .Get(provider);
                    Assert.Equal("region-precedence-stream", options.StreamName);
                    Assert.Equal("eu-west-1", options.Region);
                    Assert.Equal("region-precedence-checkpoints", checkpoint.TableName);
                    Assert.Equal("eu-west-1", checkpoint.Service);
                }

                string[] malformedArns =
                [
                    "arn:aws:sqs:us-west-2:123456789012:stream/wrong-service",
                    "arn:aws:kinesis::123456789012:stream/missing-region",
                    "arn:aws:kinesis:us-west-2:123456789012:streams/wrong-prefix",
                    "arn:aws:kinesis:us-west-2:123456789012:stream/",
                    "arn:aws:kinesis:us-west-2:123456789012:stream",
                ];
                foreach (var malformedArn in malformedArns)
                {
                    var malformedConfig = BuildConfig(new Dictionary<string, string?>
                    {
                        [$"Orleans:Streaming:{provider}:ProviderType"] = "Kinesis",
                        [$"Orleans:Streaming:{provider}:StreamArn"] = malformedArn,
                    }, providerName: provider);
                    using var malformedHost = BuildSiloHost(malformedConfig);
                    var malformedException = Assert.Throws<OrleansConfigurationException>(
                        () => Resolve(malformedHost, provider));
                    Assert.Equal(
                        $"Kinesis stream provider '{provider}' has invalid StreamArn '{malformedArn}'.",
                        malformedException.Message);
                }

                var duplicateKeyException = Assert.Throws<InvalidOperationException>(() =>
                    KinesisAspireTestApp.NormalizeConfiguration(new Dictionary<string, string?>
                    {
                        ["AWS__Region"] = "us-west-2",
                        ["AWS:Region"] = "eu-west-1",
                    }));
                Assert.Equal(
                    "Environment keys normalize to duplicate configuration key 'AWS:Region'.",
                    duplicateKeyException.Message);

                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown scenario.");
        }
    }

    [Fact]
    public async Task AspireGeneratedConfiguration_DoesNotExposeSecrets()
    {
        const string accessSentinel = "seeded-access-sentinel-7F2A";
        const string secretSentinel = "seeded-secret-sentinel-9C4B";
        const string tokenSentinel = "seeded-token-sentinel-1D8E";
        var seeded = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["AWS_ACCESS_KEY_ID"] = accessSentinel,
            ["AWS_SECRET_ACCESS_KEY"] = secretSentinel,
            ["AWS_SESSION_TOKEN"] = tokenSentinel,
        };
        var original = seeded.Keys.ToDictionary(
            key => key,
            Environment.GetEnvironmentVariable,
            StringComparer.Ordinal);

        try
        {
            SetEnvironment(seeded);
            await using var app = await KinesisAspireTestApp.CreateAsync();
            var silo = await app.ResolveEnvironmentAsync(KinesisAspireResourceRole.Silo);
            var client = await app.ResolveEnvironmentAsync(KinesisAspireResourceRole.Client);
            string[] forbiddenFragments =
            [
                "access_key",
                "access-key",
                "accesskey",
                "secret_key",
                "secret-key",
                "secretkey",
                "session_token",
                "session-token",
                "sessiontoken",
                "password",
                "credential",
                "resource_url",
                "resource-url",
                "endpoint",
            ];
            string[] sentinels = [accessSentinel, secretSentinel, tokenSentinel];

            foreach (var environment in new[] { silo, client })
            {
                foreach (var (key, value) in environment)
                {
                    Assert.DoesNotContain(
                        forbiddenFragments,
                        fragment => key.Contains(fragment, StringComparison.OrdinalIgnoreCase));
                    Assert.DoesNotContain(
                        forbiddenFragments,
                        fragment => value?.Contains(fragment, StringComparison.OrdinalIgnoreCase) == true);
                    Assert.DoesNotContain(
                        sentinels,
                        sentinel => value?.Contains(sentinel, StringComparison.Ordinal) == true);
                }
            }

            var missingConfig = BuildConfig(new Dictionary<string, string?>
            {
                ["Orleans:Streaming:Orders:ProviderType"] = "Kinesis",
                ["Orleans:Streaming:Orders:Region"] = "us-west-2",
            }, providerName: "Orders");
            using var host = BuildSiloHost(missingConfig);
            var exception = Assert.Throws<OrleansConfigurationException>(() => Resolve(host, "Orders"));
            Assert.DoesNotContain(sentinels, sentinel => exception.ToString().Contains(sentinel, StringComparison.Ordinal));

            const string invalidBoolean = "credential-path-invalid-boolean";
            var credentialFailureConfig = BuildConfig(new Dictionary<string, string?>
            {
                ["Orleans:Streaming:Orders:ProviderType"] = "Kinesis",
                ["Orleans:Streaming:Orders:StreamName"] = "credential-failure-stream",
                ["Orleans:Streaming:Orders:Region"] = "us-west-2",
                ["Orleans:Streaming:Orders:AccessKey"] = accessSentinel,
                ["Orleans:Streaming:Orders:SecretKey"] = secretSentinel,
                ["Orleans:Streaming:Orders:Checkpoint:Type"] = "DynamoDB",
                ["Orleans:Streaming:Orders:Checkpoint:TableName"] = "credential-failure-checkpoints",
                ["Orleans:Streaming:Orders:Checkpoint:Service"] = "us-west-2",
                ["Orleans:Streaming:Orders:Checkpoint:AccessKey"] = accessSentinel,
                ["Orleans:Streaming:Orders:Checkpoint:SecretKey"] = secretSentinel,
                ["Orleans:Streaming:Orders:Checkpoint:Token"] = tokenSentinel,
                ["Orleans:Streaming:Orders:Checkpoint:CreateIfNotExists"] = invalidBoolean,
            }, providerName: "Orders");
            using var credentialFailureHost = BuildSiloHost(credentialFailureConfig);
            var credentialOptions = Resolve(credentialFailureHost, "Orders");
            Assert.Equal(accessSentinel, credentialOptions.AccessKey);
            Assert.Equal(secretSentinel, credentialOptions.SecretKey);
            var credentialException = Assert.Throws<OrleansConfigurationException>(() =>
                credentialFailureHost.Services
                    .GetRequiredService<IOptionsMonitor<DynamoDBStreamQueueCheckpointerOptions>>()
                    .Get("Orders"));
            Assert.Equal(
                $"Kinesis stream provider 'Orders' has invalid DynamoDB checkpoint CreateIfNotExists value '{invalidBoolean}'.",
                credentialException.Message);
            var diagnosticOutput = $"{credentialException}{Environment.NewLine}{credentialOptions}";
            Assert.DoesNotContain(sentinels, sentinel =>
                diagnosticOutput.Contains(sentinel, StringComparison.Ordinal));
        }
        finally
        {
            SetEnvironment(original);
        }
    }

    private static IConfiguration BuildConfig(
        Dictionary<string, string?> values,
        bool isClient = false,
        string providerName = ProviderName)
    {
        var builder = KinesisAspireTestApp.CreateBuilder();
        try
        {
            var provider = new TestKinesisProviderConfiguration(values, providerName);
            var pubSubStore = new TestDynamoDBGrainStorageProviderConfiguration();
            var orleans = builder.AddOrleans($"cluster-{Guid.NewGuid():N}")
                .WithDevelopmentClustering()
                .WithGrainStorage("PubSubStore", pubSubStore)
                .WithStreaming(providerName, provider);
            var project = builder.AddContainer(
                $"{(isClient ? "client" : "silo")}-{Guid.NewGuid():N}",
                "unused");
            if (isClient)
            {
                project.WithReference(orleans.AsClient());
            }
            else
            {
                project.WithReference(orleans);
            }

            using var services = builder.Services.BuildServiceProvider();
            return CreateConfigurationAsync(project.Resource, services)
                .GetAwaiter()
                .GetResult();
        }
        finally
        {
            if (builder is IAsyncDisposable asyncDisposable)
            {
                asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            else if (builder is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    private static IHost BuildSiloHost(IConfiguration config)
    {
        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Configuration.AddConfiguration(config);
        hostBuilder.UseOrleans();
        return hostBuilder.Build();
    }

    private static IHost BuildClientHost(IConfiguration config)
    {
        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Configuration.AddConfiguration(config);
        hostBuilder.UseOrleansClient(clientBuilder =>
            clientBuilder.UseStaticClustering(
                new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 30000)));
        return hostBuilder.Build();
    }

    private static KinesisStreamOptions Resolve(IHost host)
        => host.Services.GetRequiredService<IOptionsMonitor<KinesisStreamOptions>>().Get(ProviderName);

    private static KinesisStreamOptions Resolve(IHost host, string providerName)
        => host.Services.GetRequiredService<IOptionsMonitor<KinesisStreamOptions>>().Get(providerName);

    private static async Task<IConfigurationRoot> CreateConfigurationAsync(
        IResource resource,
        IServiceProvider services)
    {
        var executionContext = new DistributedApplicationExecutionContext(
            new DistributedApplicationExecutionContextOptions(DistributedApplicationOperation.Run)
            {
                ServiceProvider = services,
            });
        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var annotation in resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            var annotationValues = new Dictionary<string, object>(StringComparer.Ordinal);
            await annotation.Callback(new EnvironmentCallbackContext(
                executionContext,
                resource,
                annotationValues));
            foreach (var (key, value) in annotationValues)
            {
                values.Add(key, value);
            }
        }

        var valueContext = new ValueProviderContext
        {
            Caller = resource,
            ExecutionContext = executionContext,
            Network = KnownNetworkIdentifiers.LocalhostNetwork,
        };
        var configuration = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in values)
        {
            if (!key.StartsWith("Orleans__Clustering__", StringComparison.Ordinal)
                && !key.StartsWith("Orleans__Streaming__", StringComparison.Ordinal)
                && !key.StartsWith("Orleans__GrainStorage__", StringComparison.Ordinal)
                && !key.StartsWith("AWS_", StringComparison.Ordinal)
                && !key.StartsWith("AWS__", StringComparison.Ordinal)
                && !key.StartsWith("ConnectionStrings__", StringComparison.Ordinal))
            {
                continue;
            }

            configuration[key.Replace("__", ":", StringComparison.Ordinal)] = value switch
            {
                null => string.Empty,
                IValueProvider provider => await provider.GetValueAsync(valueContext),
                _ => value.ToString(),
            };
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(configuration)
            .Build();
    }

    private static IReadOnlyDictionary<string, string?> ExpectedSiloConfiguration(
        KinesisAspireTopologySpecification topology)
        => new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Orleans:ClusterId"] = "orders-v1",
            ["Orleans:ServiceId"] = "orders-service",
            ["Orleans:Streaming:Orders:ProviderType"] = "Kinesis",
            ["Orleans:Streaming:Orders:ServiceKey"] = "orders-stream",
            ["Orleans:Streaming:Orders:StreamName"] = "orleans-orders",
            ["Orleans:Streaming:Orders:Region"] = "us-west-2",
            ["Orleans:Streaming:Orders:Checkpoint:Type"] = "DynamoDB",
            ["Orleans:Streaming:Orders:Checkpoint:ServiceKey"] = "orders-checkpoints",
            ["Orleans:Streaming:Orders:Checkpoint:Region"] = "us-west-2",
            ["Orleans:Streaming:Orders:Checkpoint:CreateIfNotExists"] = "false",
            ["Orleans:Streaming:Orders:Checkpoint:UseProvisionedThroughput"] = "false",
            ["Orleans:GrainStorage:PubSubStore:ProviderType"] = "DynamoDB",
            ["Orleans:GrainStorage:PubSubStore:ServiceKey"] = "orders-pubsub",
            ["Orleans:GrainStorage:PubSubStore:ServiceId"] = "orders-service",
            ["Orleans:GrainStorage:PubSubStore:UseProvisionedThroughput"] = "false",
            ["Orleans:GrainStorage:PubSubStore:CreateIfNotExists"] = "false",
            ["Orleans:GrainStorage:PubSubStore:UpdateIfExists"] = "false",
            ["AWS:Resources:orders-stream:StreamArn"] = topology.StreamArn,
            ["AWS:Resources:orders-pubsub:TableName"] = "orleans-orders-pubsub",
            ["AWS:Resources:orders-checkpoints:TableName"] = "orleans-orders-checkpoints",
            ["AWS_PROFILE"] = "orders-profile",
            ["AWS_REGION"] = "us-west-2",
            ["AWS:Profile"] = "orders-profile",
            ["AWS:Region"] = "us-west-2",
        };

    private static IReadOnlyDictionary<string, string?> ExpectedClientConfiguration(
        KinesisAspireTopologySpecification topology)
        => new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Orleans:ClusterId"] = "orders-v1",
            ["Orleans:ServiceId"] = "orders-service",
            ["Orleans:Streaming:Orders:ProviderType"] = "Kinesis",
            ["Orleans:Streaming:Orders:ServiceKey"] = "orders-stream",
            ["Orleans:Streaming:Orders:StreamName"] = "orleans-orders",
            ["Orleans:Streaming:Orders:Region"] = "us-west-2",
            ["AWS:Resources:orders-stream:StreamArn"] = topology.StreamArn,
            ["AWS_PROFILE"] = "orders-profile",
            ["AWS_REGION"] = "us-west-2",
            ["AWS:Profile"] = "orders-profile",
            ["AWS:Region"] = "us-west-2",
        };

    private static void AssertConfiguration(
        IReadOnlyDictionary<string, string?> expected,
        IReadOnlyDictionary<string, string?> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        foreach (var (key, expectedValue) in expected)
        {
            Assert.True(actual.TryGetValue(key, out var actualValue), $"Missing configuration key '{key}'.");
            Assert.Equal(expectedValue, actualValue);
        }
    }

    private static void AssertTable(
        DynamoDbTableSpecification table,
        string resourceName,
        string tableName,
        string partitionKey,
        string sortKey)
    {
        Assert.Equal(resourceName, table.ResourceName);
        Assert.Equal(tableName, table.TableName);
        Assert.Equal(new DynamoDbKeySpecification(partitionKey, "HASH", "S"), table.PartitionKey);
        Assert.Equal(new DynamoDbKeySpecification(sortKey, "RANGE", "S"), table.SortKey);
        Assert.Equal("PAY_PER_REQUEST", table.BillingMode);
        Assert.Equal("RETAIN", table.RemovalPolicy);
    }

    private static void AssertActiveEnvironment(IReadOnlyDictionary<string, string?> expected)
    {
        foreach (var (key, value) in expected)
        {
            Assert.Equal(value, Environment.GetEnvironmentVariable(key));
        }
    }

    private static void AssertEnvironment(IReadOnlyDictionary<string, string?> expected)
    {
        foreach (var (key, value) in expected)
        {
            Assert.Equal(value, Environment.GetEnvironmentVariable(key));
        }
    }

    private static void SetEnvironment(IReadOnlyDictionary<string, string?> values)
    {
        foreach (var (key, value) in values)
        {
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    private static void AssertRegionFallback(string providerName, string key, string region)
    {
        using var environment = new EnvironmentVariableScope(
            new Dictionary<string, string?>(StringComparer.Ordinal));
        var config = BuildConfig(new Dictionary<string, string?>
        {
            [$"Orleans:Streaming:{providerName}:ProviderType"] = "Kinesis",
            [$"Orleans:Streaming:{providerName}:StreamName"] = "fallback-stream",
            [key] = region,
        }, providerName: providerName);
        using var host = BuildSiloHost(config);
        var options = Resolve(host, providerName);
        Assert.Equal(region, options.Region);
        Assert.Equal("fallback-stream", options.StreamName);
    }

    private sealed class TestKinesisProviderConfiguration(
        IReadOnlyDictionary<string, string?> values,
        string providerName) : IProviderConfiguration
    {
        private readonly string _providerPrefix = $"Orleans:Streaming:{providerName}:";

        public void ConfigureResource<T>(
            IResourceBuilder<T> resourceBuilder,
            string configurationSectionPath)
            where T : IResourceWithEnvironment
        {
            var sectionPrefix = $"Orleans__{configurationSectionPath.Replace(":", "__", StringComparison.Ordinal)}";
            var providerType = values
                .FirstOrDefault(static pair => pair.Key.EndsWith(":ProviderType", StringComparison.Ordinal))
                .Value ?? "Kinesis";
            resourceBuilder.WithEnvironment($"{sectionPrefix}__ProviderType", providerType);

            foreach (var (key, value) in values)
            {
                if (key.EndsWith(":ProviderType", StringComparison.Ordinal))
                {
                    continue;
                }

                var environmentKey = key.StartsWith(_providerPrefix, StringComparison.Ordinal)
                    ? $"{sectionPrefix}__{key[_providerPrefix.Length..].Replace(":", "__", StringComparison.Ordinal)}"
                    : key.Replace(":", "__", StringComparison.Ordinal);
                resourceBuilder.WithEnvironment(environmentKey, value);
            }
        }
    }

    private sealed class TestDynamoDBGrainStorageProviderConfiguration : IProviderConfiguration
    {
        public void ConfigureResource<T>(
            IResourceBuilder<T> resourceBuilder,
            string configurationSectionPath)
            where T : IResourceWithEnvironment
        {
            var prefix = $"Orleans__{configurationSectionPath.Replace(":", "__", StringComparison.Ordinal)}";
            resourceBuilder
                .WithEnvironment($"{prefix}__ProviderType", "DynamoDB")
                .WithEnvironment($"{prefix}__Service", "us-east-1")
                .WithEnvironment($"{prefix}__TableName", "KinesisPubSubStore")
                .WithEnvironment($"{prefix}__UseProvisionedThroughput", "false")
                .WithEnvironment($"{prefix}__CreateIfNotExists", "true");
        }
    }
}
