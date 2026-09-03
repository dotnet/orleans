using System.Net;
using Amazon.DynamoDBv2.Model;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Orleans;
using Aspire.Hosting.Testing;
using AWSUtils.Tests.StorageTests;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.AWSUtils.Tests;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.Storage;
using TestExtensions;
using Xunit;

namespace AWSUtils.Tests.Configuration;

[TestSuite("Functional")]
[TestProvider("DynamoDB")]
[TestArea("Aspire")]
[TestCategory("AWS"), TestCategory("DynamoDB"), TestCategory("Aspire"), TestCategory("Functional")]
[Collection(TestEnvironmentFixture.DefaultCollection)]
public sealed class DynamoDBAspireLiveTests
{
    private const string StorageName = "LiveStore";
    private const string UnavailableSkipReason = "Unable to connect to AWS DynamoDB simulator";

    [Fact]
    public async Task GeneratedConfiguration_Clustering_ProvidesMembershipAndGateway()
    {
        var endpoint = GetDynamoDBLocalEndpointOrSkip();
        var suffix = Guid.NewGuid().ToString("N");
        var clusterId = $"phase4-cluster-{suffix}";
        var serviceId = $"phase4-service-{suffix}";
        var tableName = $"phase4-membership-{suffix}";
        IMembershipTable? membership = null;

        try
        {
            var generated = await CreateGeneratedConfigurationAsync(
                ProviderSurface.Clustering,
                endpoint,
                clusterId,
                serviceId,
                tableName);

            AssertGeneratedEndpoint(generated.Silo, endpoint);
            Assert.NotNull(generated.Client);
            AssertGeneratedEndpoint(generated.Client, endpoint);

            using var siloHost = BuildSiloHost(generated.Silo);
            using var clientHost = BuildClientHost(generated.Client);
            membership = siloHost.Services.GetRequiredService<IMembershipTable>();
            var gateway = clientHost.Services.GetRequiredService<IGatewayListProvider>();
            var membershipOptions = siloHost.Services.GetRequiredService<IOptions<DynamoDBClusteringOptions>>().Value;
            var gatewayOptions = clientHost.Services.GetRequiredService<IOptions<DynamoDBGatewayOptions>>().Value;

            Assert.Equal("DynamoDBMembershipTable", membership.GetType().Name);
            Assert.Equal("DynamoDBGatewayListProvider", gateway.GetType().Name);
            AssertProviderOptions(membershipOptions.Service, membershipOptions.TableName, membershipOptions.UseProvisionedThroughput, endpoint, tableName);
            AssertProviderOptions(gatewayOptions.Service, gatewayOptions.TableName, gatewayOptions.UseProvisionedThroughput, endpoint, tableName);

            await membership.InitializeMembershipTable(tryInitTableVersion: true);
            await gateway.InitializeGatewayListProvider();

            var initial = await membership.ReadAll();
            Assert.Empty(initial.Members);
            Assert.Equal(0, initial.Version.Version);

            const int siloPort = 11_111;
            const int gatewayPort = 30_000;
            const int generation = 7;
            var siloAddress = SiloAddress.New(IPAddress.Loopback, siloPort, generation);
            var startTime = new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);
            var entry = new MembershipEntry
            {
                HostName = "phase4-host",
                IAmAliveTime = startTime.AddMinutes(1),
                ProxyPort = gatewayPort,
                SiloAddress = siloAddress,
                SiloName = $"phase4-silo-{suffix}",
                StartTime = startTime,
                Status = SiloStatus.Active,
            };

            Assert.True(await membership.InsertRow(entry, initial.Version.Next()));

            var stored = await membership.ReadRow(siloAddress);
            var storedEntry = Assert.Single(stored.Members).Item1;
            Assert.Equal(entry.SiloName, storedEntry.SiloName);
            Assert.Equal(SiloStatus.Active, storedEntry.Status);
            Assert.Equal(gatewayPort, storedEntry.ProxyPort);

            var gateways = await gateway.GetGateways();
            var gatewayUri = Assert.Single(gateways);
            Assert.Equal(
                SiloAddress.New(IPAddress.Loopback, gatewayPort, generation).ToGatewayUri(),
                gatewayUri);
        }
        finally
        {
            if (membership is not null)
            {
                try
                {
                    await membership.DeleteMembershipTableEntries(clusterId);
                }
                catch (ResourceNotFoundException)
                {
                }
            }

            await DeleteTableIfExistsAsync(tableName, endpoint);
        }
    }

    [Fact]
    public async Task GeneratedConfiguration_NamedGrainStorage_WritesReadsAndClearsState()
    {
        var endpoint = GetDynamoDBLocalEndpointOrSkip();
        var suffix = Guid.NewGuid().ToString("N");
        var clusterId = $"phase4-cluster-{suffix}";
        var serviceId = $"phase4-service-{suffix}";
        var tableName = $"phase4-storage-{suffix}";

        try
        {
            var generated = await CreateGeneratedConfigurationAsync(
                ProviderSurface.GrainStorage,
                endpoint,
                clusterId,
                serviceId,
                tableName);

            AssertGeneratedEndpoint(generated.Silo, endpoint);

            using var host = BuildSiloHost(generated.Silo);
            var storage = host.Services.GetRequiredKeyedService<IGrainStorage>(StorageName);
            var options = host.Services
                .GetRequiredService<IOptionsMonitor<DynamoDBStorageOptions>>()
                .Get(StorageName);

            Assert.IsType<DynamoDBGrainStorage>(storage);
            AssertProviderOptions(options.Service, options.TableName, options.UseProvisionedThroughput, endpoint, tableName);
            Assert.Equal(serviceId, options.ServiceId);
            Assert.True(options.DeleteStateOnClear);

            await Assert.IsType<DynamoDBGrainStorage>(storage).Init(CancellationToken.None);

            var grainType = $"phase4-state-{suffix}";
            var grainId = GrainId.Create("phase4-live", suffix);
            var state = new GrainState<LiveState>(
                new LiveState
                {
                    Name = "generated-configuration",
                    Revision = 42,
                });

            await storage.WriteStateAsync(grainType, grainId, state);
            Assert.True(state.RecordExists);
            Assert.Equal("0", state.ETag);

            var read = new GrainState<LiveState>(new LiveState());
            await storage.ReadStateAsync(grainType, grainId, read);
            Assert.True(read.RecordExists);
            Assert.NotNull(read.State);
            Assert.Equal("generated-configuration", read.State.Name);
            Assert.Equal(42, read.State.Revision);
            Assert.Equal(state.ETag, read.ETag);

            await storage.ClearStateAsync(grainType, grainId, read);
            Assert.False(read.RecordExists);
            Assert.Null(read.ETag);

            var afterClear = new GrainState<LiveState>(
                new LiveState
                {
                    Name = "sentinel",
                    Revision = -1,
                });
            await storage.ReadStateAsync(grainType, grainId, afterClear);
            Assert.False(afterClear.RecordExists);
            Assert.Null(afterClear.ETag);
            Assert.NotNull(afterClear.State);
            Assert.Equal(string.Empty, afterClear.State.Name);
            Assert.Equal(0, afterClear.State.Revision);
        }
        finally
        {
            await DeleteTableIfExistsAsync(tableName, endpoint);
        }
    }

    [Fact]
    public async Task GeneratedConfiguration_Reminders_UpsertsReadsAndDeletesEntry()
    {
        var endpoint = GetDynamoDBLocalEndpointOrSkip();
        var suffix = Guid.NewGuid().ToString("N");
        var clusterId = $"phase4-cluster-{suffix}";
        var serviceId = $"phase4-service-{suffix}";
        var tableName = $"phase4-reminders-{suffix}";

        try
        {
            var generated = await CreateGeneratedConfigurationAsync(
                ProviderSurface.Reminders,
                endpoint,
                clusterId,
                serviceId,
                tableName);

            AssertGeneratedEndpoint(generated.Silo, endpoint);

            using var host = BuildSiloHost(generated.Silo);
            var reminders = host.Services.GetRequiredService<IReminderTable>();
            var options = host.Services.GetRequiredService<IOptions<DynamoDBReminderStorageOptions>>().Value;

            Assert.Equal("DynamoDBReminderTable", reminders.GetType().Name);
            AssertProviderOptions(options.Service, options.TableName, options.UseProvisionedThroughput, endpoint, tableName);

            await reminders.StartAsync(TestContext.Current.CancellationToken);

            var entry = new ReminderEntry
            {
                GrainId = GrainId.Create("phase4-remindable", suffix),
                ReminderName = $"phase4-reminder-{suffix}",
                StartAt = new DateTime(2026, 8, 23, 12, 34, 56, DateTimeKind.Utc),
                Period = TimeSpan.FromMinutes(7),
            };

            var eTag = await reminders.UpsertRow(entry);
            Assert.False(string.IsNullOrWhiteSpace(eTag));
            Assert.Equal(eTag, entry.ETag);

            var stored = await reminders.ReadRow(entry.GrainId, entry.ReminderName);
            Assert.NotNull(stored);
            Assert.Equal(entry.GrainId, stored.GrainId);
            Assert.Equal(entry.ReminderName, stored.ReminderName);
            Assert.Equal(entry.StartAt, stored.StartAt);
            Assert.Equal(entry.Period, stored.Period);
            Assert.Equal(eTag, stored.ETag);

            Assert.True(await reminders.RemoveRow(entry.GrainId, entry.ReminderName, eTag));
            Assert.Null(await reminders.ReadRow(entry.GrainId, entry.ReminderName));
            Assert.False(await reminders.RemoveRow(entry.GrainId, entry.ReminderName, eTag));
        }
        finally
        {
            await DeleteTableIfExistsAsync(tableName, endpoint);
        }
    }

    private static Uri GetDynamoDBLocalEndpointOrSkip()
    {
        if (!AWSTestConstants.IsDynamoDbAvailable
            || !Uri.TryCreate(AWSTestConstants.DynamoDbService, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw Xunit.Sdk.SkipException.ForSkip(UnavailableSkipReason);
        }

        return endpoint;
    }

    private static IHost BuildSiloHost(IConfiguration configuration)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddConfiguration(configuration);
        builder.UseOrleans();
        return builder.Build();
    }

    private static IHost BuildClientHost(IConfiguration configuration)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddConfiguration(configuration);
        builder.UseOrleansClient();
        return builder.Build();
    }

    private static async Task<GeneratedConfiguration> CreateGeneratedConfigurationAsync(
        ProviderSurface surface,
        Uri endpoint,
        string clusterId,
        string serviceId,
        string tableName)
    {
        await using var builder = DistributedApplicationTestingBuilder.Create();
        var dynamodb = AddDynamoDBLocal(builder, "dynamodb");
        var provider = new DynamoDBProviderConfiguration("dynamodb", tableName, serviceId);
        var orleans = builder.AddOrleans($"orleans-{Guid.NewGuid():N}")
            .WithClusterId(clusterId)
            .WithServiceId(serviceId);
        if (surface != ProviderSurface.Clustering)
        {
            orleans.WithDevelopmentClustering();
        }

        switch (surface)
        {
            case ProviderSurface.Clustering:
                orleans.WithClustering(provider);
                break;
            case ProviderSurface.GrainStorage:
                orleans.WithGrainStorage(StorageName, provider);
                break;
            case ProviderSurface.Reminders:
                orleans.WithReminders(provider);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(surface), surface, null);
        }

        var silo = builder.AddContainer("silo", "unused")
            .WithReference(orleans)
            .WithEnvironment("AWS_ENDPOINT_URL_DYNAMODB", dynamodb.GetEndpoint("http"));
        IResource? clientResource = null;
        if (surface == ProviderSurface.Clustering)
        {
            clientResource = builder.AddContainer("client", "unused")
                .WithReference(orleans.AsClient())
                .WithEnvironment("AWS_ENDPOINT_URL_DYNAMODB", dynamodb.GetEndpoint("http"))
                .Resource;
        }

        AllocateEndpoint(dynamodb, endpoint);

        await using var app = await builder.BuildAsync();
        var siloConfiguration = await AspireResourceConfiguration.CreateAsync(
            silo.Resource,
            app.Services,
            include: IncludeProviderConfiguration);
        var clientConfiguration = clientResource is null
            ? null
            : await AspireResourceConfiguration.CreateAsync(
                clientResource,
                app.Services,
                include: IncludeProviderConfiguration);
        return new GeneratedConfiguration(siloConfiguration, clientConfiguration);
    }

    private static IResourceBuilder<ContainerResource> AddDynamoDBLocal(
        IDistributedApplicationBuilder builder,
        string name)
        => builder.AddContainer(name, "amazon/dynamodb-local")
            .WithHttpEndpoint(targetPort: 8000, name: "http");

    private static void AllocateEndpoint(
        IResourceBuilder<ContainerResource> dynamodb,
        Uri endpointUri)
    {
        var endpoint = dynamodb.Resource.Annotations
            .OfType<EndpointAnnotation>()
            .Single(annotation => annotation.Name == "http");
        endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, endpointUri.Host, endpointUri.Port);
    }

    private static bool IncludeProviderConfiguration(string key)
        => !key.StartsWith("Orleans__Endpoints__", StringComparison.Ordinal)
            && (key.StartsWith("Orleans__", StringComparison.Ordinal)
                || key.StartsWith("AWS_", StringComparison.Ordinal)
                || key.StartsWith("AWS__", StringComparison.Ordinal));

    private static void AssertGeneratedEndpoint(
        IConfiguration configuration,
        Uri endpoint)
        => Assert.Equal(
            endpoint.GetLeftPart(UriPartial.Authority).TrimEnd('/'),
            configuration["AWS_ENDPOINT_URL_DYNAMODB"]?.TrimEnd('/'));

    private static void AssertProviderOptions(
        string? service,
        string? tableName,
        bool useProvisionedThroughput,
        Uri endpoint,
        string expectedTableName)
    {
        Assert.Equal(endpoint.GetLeftPart(UriPartial.Authority).TrimEnd('/'), service?.TrimEnd('/'));
        Assert.Equal(expectedTableName, tableName);
        Assert.False(useProvisionedThroughput);
    }

    private static async Task DeleteTableIfExistsAsync(string tableName, Uri endpoint)
    {
        var storage = new DynamoDBStorage(
            NullLoggerFactory.Instance.CreateLogger("DynamoDBAspireLiveTests"),
            endpoint.GetLeftPart(UriPartial.Authority));
        try
        {
            await storage.DeleTableAsync(tableName);
        }
        catch (ResourceNotFoundException)
        {
        }
    }

    private enum ProviderSurface
    {
        Clustering,
        GrainStorage,
        Reminders,
    }

    private sealed record GeneratedConfiguration(
        IConfigurationRoot Silo,
        IConfigurationRoot? Client);

    private sealed class DynamoDBProviderConfiguration(
        string serviceKey,
        string tableName,
        string serviceId) : IProviderConfiguration
    {
        public void ConfigureResource<T>(
            IResourceBuilder<T> resource,
            string configurationSection)
            where T : IResourceWithEnvironment
        {
            var section = configurationSection.Replace(":", "__", StringComparison.Ordinal);
            var prefix = $"Orleans__{section}";
            resource.WithEnvironment($"{prefix}__ProviderType", "DynamoDB");
            resource.WithEnvironment($"{prefix}__ServiceKey", serviceKey);
            resource.WithEnvironment($"{prefix}__TableName", tableName);
            resource.WithEnvironment($"{prefix}__UseProvisionedThroughput", "false");
            resource.WithEnvironment($"{prefix}__UpdateIfExists", "false");

            if (section.StartsWith("GrainStorage__", StringComparison.Ordinal))
            {
                resource.WithEnvironment($"{prefix}__ServiceId", serviceId);
                resource.WithEnvironment($"{prefix}__DeleteStateOnClear", "true");
            }
        }
    }

    [GenerateSerializer]
    internal sealed class LiveState
    {
        [Id(0)]
        public string Name { get; set; } = string.Empty;

        [Id(1)]
        public int Revision { get; set; }
    }
}
