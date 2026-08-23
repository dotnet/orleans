using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Orleans;
using Aspire.Hosting.Testing;
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

        await using var app = await builder.BuildAsync();
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
