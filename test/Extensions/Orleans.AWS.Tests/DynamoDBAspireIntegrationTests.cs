using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Orleans;
using Aspire.Hosting.Testing;
using Amazon.DynamoDBv2;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orleans.Clustering.DynamoDB;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Storage;
using TestExtensions;
using Xunit;
namespace AWSUtils.Tests.Configuration;

[TestSuite("BVT")]
[TestProvider("DynamoDB")]
[TestArea("Aspire")]
[TestCategory("AWS"), TestCategory("DynamoDB"), TestCategory("Aspire"), TestCategory("BVT")]
public sealed class DynamoDBAspireIntegrationTests
{
    [Fact]
    public async Task DynamoDBLocalContract_RunMode_EmitsAwsDynamoDBEndpoint()
    {
        await using var builder = DistributedApplicationTestingBuilder.Create();
        var dynamodb = AddDynamoDBLocal(builder, "dynamodb");
        var silo = builder.AddContainer("silo", "unused")
            .WithEnvironment("AWS_ENDPOINT_URL_DYNAMODB", dynamodb.GetEndpoint("http"));
        AllocateEndpoint(dynamodb, 8000);

        await using var app = await builder.BuildAsync(TestContext.Current.CancellationToken);
        var configuration = await AspireResourceConfiguration.CreateAsync(
            silo.Resource,
            app.Services,
            include: static key => key.StartsWith("AWS_", StringComparison.Ordinal));

        Assert.Equal("http://localhost:8000", configuration["AWS_ENDPOINT_URL_DYNAMODB"]);
        Assert.Contains(
            dynamodb.Resource.Annotations.OfType<EndpointAnnotation>(),
            endpoint => endpoint.Name == "http" && endpoint.TargetPort == 8000);
    }

    [Fact]
    public async Task AppHost_WhenInferenceIsUnavailable_UsesExplicitDynamoDBProviderConfiguration()
    {
        var generated = await CreateDynamoDBOrleansEnvironmentAsync();
        var siloConfiguration = generated.Silo;
        var clientConfiguration = generated.Client;

        AssertProviderConfiguration(siloConfiguration, "Orleans:Clustering");
        AssertProviderConfiguration(siloConfiguration, "Orleans:GrainStorage:Default");
        AssertProviderConfiguration(siloConfiguration, "Orleans:Reminders");
        AssertProviderConfiguration(clientConfiguration, "Orleans:Clustering");
        Assert.Equal("http://localhost:8000", siloConfiguration["AWS_ENDPOINT_URL_DYNAMODB"]);
        Assert.Equal("http://localhost:8000", clientConfiguration["AWS_ENDPOINT_URL_DYNAMODB"]);
    }

    [Fact]
    public async Task AppHostGeneratedConfiguration_ActivatesAllDynamoDBProviders()
    {
        var generated = await CreateDynamoDBOrleansEnvironmentAsync();
        var siloConfiguration = generated.Silo;
        var clientConfiguration = generated.Client;

        var siloBuilder = Host.CreateApplicationBuilder();
        siloBuilder.Configuration.AddConfiguration(siloConfiguration);
        siloBuilder.UseOrleans();
        using var siloHost = siloBuilder.Build();

        var membership = siloHost.Services.GetRequiredService<IMembershipTable>();
        var storage = siloHost.Services.GetRequiredKeyedService<IGrainStorage>("Default");
        var reminders = siloHost.Services.GetRequiredService<IReminderTable>();
        var clusteringOptions = siloHost.Services.GetRequiredService<IOptions<DynamoDBClusteringOptions>>().Value;
        var storageOptions = siloHost.Services
            .GetRequiredService<IOptionsMonitor<DynamoDBStorageOptions>>()
            .Get("Default");
        var reminderOptions = siloHost.Services
            .GetRequiredService<IOptions<DynamoDBReminderStorageOptions>>()
            .Value;

        Assert.Equal("DynamoDBMembershipTable", membership.GetType().Name);
        Assert.IsType<DynamoDBGrainStorage>(storage);
        Assert.Equal("DynamoDBReminderTable", reminders.GetType().Name);
        AssertDynamoDBOptions(clusteringOptions.Service, clusteringOptions.TableName, "OrleansSilos");
        AssertDynamoDBOptions(storageOptions.Service, storageOptions.TableName, "OrleansGrainState");
        AssertDynamoDBOptions(reminderOptions.Service, reminderOptions.TableName, "OrleansReminders");
        ValidateDynamoDBOptions(
            siloHost.Services,
            [
                "DynamoDBClusteringOptionsValidator",
                "DynamoDBGrainStorageOptionsValidator",
                "DynamoDBReminderStorageOptionsValidator",
            ]);

        var clientBuilder = Host.CreateApplicationBuilder();
        clientBuilder.Configuration.AddConfiguration(clientConfiguration);
        clientBuilder.UseOrleansClient();
        using var clientHost = clientBuilder.Build();

        var gateway = clientHost.Services.GetRequiredService<Orleans.Messaging.IGatewayListProvider>();
        var gatewayOptions = clientHost.Services.GetRequiredService<IOptions<DynamoDBGatewayOptions>>().Value;

        Assert.Equal("DynamoDBGatewayListProvider", gateway.GetType().Name);
        AssertDynamoDBOptions(gatewayOptions.Service, gatewayOptions.TableName, "OrleansSilos");
        ValidateDynamoDBOptions(clientHost.Services, ["DynamoDBGatewayOptionsValidator"]);
    }

