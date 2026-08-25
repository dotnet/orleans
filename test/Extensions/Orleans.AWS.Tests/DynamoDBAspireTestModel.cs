#if NET10_0_OR_GREATER

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Orleans;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TestExtensions;

namespace AWSUtils.Tests.Configuration;

internal sealed class DynamoDBAspireTestModel : IAsyncDisposable
{
    private readonly IAsyncDisposable _builder;
    private readonly DistributedApplication _application;

    private DynamoDBAspireTestModel(
        IAsyncDisposable builder,
        DistributedApplication application,
        IResource silo,
        IResource client)
    {
        _builder = builder;
        _application = application;
        Silo = silo;
        Client = client;
    }

    public IResource Silo { get; }

    public IResource Client { get; }

    public DistributedApplicationModel Model
        => _application.Services.GetRequiredService<DistributedApplicationModel>();

    public static async Task<DynamoDBAspireTestModel> CreateCdkAsync(
        string? profile = "integration-profile",
        string region = DynamoDBAspireTopology.Region)
    {
        var builder = DistributedApplicationTestingBuilder.Create();
        var stack = builder.AddResource(new DynamoDBStackResource("orleans-dynamodb"));
        foreach (var table in DynamoDBAspireTopology.Tables)
        {
            builder.AddResource(new DynamoDBTableResource(table));
        }

        var orleans = builder.AddOrleans("cluster")
            .WithClusterId(DynamoDBAspireTopology.ClusterId)
            .WithServiceId(DynamoDBAspireTopology.ServiceId)
            .WithClustering(new DynamoDBProviderConfiguration(
                DynamoDBAspireTopology.Membership,
                profile,
                region,
                infrastructureOwnsTable: true))
            .WithGrainStorage("Default", new DynamoDBProviderConfiguration(
                DynamoDBAspireTopology.GrainState,
                profile,
                region,
                infrastructureOwnsTable: true,
                serviceId: DynamoDBAspireTopology.ServiceId))
            .WithReminders(new DynamoDBProviderConfiguration(
                DynamoDBAspireTopology.Reminders,
                profile,
                region,
                infrastructureOwnsTable: true));
        var silo = builder.AddContainer("silo", "unused")
            .WithReference(orleans)
            .WaitFor(stack);
        var client = builder.AddContainer("client", "unused")
            .WithReference(orleans.AsClient())
            .WaitFor(stack)
            .WaitFor(silo);

        var application = await builder.BuildAsync();
        return new(builder, application, silo.Resource, client.Resource);
    }

    public Task<IConfigurationRoot> GetSiloConfigurationAsync(
        DistributedApplicationOperation operation = DistributedApplicationOperation.Run)
        => GetConfigurationAsync(Silo, operation);

    public Task<IConfigurationRoot> GetClientConfigurationAsync(
        DistributedApplicationOperation operation = DistributedApplicationOperation.Run)
        => GetConfigurationAsync(Client, operation);

    public async ValueTask DisposeAsync()
    {
        await _application.DisposeAsync();
        await _builder.DisposeAsync();
    }

    private Task<IConfigurationRoot> GetConfigurationAsync(
        IResource resource,
        DistributedApplicationOperation operation)
        => AspireResourceConfiguration.CreateAsync(
            resource,
            _application.Services,
            operation,
            include: static key => !key.StartsWith("Orleans__Endpoints__", StringComparison.Ordinal)
                && (key.StartsWith("Orleans__", StringComparison.Ordinal)
                    || key.StartsWith("AWS_", StringComparison.Ordinal)
                    || key.StartsWith("AWS__", StringComparison.Ordinal)));

    internal sealed class DynamoDBStackResource(string name) : Resource(name), IResourceWithWaitSupport;

    internal sealed class DynamoDBTableResource(DynamoDBTableContract contract) : Resource(contract.ResourceName)
    {
        public DynamoDBTableContract Contract { get; } = contract;
    }

