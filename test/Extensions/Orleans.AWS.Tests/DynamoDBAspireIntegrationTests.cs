using Amazon.CDK.AWS.DynamoDB;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.AWS.DynamoDB;
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
using Xunit;
using DynamoDBAttribute = Amazon.CDK.AWS.DynamoDB.Attribute;

namespace AWSUtils.Tests.Configuration;

[TestSuite("BVT")]
[TestProvider("DynamoDB")]
[TestArea("Aspire")]
[TestCategory("AWS"), TestCategory("DynamoDB"), TestCategory("Aspire"), TestCategory("BVT")]
public sealed class DynamoDBAspireIntegrationTests
{
    [Fact]
    public async Task DynamoDBLocalReference_RunMode_EmitsAwsDynamoDBEndpoint()
    {
        await using var builder = DistributedApplicationTestingBuilder.Create();
        var dynamodb = builder.AddAWSDynamoDBLocal("dynamodb");
        var silo = builder.AddContainer("silo", "unused").WithReference(dynamodb);
        AllocateEndpoint(dynamodb, 8000);

        await using var app = await builder.BuildAsync();
        var environment = await GetEnvironmentVariablesAsync(
            silo.Resource,
            app.Services,
            DistributedApplicationOperation.Run);

        Assert.Equal("http://localhost:8000", environment["AWS_ENDPOINT_URL_DYNAMODB"]);
        Assert.Contains(
            dynamodb.Resource.Annotations.OfType<EndpointAnnotation>(),
            endpoint => endpoint.Name == "http" && endpoint.TargetPort == 8000);
    }

    [Fact]
    public async Task CdkTableReference_PublishMode_EmitsAWSResourcesTableName()
    {
        await using var builder = DistributedApplicationTestingBuilder.Create();
        var stack = builder.AddAWSCDKStack("dynamodb-stack", "phase3-dynamodb-stack");
        var table = stack.AddDynamoDBTable(
            "orders-table",
            new TableProps
            {
                TableName = "phase3-orders",
                PartitionKey = new DynamoDBAttribute { Name = "order-id", Type = AttributeType.STRING },
            });
        var silo = builder.AddContainer("silo", "unused").WithReference(table);

        await using var app = await builder.BuildAsync();
        var environment = await GetEnvironmentVariablesAsync(
            silo.Resource,
            app.Services,
            DistributedApplicationOperation.Publish);

        var expression = Assert.IsType<string>(environment["AWS__Resources__orders-table__TableName"]);
        Assert.StartsWith("{dynamodb-stack.output.", expression, StringComparison.Ordinal);
        Assert.EndsWith("TableName}", expression, StringComparison.Ordinal);
        Assert.Equal("orders-table", table.Resource.Name);
        Assert.Contains(
            table.Resource.Annotations,
            annotation => annotation.GetType().Name.StartsWith(
                "ConstructOutputAnnotation",
                StringComparison.Ordinal));
    }

    [Fact]
    public Task CloudFormationOutput_ForClustering_EmitsStructuredConfiguration()
        => AssertCloudFormationOutputAsync("Orleans__Clustering");

    [Fact]
    public Task CloudFormationOutput_ForDefaultGrainStorage_EmitsStructuredConfiguration()
        => AssertCloudFormationOutputAsync("Orleans__GrainStorage__Default");

    [Fact]
    public Task CloudFormationOutput_ForReminders_EmitsStructuredConfiguration()
        => AssertCloudFormationOutputAsync("Orleans__Reminders");

    [Fact]
    public async Task AppHost_WhenInferenceIsUnavailable_UsesExplicitDynamoDBProviderConfiguration()
    {
        var (siloEnvironment, clientEnvironment) = await CreateDynamoDBOrleansEnvironmentAsync();

        AssertProviderConfiguration(siloEnvironment, "Orleans__Clustering");
        AssertProviderConfiguration(siloEnvironment, "Orleans__GrainStorage__Default");
        AssertProviderConfiguration(siloEnvironment, "Orleans__Reminders");
        AssertProviderConfiguration(clientEnvironment, "Orleans__Clustering");
        Assert.Equal("http://localhost:8000", siloEnvironment["AWS_ENDPOINT_URL_DYNAMODB"]);
        Assert.Equal("http://localhost:8000", clientEnvironment["AWS_ENDPOINT_URL_DYNAMODB"]);
    }

    [Fact]
    public async Task AppHostGeneratedConfiguration_ActivatesAllDynamoDBProviders()
    {
        var (siloEnvironment, clientEnvironment) = await CreateDynamoDBOrleansEnvironmentAsync();
        var siloConfiguration = ToConfigurationValues(siloEnvironment);
        var clientConfiguration = ToConfigurationValues(clientEnvironment);

        var siloBuilder = Host.CreateApplicationBuilder();
        siloBuilder.Configuration.AddInMemoryCollection(siloConfiguration);
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
        clientBuilder.Configuration.AddInMemoryCollection(clientConfiguration);
        clientBuilder.UseOrleansClient();
        using var clientHost = clientBuilder.Build();

        var gateway = clientHost.Services.GetRequiredService<Orleans.Messaging.IGatewayListProvider>();
        var gatewayOptions = clientHost.Services.GetRequiredService<IOptions<DynamoDBGatewayOptions>>().Value;

        Assert.Equal("DynamoDBGatewayListProvider", gateway.GetType().Name);
        AssertDynamoDBOptions(gatewayOptions.Service, gatewayOptions.TableName, "OrleansSilos");
        ValidateDynamoDBOptions(clientHost.Services, ["DynamoDBGatewayOptionsValidator"]);
    }