    [Fact]
    public async Task CdkAppModel_EmitsExactTopologyAndSecretFreeConfiguration()
    {
        await using var app = await DynamoDBAspireTestModel.CreateCdkAsync();
        var silo = await app.GetSiloConfigurationAsync();
        var client = await app.GetClientConfigurationAsync();
        var tables = app.Model.Resources
            .OfType<DynamoDBAspireTestModel.DynamoDBTableResource>()
            .Select(resource => resource.Contract)
            .OrderBy(table => table.ResourceName, StringComparer.Ordinal)
            .ToArray();

        AssertTable(
            tables,
            "orleans-membership",
            "orders-orleans-membership",
            ("DeploymentId", DynamoDBAttributeType.String),
            ("SiloIdentity", DynamoDBAttributeType.String));
        AssertTable(
            tables,
            "orleans-grain-state",
            "orders-orleans-grain-state",
            ("GrainReference", DynamoDBAttributeType.String),
            ("GrainType", DynamoDBAttributeType.String));
        AssertTable(
            tables,
            "orleans-reminders",
            "orders-orleans-reminders",
            ("ReminderId", DynamoDBAttributeType.String),
            ("GrainHash", DynamoDBAttributeType.Number),
            ("ServiceIdIndex", "ServiceId", DynamoDBAttributeType.String, "GrainHash", DynamoDBAttributeType.Number),
            ("ServiceIdGrainReferenceIndex", "ServiceId", DynamoDBAttributeType.String, "GrainReference", DynamoDBAttributeType.String));
        AssertTable(
            tables,
            "orleans-transactions",
            "orders-orleans-transactions",
            ("PartitionKey", DynamoDBAttributeType.String),
            ("RowKey", DynamoDBAttributeType.String));
        AssertTable(
            tables,
            "orleans-checkpoints",
            "orders-orleans-checkpoints",
            ("CheckpointNamespace", DynamoDBAttributeType.String),
            ("Partition", DynamoDBAttributeType.String));
        Assert.Equal("integration-profile", silo["AWS_PROFILE"]);
        Assert.Equal(DynamoDBAspireTopology.Region, silo["AWS_REGION"]);
        Assert.Equal("integration-profile", silo["AWS:Profile"]);
        Assert.Equal(DynamoDBAspireTopology.Region, silo["AWS:Region"]);
        Assert.Equal(silo["AWS_PROFILE"], client["AWS_PROFILE"]);
        Assert.Equal(silo["AWS_REGION"], client["AWS_REGION"]);
        AssertProviderConfiguration(silo, "Orleans:Clustering", DynamoDBAspireTopology.Membership);
        AssertProviderConfiguration(silo, "Orleans:GrainStorage:Default", DynamoDBAspireTopology.GrainState);
        AssertProviderConfiguration(silo, "Orleans:Reminders", DynamoDBAspireTopology.Reminders);
        AssertProviderConfiguration(client, "Orleans:Clustering", DynamoDBAspireTopology.Membership);
        AssertSecretFree(silo);
        AssertSecretFree(client);
        Assert.Single(
            app.Model.Resources,
            resource => resource is DynamoDBAspireTestModel.DynamoDBStackResource);
    }

