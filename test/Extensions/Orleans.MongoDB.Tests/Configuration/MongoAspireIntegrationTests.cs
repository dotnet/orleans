#if NET10_0_OR_GREATER
using System.Collections.Concurrent;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using Orleans.Clustering.MongoDB;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Persistence.MongoDB;
using Orleans.Reminders.MongoDB;
using Orleans.Storage;
using Xunit;

namespace Orleans.MongoDB.Tests.Configuration;

[TestProvider("MongoDB"), TestSuite("BVT"), TestArea("Configuration")]
public sealed class MongoAspireIntegrationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AspireNativeRegistrations_ConsumeClientAndInferDatabaseUnlessExplicit(bool keyed, bool explicitDatabase)
    {
        await using var registeredDatabase = await MongoDatabase.CreateAsync();
        await using var overrideDatabase = await MongoDatabase.CreateAsync();
        var commands = new ConcurrentQueue<CapturedMongoCommand>();
        var configuration = ProviderConfiguration(keyed ? "orders" : null);
        var expectedDatabase = explicitDatabase ? overrideDatabase.Name : registeredDatabase.Name;
        if (explicitDatabase)
        {
            foreach (var section in SiloSections)
                configuration[$"Orleans:{section}:DatabaseName"] = expectedDatabase;
        }
        var builder = Host.CreateApplicationBuilder();
        builder.Services.Configure<ClusterOptions>(options => options.ClusterId = "aspire-client-test");
        builder.Configuration.AddInMemoryCollection(configuration);
        RegisterAspireClient(builder, registeredDatabase.Name, keyed, commands);
        builder.UseOrleans();
        using var host = builder.Build();
        var native = keyed
            ? host.Services.GetRequiredKeyedService<IMongoClient>("orders")
            : host.Services.GetRequiredService<IMongoClient>();
        var database = keyed
            ? host.Services.GetRequiredKeyedService<IMongoDatabase>("orders")
            : host.Services.GetRequiredService<IMongoDatabase>();
        Assert.Same(native, database.Client);
        Assert.Equal(registeredDatabase.Name, database.DatabaseNamespace.DatabaseName);
        AssertOptions(host.Services, expectedDatabase, keyed ? "orders" : null);
        commands.Clear();
        await MongoClientSharingTests.ExerciseSiloProviders(host.Services, "Accounts");
        MongoClientSharingTests.AssertCommands(commands, expectedDatabase, "Shared_", "Shared_", "Shared_Account");
        if (explicitDatabase)
            Assert.DoesNotContain(commands, command => command.DatabaseName == registeredDatabase.Name);

        var gatewayBuilder = Host.CreateApplicationBuilder();
        gatewayBuilder.Services.Configure<ClusterOptions>(options => options.ClusterId = "aspire-client-test");
        gatewayBuilder.Configuration.AddInMemoryCollection(configuration.Where(pair =>
            pair.Key.StartsWith("Orleans:Clustering:", StringComparison.Ordinal)));
        RegisterAspireClient(gatewayBuilder, registeredDatabase.Name, keyed, commands);
        gatewayBuilder.UseOrleansClient();
        using var gatewayHost = gatewayBuilder.Build();
        var gatewayOptions = gatewayHost.Services.GetRequiredService<IOptions<MongoDBGatewayListProviderOptions>>().Value;
        Assert.Equal(expectedDatabase, gatewayOptions.DatabaseName);
        Assert.Equal(keyed ? "orders" : null, gatewayOptions.ClientName);
        commands.Clear();
        await MongoClientSharingTests.ExerciseGateway(gatewayHost.Services, native, expectedDatabase, "Shared_",
            includeExistingMember: true);
        Assert.Contains(commands, command => command.CommandName == "find"
            && command.DatabaseName == expectedDatabase
            && command.Command["find"].AsString == "Shared_OrleansMembershipSingle");
    }

    [Fact]
    public async Task AspireAppHostModel_EmittedMongoDBDatabaseAndServiceKeyDriveSiloAndClientDiscovery()
    {
        await using var database = await MongoDatabase.CreateAsync();
        var appBuilder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = [],
            DisableDashboard = true
        });
        var orders = appBuilder.AddMongoDB("mongo").AddDatabase("orders", database.Name);
        var orleans = appBuilder.AddOrleans("cluster")
            .WithClustering(orders)
            .WithGrainStorage("Accounts", orders)
            .WithGrainStorage("Archive", orders)
            .WithReminders(orders);
        var silo = appBuilder.AddExecutable("silo", "dotnet", ".", "unused-silo.dll").WithReference(orleans);
        var client = appBuilder.AddExecutable("client", "dotnet", ".", "unused-client.dll").WithReference(orleans.AsClient());
        // Build the actual model, never run it: no Docker/container/process/port ownership.
        await using var app = appBuilder.Build();
        var siloConfiguration = await GetProviderEnvironment(silo.Resource, app.Services);
        foreach (var section in SiloSections.Append("GrainStorage:Archive"))
        {
            Assert.Equal("MongoDBDatabase", siloConfiguration[$"Orleans:{section}:ProviderType"]);
            Assert.Equal("orders", siloConfiguration[$"Orleans:{section}:ServiceKey"]);
            Assert.DoesNotContain($"Orleans:{section}:DatabaseName", siloConfiguration);
        }
        var clientConfiguration = await GetProviderEnvironment(client.Resource, app.Services);
        Assert.Equal("MongoDBDatabase", clientConfiguration["Orleans:Clustering:ProviderType"]);
        Assert.Equal("orders", clientConfiguration["Orleans:Clustering:ServiceKey"]);

        var commands = new ConcurrentQueue<CapturedMongoCommand>();
        var siloBuilder = Host.CreateApplicationBuilder();
        siloBuilder.Configuration.AddInMemoryCollection(siloConfiguration);
        // Use the provisioned replica set transport, while consuming unchanged AppHost provider output.
        RegisterAspireClient(siloBuilder, database.Name, true, commands);
        siloBuilder.UseOrleans();
        using var siloHost = siloBuilder.Build();
        AssertOptions(siloHost.Services, database.Name, "orders");
        var archiveOptions = siloHost.Services.GetRequiredService<IOptionsMonitor<MongoDBGrainStorageOptions>>().Get("Archive");
        Assert.Equal(database.Name, archiveOptions.DatabaseName);
        Assert.Equal("orders", archiveOptions.ClientName);
        commands.Clear();
        await MongoClientSharingTests.ExerciseSiloProviders(siloHost.Services, "Accounts");
        await MongoClientSharingTests.ExerciseStorage(
            siloHost.Services.GetRequiredKeyedService<IGrainStorage>("Archive"), "ArchiveState");
        // AppHost emits no collection prefix; preserve production defaults.
        MongoClientSharingTests.AssertCommands(commands, database.Name, "", "", "GrainsAccount");
        Assert.Contains(commands, command => command.CommandName == "update"
            && command.Command["update"].AsString == "GrainsArchiveState");

        var clientBuilder = Host.CreateApplicationBuilder();
        clientBuilder.Configuration.AddInMemoryCollection(clientConfiguration);
        RegisterAspireClient(clientBuilder, database.Name, true, commands);
        clientBuilder.UseOrleansClient();
        using var clientHost = clientBuilder.Build();
        var gatewayOptions = clientHost.Services.GetRequiredService<IOptions<MongoDBGatewayListProviderOptions>>().Value;
        Assert.Equal(database.Name, gatewayOptions.DatabaseName);
        Assert.Equal("orders", gatewayOptions.ClientName);
        commands.Clear();
        await MongoClientSharingTests.ExerciseGateway(clientHost.Services,
            siloHost.Services.GetRequiredKeyedService<IMongoClient>("orders"), database.Name, "",
            gatewayOptions.Strategy, includeExistingMember: true);
        Assert.Contains(commands, command => command.CommandName == "find"
            && command.DatabaseName == database.Name);
    }

    private static readonly string[] SiloSections = ["Clustering", "Reminders", "GrainStorage:Accounts"];

    private static Dictionary<string, string?> ProviderConfiguration(string? serviceKey)
    {
        var values = new Dictionary<string, string?>();
        foreach (var section in SiloSections)
        {
            values[$"Orleans:{section}:ProviderType"] = "MongoDBDatabase";
            values[$"Orleans:{section}:ServiceKey"] = serviceKey;
            values[$"Orleans:{section}:CollectionPrefix"] = "Shared_";
        }
        values["Orleans:Clustering:Strategy"] = "SingleDocument";
        values["Orleans:Reminders:CreateIndexes"] = "false";
        return values;
    }

    private static void RegisterAspireClient(HostApplicationBuilder builder, string databaseName, bool keyed,
        ConcurrentQueue<CapturedMongoCommand> commands)
    {
        var connection = new MongoUrlBuilder(Environment.GetEnvironmentVariable(MongoDatabase.ConnectionVariable)!)
        {
            DatabaseName = databaseName
        };
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:orders"] = connection.ToString()
        });
        void Configure(MongoClientSettings settings)
        {
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(15);
            settings.ClusterConfigurator = cluster => cluster.Subscribe<CommandStartedEvent>(e => commands.Enqueue(new(e)));
        }
        if (keyed)
        {
            builder.AddKeyedMongoDBClient("orders", settings => settings.DisableHealthChecks = true, Configure);
            builder.Services.AddSingleton<IMongoClient>(_ => throw new InvalidOperationException(
                "Keyed Aspire provider must not resolve the global client."));
        }
        else builder.AddMongoDBClient("orders", settings => settings.DisableHealthChecks = true, Configure);
    }

    private static void AssertOptions(IServiceProvider services, string databaseName, string? clientName)
    {
        var membership = services.GetRequiredService<IOptions<MongoDBMembershipTableOptions>>().Value;
        var reminders = services.GetRequiredService<IOptions<MongoDBRemindersOptions>>().Value;
        var storage = services.GetRequiredService<IOptionsMonitor<MongoDBGrainStorageOptions>>().Get("Accounts");
        Assert.Equal(databaseName, membership.DatabaseName);
        Assert.Equal(databaseName, reminders.DatabaseName);
        Assert.Equal(databaseName, storage.DatabaseName);
        Assert.Equal(clientName, membership.ClientName);
        Assert.Equal(clientName, reminders.ClientName);
        Assert.Equal(clientName, storage.ClientName);
    }

    private static async Task<Dictionary<string, string?>> GetProviderEnvironment(IResource resource, IServiceProvider services)
    {
        var execution = new DistributedApplicationExecutionContext(
            new DistributedApplicationExecutionContextOptions(DistributedApplicationOperation.Run) { ServiceProvider = services });
        var values = new Dictionary<string, object>();
        var callback = new EnvironmentCallbackContext(execution, resource, values);
        foreach (var annotation in resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
            await annotation.Callback(callback);
        var context = new ValueProviderContext
        {
            Caller = resource, ExecutionContext = execution, Network = KnownNetworkIdentifiers.LocalhostNetwork
        };
        var result = new Dictionary<string, string?>();
        foreach (var (key, value) in values)
        {
            if (!key.StartsWith("Orleans__", StringComparison.Ordinal)) continue;
            // Provider/cluster configuration needs no container endpoint allocation.
            if (key.StartsWith("Orleans__Endpoints__", StringComparison.Ordinal)) continue;
            result[key.Replace("__", ":", StringComparison.Ordinal)] = value is IValueProvider provider
                ? await provider.GetValueAsync(context)
                : value.ToString();
        }
        return result;
    }
}
#endif