    private static async Task AssertCloudFormationOutputAsync(string section)
    {
        await using var builder = DistributedApplicationTestingBuilder.Create();
        var stack = builder.AddAWSCloudFormationStack("dynamodb-outputs");
        var silo = builder.AddContainer("silo", "unused")
            .WithEnvironment($"{section}__TableName", stack.GetOutput("TableName"));

        await using var app = await builder.BuildAsync();
        var environment = await GetEnvironmentVariablesAsync(
            silo.Resource,
            app.Services,
            DistributedApplicationOperation.Publish);

        Assert.Equal(
            "{dynamodb-outputs.output.TableName}",
            environment[$"{section}__TableName"]);
        Assert.Contains(
            stack.Resource.Annotations,
            annotation => annotation.GetType().Name == "CloudFormationReferenceAnnotation");
    }

    private static async Task<(
        Dictionary<string, string?> Silo,
        Dictionary<string, string?> Client)> CreateDynamoDBOrleansEnvironmentAsync()
    {
        await using var builder = DistributedApplicationTestingBuilder.Create();
        var dynamodb = builder.AddAWSDynamoDBLocal("dynamodb");
        var provider = new DynamoDBProviderConfiguration("dynamodb");
        var orleans = builder.AddOrleans("cluster")
            .WithClusterId("phase3-cluster")
            .WithServiceId("phase3-service")
            .WithClustering(provider)
            .WithGrainStorage("Default", provider)
            .WithReminders(provider);
        var silo = builder.AddContainer("silo", "unused")
            .WithReference(orleans)
            .WithReference(dynamodb);
        var client = builder.AddContainer("client", "unused")
            .WithReference(orleans.AsClient())
            .WithReference(dynamodb);
        AllocateEndpoint(dynamodb, 8000);

        await using var app = await builder.BuildAsync();
        var siloEnvironment = await GetEnvironmentVariablesAsync(
            silo.Resource,
            app.Services,
            DistributedApplicationOperation.Run);
        var clientEnvironment = await GetEnvironmentVariablesAsync(
            client.Resource,
            app.Services,
            DistributedApplicationOperation.Run);
        return (siloEnvironment, clientEnvironment);
    }

    private static void AllocateEndpoint(
        IResourceBuilder<DynamoDBLocalResource> dynamodb,
        int port)
    {
        var endpoint = dynamodb.Resource.Annotations
            .OfType<EndpointAnnotation>()
            .Single(annotation => annotation.Name == "http");
        endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", port);
    }

    private static async Task<Dictionary<string, string?>> GetEnvironmentVariablesAsync(
        IResource resource,
        IServiceProvider services,
        DistributedApplicationOperation operation)
    {
        var executionContext = new DistributedApplicationExecutionContext(
            new DistributedApplicationExecutionContextOptions(operation)
            {
                ServiceProvider = services,
            });
        var values = new Dictionary<string, object>();
        var callbackContext = new EnvironmentCallbackContext(executionContext, resource, values);

        foreach (var annotation in resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await annotation.Callback(callbackContext);
        }

        var valueContext = new ValueProviderContext
        {
            Caller = resource,
            ExecutionContext = executionContext,
            Network = KnownNetworkIdentifiers.LocalhostNetwork,
        };
        var result = new Dictionary<string, string?>();
        foreach (var (key, value) in values)
        {
            // Unallocated silo/gateway endpoint references are irrelevant to these
            // model-only provider tests and cannot be evaluated until the app runs.
            if (key.StartsWith("Orleans__Endpoints__", StringComparison.Ordinal))
            {
                continue;
            }

            result[key] = value switch
            {
                IManifestExpressionProvider expressionProvider
                    when operation == DistributedApplicationOperation.Publish
                    => expressionProvider.ValueExpression,
                IValueProvider valueProvider => await valueProvider.GetValueAsync(valueContext),
                _ => value.ToString(),
            };
        }

        return result;
    }

    private static Dictionary<string, string?> ToConfigurationValues(
        Dictionary<string, string?> environment)
        => environment.ToDictionary(
            pair => pair.Key.StartsWith("Orleans__", StringComparison.Ordinal)
                || pair.Key.StartsWith("AWS__", StringComparison.Ordinal)
                    ? pair.Key.Replace("__", ":", StringComparison.Ordinal)
                    : pair.Key,
            pair => pair.Value);

    private static void AssertProviderConfiguration(
        Dictionary<string, string?> environment,
        string section)
    {
        Assert.Equal("DynamoDB", environment[$"{section}__ProviderType"]);
        Assert.Equal("dynamodb", environment[$"{section}__ServiceKey"]);
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