    [Fact]
    public async Task CdkGeneratedConfiguration_ActivatesInfrastructureOwnedProviders()
    {
        await using var app = await DynamoDBAspireTestModel.CreateCdkAsync(profile: null);
        var siloConfiguration = await app.GetSiloConfigurationAsync();
        var clientConfiguration = await app.GetClientConfigurationAsync();

        var siloBuilder = Host.CreateApplicationBuilder();
        siloBuilder.Configuration.AddConfiguration(siloConfiguration);
        siloBuilder.UseOrleans();
        using var siloHost = siloBuilder.Build();
        var clustering = siloHost.Services.GetRequiredService<IOptions<DynamoDBClusteringOptions>>().Value;
        var storage = siloHost.Services
            .GetRequiredService<IOptionsMonitor<DynamoDBStorageOptions>>()
            .Get("Default");
        var reminders = siloHost.Services
            .GetRequiredService<IOptions<DynamoDBReminderStorageOptions>>()
            .Value;

        AssertInfrastructureOwnedOptions(
            clustering.Service,
            clustering.TableName,
            clustering.UseProvisionedThroughput,
            clustering.CreateIfNotExists,
            clustering.UpdateIfExists,
            clustering.AccessKey,
            clustering.SecretKey,
            clustering.Token,
            clustering.ProfileName,
            DynamoDBAspireTopology.Membership.TableName);
        AssertInfrastructureOwnedOptions(
            storage.Service,
            storage.TableName,
            storage.UseProvisionedThroughput,
            storage.CreateIfNotExists,
            storage.UpdateIfExists,
            storage.AccessKey,
            storage.SecretKey,
            storage.Token,
            storage.ProfileName,
            DynamoDBAspireTopology.GrainState.TableName);
        AssertInfrastructureOwnedOptions(
            reminders.Service,
            reminders.TableName,
            reminders.UseProvisionedThroughput,
            reminders.CreateIfNotExists,
            reminders.UpdateIfExists,
            reminders.AccessKey,
            reminders.SecretKey,
            reminders.Token,
            reminders.ProfileName,
            DynamoDBAspireTopology.Reminders.TableName);
        Assert.Equal(DynamoDBAspireTopology.ServiceId, storage.ServiceId);

        var clientBuilder = Host.CreateApplicationBuilder();
        clientBuilder.Configuration.AddConfiguration(clientConfiguration);
        clientBuilder.UseOrleansClient();
        using var clientHost = clientBuilder.Build();
        var gateway = clientHost.Services.GetRequiredService<IOptions<DynamoDBGatewayOptions>>().Value;
        AssertInfrastructureOwnedOptions(
            gateway.Service,
            gateway.TableName,
            gateway.UseProvisionedThroughput,
            gateway.CreateIfNotExists,
            gateway.UpdateIfExists,
            gateway.AccessKey,
            gateway.SecretKey,
            gateway.Token,
            gateway.ProfileName,
            DynamoDBAspireTopology.Membership.TableName);
    }

