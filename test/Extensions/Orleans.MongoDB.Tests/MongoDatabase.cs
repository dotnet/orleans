using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers.MongoDB.Configuration;
using Orleans.Providers.MongoDB.Membership;
using Orleans.Providers.MongoDB.Reminders;
using Orleans.Providers.MongoDB.Utils;
using Xunit;
using Xunit.Sdk;

namespace Orleans.MongoDB.Tests;

/// <summary>Owns exactly one uniquely named database, never a database from the connection URL.</summary>
internal sealed class MongoDatabase : IAsyncDisposable
{
    public const string ConnectionVariable = "ORLEANSMONGODBCONNECTIONSTRING";
    private readonly MongoClient client;

    private MongoDatabase(string connectionString, Action<MongoClientSettings>? configure)
    {
        var settings = MongoClientSettings.FromConnectionString(connectionString);
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(15);
        settings.ConnectTimeout = TimeSpan.FromSeconds(10);
        settings.SocketTimeout = TimeSpan.FromSeconds(30);
        configure?.Invoke(settings);
        client = new MongoClient(settings);
        Name = $"orleans_mongo_test_{Guid.NewGuid():N}";
        Database = client.GetDatabase(Name);
        ClientFactory = new DefaultMongoClientFactory(client);
    }

    public string Name { get; }
    public IMongoDatabase Database { get; }
    public IMongoClient Client => client;
    public IMongoClientFactory ClientFactory { get; }

    public static void RequireConnection()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable)))
        {
            throw SkipException.ForSkip($"Set {ConnectionVariable} to run MongoDB provider contract tests.");
        }
    }

    public static async Task<MongoDatabase> CreateAsync(Action<MongoClientSettings>? configure = null)
    {
        RequireConnection();
        var result = new MongoDatabase(Environment.GetEnvironmentVariable(ConnectionVariable)!, configure);
        try
        {
            // Only absence skips. Authentication, selection and replica-set failures must fail CI.
            await result.Database.RunCommandAsync<BsonDocument>(
                new BsonDocument("ping", 1), cancellationToken: TestContext.Current.CancellationToken);
            return result;
        }
        catch
        {
            (result.client as IDisposable)?.Dispose();
            throw;
        }
    }

    public IMembershipTable CreateMembershipTable(string clusterId, MongoDBMembershipStrategy strategy)
        => new MongoMembershipTable(ClientFactory, NullLogger<MongoMembershipTable>.Instance,
            Options.Create(new ClusterOptions { ClusterId = clusterId, ServiceId = "mongo-test" }),
            Options.Create(new MongoDBMembershipTableOptions { DatabaseName = Name, Strategy = strategy }));

    public IReminderTable CreateReminderTable(string serviceId)
        => new MongoReminderTable(ClientFactory, NullLogger<MongoReminderTable>.Instance,
            Options.Create(new MongoDBRemindersOptions { DatabaseName = Name }),
            Options.Create(new ClusterOptions { ServiceId = serviceId, ClusterId = "mongo-test" }));

    public async ValueTask DisposeAsync()
    {
        try
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await client.DropDatabaseAsync(Name, cleanup.Token);
        }
        finally
        {
            (client as IDisposable)?.Dispose();
        }
    }
}

internal static class MongoTestHost
{
    // Building, but never starting, supplies Orleans codecs and runtime services without binding ports.
    public static IHost Create(Action<ISiloBuilder>? configure = null)
        => new HostBuilder().UseOrleans(silo =>
        {
            silo.UseLocalhostClustering();
            configure?.Invoke(silo);
        }).Build();
}