    private sealed class DynamoDBProviderConfiguration(
        DynamoDBTableContract table,
        string? profile,
        string region,
        bool infrastructureOwnsTable,
        string? serviceId = null) : IProviderConfiguration
    {
        public void ConfigureResource<T>(
            IResourceBuilder<T> resource,
            string configurationSection)
            where T : IResourceWithEnvironment
        {
            var prefix = $"Orleans__{configurationSection.Replace(":", "__", StringComparison.Ordinal)}";
            resource
                .WithEnvironment($"{prefix}__ProviderType", "DynamoDB")
                .WithEnvironment($"{prefix}__ServiceKey", table.ResourceName)
                .WithEnvironment($"{prefix}__Region", region)
                .WithEnvironment($"AWS__Resources__{table.ResourceName}__TableName", table.TableName)
                .WithEnvironment(context =>
                {
                    if (context.ExecutionContext.IsPublishMode)
                    {
                        return;
                    }

                    context.EnvironmentVariables["AWS_REGION"] = region;
                    context.EnvironmentVariables["AWS__Region"] = region;
                    if (profile is not null)
                    {
                        context.EnvironmentVariables["AWS_PROFILE"] = profile;
                        context.EnvironmentVariables["AWS__Profile"] = profile;
                    }
                });

            if (infrastructureOwnsTable)
            {
                resource
                    .WithEnvironment($"{prefix}__UseProvisionedThroughput", "false")
                    .WithEnvironment($"{prefix}__CreateIfNotExists", "false")
                    .WithEnvironment($"{prefix}__UpdateIfExists", "false");
            }

            if (serviceId is not null)
            {
                resource.WithEnvironment($"{prefix}__ServiceId", serviceId);
            }
        }
    }
}

internal static class DynamoDBAspireTopology
{
    public const string ClusterId = "orders-production";
    public const string ServiceId = "orders-service";
    public const string Region = "us-east-1";

    public static readonly DynamoDBTableContract Membership = new(
        "orleans-membership",
        "orders-orleans-membership",
        new("DeploymentId", DynamoDBAttributeType.String),
        new("SiloIdentity", DynamoDBAttributeType.String),
        []);

    public static readonly DynamoDBTableContract GrainState = new(
        "orleans-grain-state",
        "orders-orleans-grain-state",
        new("GrainReference", DynamoDBAttributeType.String),
        new("GrainType", DynamoDBAttributeType.String),
        []);

    public static readonly DynamoDBTableContract Reminders = new(
        "orleans-reminders",
        "orders-orleans-reminders",
        new("ReminderId", DynamoDBAttributeType.String),
        new("GrainHash", DynamoDBAttributeType.Number),
        [
            new(
                "ServiceIdIndex",
                new("ServiceId", DynamoDBAttributeType.String),
                new("GrainHash", DynamoDBAttributeType.Number)),
            new(
                "ServiceIdGrainReferenceIndex",
                new("ServiceId", DynamoDBAttributeType.String),
                new("GrainReference", DynamoDBAttributeType.String)),
        ]);

    public static readonly DynamoDBTableContract Transactions = new(
        "orleans-transactions",
        "orders-orleans-transactions",
        new("PartitionKey", DynamoDBAttributeType.String),
        new("RowKey", DynamoDBAttributeType.String),
        []);

    public static readonly DynamoDBTableContract Checkpoints = new(
        "orleans-checkpoints",
        "orders-orleans-checkpoints",
        new("CheckpointNamespace", DynamoDBAttributeType.String),
        new("Partition", DynamoDBAttributeType.String),
        []);

    public static IReadOnlyList<DynamoDBTableContract> Tables { get; } =
        [Membership, GrainState, Reminders, Transactions, Checkpoints];
}

internal sealed record DynamoDBTableContract(
    string ResourceName,
    string TableName,
    DynamoDBAttributeContract PartitionKey,
    DynamoDBAttributeContract SortKey,
    IReadOnlyList<DynamoDBIndexContract> GlobalSecondaryIndexes);

internal sealed record DynamoDBIndexContract(
    string Name,
    DynamoDBAttributeContract PartitionKey,
    DynamoDBAttributeContract SortKey);

internal sealed record DynamoDBAttributeContract(string Name, DynamoDBAttributeType Type);

internal enum DynamoDBAttributeType
{
    String,
    Number,
}

#endif