    [Fact]
    public async Task CdkPublishConfiguration_RetainsProviderRegionAndServiceIdentity()
    {
        await using var app = await DynamoDBAspireTestModel.CreateCdkAsync();
        var configuration = await app.GetSiloConfigurationAsync(DistributedApplicationOperation.Publish);

        Assert.Null(configuration["AWS:Region"]);
        Assert.Null(configuration["AWS:Profile"]);
        Assert.Equal(
            DynamoDBAspireTopology.Region,
            configuration["Orleans:Clustering:Region"]);
        Assert.Equal(
            DynamoDBAspireTopology.Region,
            configuration["Orleans:GrainStorage:Default:Region"]);
        Assert.Equal(
            DynamoDBAspireTopology.Region,
            configuration["Orleans:Reminders:Region"]);
        Assert.Equal(
            DynamoDBAspireTopology.ServiceId,
            configuration["Orleans:GrainStorage:Default:ServiceId"]);

        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Configuration.AddConfiguration(configuration);
        hostBuilder.UseOrleans();
        using var host = hostBuilder.Build();
        var storage = host.Services
            .GetRequiredService<IOptionsMonitor<DynamoDBStorageOptions>>()
            .Get("Default");

        Assert.Equal(DynamoDBAspireTopology.Region, storage.Service);
        Assert.Equal(DynamoDBAspireTopology.ServiceId, storage.ServiceId);
    }

    [Fact]
    public void DynamoDBProvider_UsesAwsSdkV4()
        => Assert.Equal(4, typeof(AmazonDynamoDBClient).Assembly.GetName().Version?.Major);

    private static async Task<(
        IConfigurationRoot Silo,
        IConfigurationRoot Client)> CreateDynamoDBOrleansEnvironmentAsync()
    {
        await using var builder = DistributedApplicationTestingBuilder.Create();
        var dynamodb = AddDynamoDBLocal(builder, "dynamodb");
        var provider = new DynamoDBProviderConfiguration("dynamodb");
        var orleans = builder.AddOrleans("cluster")
            .WithClusterId("phase3-cluster")
            .WithServiceId("phase3-service")
            .WithClustering(provider)
            .WithGrainStorage("Default", provider)
            .WithReminders(provider);
        var silo = builder.AddContainer("silo", "unused")
            .WithReference(orleans)
            .WithEnvironment("AWS_ENDPOINT_URL_DYNAMODB", dynamodb.GetEndpoint("http"));
        var client = builder.AddContainer("client", "unused")
            .WithReference(orleans.AsClient())
            .WithEnvironment("AWS_ENDPOINT_URL_DYNAMODB", dynamodb.GetEndpoint("http"));
        AllocateEndpoint(dynamodb, 8000);

        await using var app = await builder.BuildAsync();
        var siloConfiguration = await AspireResourceConfiguration.CreateAsync(
            silo.Resource,
            app.Services,
            include: IncludeProviderConfiguration);
        var clientConfiguration = await AspireResourceConfiguration.CreateAsync(
            client.Resource,
            app.Services,
            include: IncludeProviderConfiguration);
        return (siloConfiguration, clientConfiguration);
    }

    private static IResourceBuilder<ContainerResource> AddDynamoDBLocal(
        IDistributedApplicationBuilder builder,
        string name)
        => builder.AddContainer(name, "amazon/dynamodb-local")
            .WithHttpEndpoint(targetPort: 8000, name: "http");

    private static void AllocateEndpoint(
        IResourceBuilder<ContainerResource> dynamodb,
        int port)
    {
        var endpoint = dynamodb.Resource.Annotations
            .OfType<EndpointAnnotation>()
            .Single(annotation => annotation.Name == "http");
        endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", port);
    }

    private static bool IncludeProviderConfiguration(string key)
        => !key.StartsWith("Orleans__Endpoints__", StringComparison.Ordinal)
            && (key.StartsWith("Orleans__", StringComparison.Ordinal)
                || key.StartsWith("AWS_", StringComparison.Ordinal)
                || key.StartsWith("AWS__", StringComparison.Ordinal));

    private static void AssertProviderConfiguration(
        IConfiguration configuration,
        string section)
    {
        Assert.Equal("DynamoDB", configuration[$"{section}:ProviderType"]);
        Assert.Equal("dynamodb", configuration[$"{section}:ServiceKey"]);
    }

