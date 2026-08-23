// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#if NET10_0_OR_GREATER

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Orleans;
using Aspire.Hosting.Testing;
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
        });
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

    private static IConfiguration BuildConfig(Dictionary<string, string?> values)
    {
        var builder = DistributedApplicationTestingBuilder.Create();
        try
        {
            var provider = new TestKinesisProviderConfiguration(values);
            var orleans = builder.AddOrleans($"cluster-{Guid.NewGuid():N}")
                .WithDevelopmentClustering()
                .WithStreaming(ProviderName, provider);
            var silo = builder.AddContainer($"silo-{Guid.NewGuid():N}", "unused")
                .WithReference(orleans);

            using var services = builder.Services.BuildServiceProvider();
            return AspireResourceConfiguration.CreateAsync(
                    silo.Resource,
                    services,
                    include: static key =>
                        key.StartsWith("Orleans__Streaming__", StringComparison.Ordinal)
                        || key.StartsWith("AWS_", StringComparison.Ordinal)
                        || key.StartsWith("AWS__", StringComparison.Ordinal)
                        || key.StartsWith("ConnectionStrings__", StringComparison.Ordinal))
                .GetAwaiter()
                .GetResult();
        }
        finally
        {
            builder.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static IHost BuildSiloHost(IConfiguration config)
    {
        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Configuration.AddConfiguration(config);
        hostBuilder.UseOrleans(siloBuilder =>
        {
            siloBuilder.UseLocalhostClustering();
            siloBuilder.AddMemoryGrainStorage("PubSubStore");
        });
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

    private sealed class TestKinesisProviderConfiguration(
        IReadOnlyDictionary<string, string?> values) : IProviderConfiguration
    {
        private static readonly string ProviderPrefix = $"Orleans:Streaming:{ProviderName}:";

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

                var environmentKey = key.StartsWith(ProviderPrefix, StringComparison.Ordinal)
                    ? $"{sectionPrefix}__{key[ProviderPrefix.Length..].Replace(":", "__", StringComparison.Ordinal)}"
                    : key.Replace(":", "__", StringComparison.Ordinal);
                resourceBuilder.WithEnvironment(environmentKey, value);
            }
        }
    }
}

#endif