    private static void AssertProviderConfiguration(
        IConfiguration configuration,
        string section,
        DynamoDBTableContract table)
    {
        Assert.Equal("DynamoDB", configuration[$"{section}:ProviderType"]);
        Assert.Equal(table.ResourceName, configuration[$"{section}:ServiceKey"]);
        Assert.Equal(DynamoDBAspireTopology.Region, configuration[$"{section}:Region"]);
        Assert.False(bool.Parse(configuration[$"{section}:UseProvisionedThroughput"]!));
        Assert.False(bool.Parse(configuration[$"{section}:CreateIfNotExists"]!));
        Assert.False(bool.Parse(configuration[$"{section}:UpdateIfExists"]!));
        Assert.Equal(table.TableName, configuration[$"AWS:Resources:{table.ResourceName}:TableName"]);
    }

    private static void AssertDynamoDBOptions(
        string? service,
        string? tableName,
        string expectedTableName)
    {
        Assert.Equal("http://localhost:8000", service);
        Assert.Equal(expectedTableName, tableName);
    }

    private static void ValidateDynamoDBOptions(
        IServiceProvider services,
        string[] expectedValidatorNames)
    {
        var validators = services
            .GetServices<IConfigurationValidator>()
            .Where(validator => validator.GetType().Name.Contains("DynamoDB", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(
            expectedValidatorNames.Order(StringComparer.Ordinal),
            validators.Select(validator => validator.GetType().Name).Order(StringComparer.Ordinal));
        Assert.All(validators, validator => validator.ValidateConfiguration());
    }

    private static void AssertInfrastructureOwnedOptions(
        string? service,
        string? tableName,
        bool useProvisionedThroughput,
        bool createIfNotExists,
        bool updateIfExists,
        string? accessKey,
        string? secretKey,
        string? token,
        string? profileName,
        string expectedTableName)
    {
        Assert.Equal(DynamoDBAspireTopology.Region, service);
        Assert.Equal(expectedTableName, tableName);
        Assert.False(useProvisionedThroughput);
        Assert.False(createIfNotExists);
        Assert.False(updateIfExists);
        Assert.Null(accessKey);
        Assert.Null(secretKey);
        Assert.Null(token);
        Assert.Null(profileName);
    }

    private static void AssertSecretFree(IConfiguration configuration)
    {
        string[] forbidden = ["AccessKey", "SecretKey", "SessionToken", "Token"];
        Assert.DoesNotContain(
            configuration.AsEnumerable(),
            pair => forbidden.Any(fragment =>
                pair.Key.Contains(fragment, StringComparison.OrdinalIgnoreCase)
                || pair.Value?.Contains(fragment, StringComparison.OrdinalIgnoreCase) == true));
    }

    private static void AssertTable(
        IReadOnlyList<DynamoDBTableContract> tables,
        string resourceName,
        string tableName,
        (string Name, DynamoDBAttributeType Type) partitionKey,
        (string Name, DynamoDBAttributeType Type) sortKey,
        params (string Name, string PartitionName, DynamoDBAttributeType PartitionType, string SortName, DynamoDBAttributeType SortType)[] indexes)
    {
        var table = Assert.Single(tables, value => value.ResourceName == resourceName);
        Assert.Equal(tableName, table.TableName);
        Assert.Equal(new DynamoDBAttributeContract(partitionKey.Name, partitionKey.Type), table.PartitionKey);
        Assert.Equal(new DynamoDBAttributeContract(sortKey.Name, sortKey.Type), table.SortKey);
        Assert.Equal(indexes.Length, table.GlobalSecondaryIndexes.Count);
        foreach (var index in indexes)
        {
            var actual = Assert.Single(table.GlobalSecondaryIndexes, value => value.Name == index.Name);
            Assert.Equal(
                new DynamoDBAttributeContract(index.PartitionName, index.PartitionType),
                actual.PartitionKey);
            Assert.Equal(
                new DynamoDBAttributeContract(index.SortName, index.SortType),
                actual.SortKey);
        }
    }

    private sealed class DynamoDBProviderConfiguration(string serviceKey) : IProviderConfiguration
    {
        public void ConfigureResource<T>(
            IResourceBuilder<T> resource,
            string configurationSection)
            where T : IResourceWithEnvironment
        {
            resource.WithEnvironment(
                $"Orleans__{configurationSection}__ProviderType",
                "DynamoDB");
            resource.WithEnvironment(
                $"Orleans__{configurationSection}__ServiceKey",
                serviceKey);
        }
    }
}
