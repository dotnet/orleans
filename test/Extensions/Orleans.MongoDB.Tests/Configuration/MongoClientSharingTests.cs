using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using Orleans.Clustering.MongoDB;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Messaging;
using Orleans.MongoDB.Tests.Persistence;
using Orleans.Persistence.MongoDB;
using Orleans.Providers.MongoDB.Membership;
using Orleans.Providers.MongoDB.Reminders;
using Orleans.Providers.MongoDB.StorageProviders;
using Orleans.Providers.MongoDB.StorageProviders.Serializers;
using Orleans.Reminders.MongoDB;
using Orleans.Runtime;
using Orleans.Storage;
using Xunit;

namespace Orleans.MongoDB.Tests.Configuration;

[TestProvider("MongoDB"), TestSuite("BVT"), TestArea("Configuration")]
public sealed class MongoClientSharingTests
{
    [Fact]
    public async Task ApplicationSingletonFactory_AllSiloProvidersUseOneClientAndContainerOwnsLifetime()
    {
        var commands = new ConcurrentQueue<CapturedMongoCommand>();
        await using var database = await MongoDatabase.CreateAsync();
        MongoClient? ownedClient = null;
        var calls = 0;
        using (var host = MongoTestHost.Create(silo =>
        {
            silo.Services.AddSingleton<IMongoClient>(_ =>
            {
                calls++;
                var settings = MongoClientSettings.FromConnectionString(
                    Environment.GetEnvironmentVariable(MongoDatabase.ConnectionVariable)!);
                settings.ClusterConfigurator = cluster => cluster.Subscribe<CommandStartedEvent>(e => commands.Enqueue(new(e)));
                return ownedClient = new MongoClient(settings);
            });
            ConfigureSilo(silo, database.Name, null);
        }))
        {
            await ExerciseSiloProviders(host.Services, "Accounts");
            Assert.Same(ownedClient, host.Services.GetRequiredService<IMongoClient>());
            Assert.Same(ownedClient, host.Services.GetRequiredService<IMongoClient>());
            Assert.Equal(1, calls);
            AssertCommands(commands, database.Name, "Shared_", "Shared_", "Shared_Account");
        }
        Assert.Equal(1, calls);
        Assert.Throws<ObjectDisposedException>(() => ownedClient!.GetDatabase(database.Name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    [InlineData("selected")]
    public async Task ClientName_AllCapabilitiesSelectOnlyConfiguredNativeClient(string? clientName)
    {
        var selectedCommands = new ConcurrentQueue<CapturedMongoCommand>();
        var otherCommands = new ConcurrentQueue<CapturedMongoCommand>();
        await using var selected = await MongoDatabase.CreateAsync(settings =>
            settings.ClusterConfigurator = cluster => cluster.Subscribe<CommandStartedEvent>(e => selectedCommands.Enqueue(new(e))));
        await using var other = await MongoDatabase.CreateAsync(settings =>
            settings.ClusterConfigurator = cluster => cluster.Subscribe<CommandStartedEvent>(e => otherCommands.Enqueue(new(e))));
        var keyed = !string.IsNullOrWhiteSpace(clientName);
        void Register(IServiceCollection services)
        {
            services.AddSingleton<IMongoClient>(keyed ? other.Client : selected.Client);
            services.AddKeyedSingleton<IMongoClient>("selected", keyed ? selected.Client : other.Client);
        }
        using var siloHost = MongoTestHost.Create(silo =>
        {
            Register(silo.Services);
            ConfigureSilo(silo, selected.Name, clientName);
            // A second named provider must read its own options, not Accounts or Options.DefaultName.
            silo.AddMongoDBGrainStorage("Archive", (MongoDBGrainStorageOptions options) =>
            {
                options.DatabaseName = other.Name;
                options.ClientName = keyed ? null : "selected";
                options.CollectionPrefix = "Archive_";
            });
        });
        using var clientHost = new HostBuilder().UseOrleansClient(builder =>
        {
            Register(builder.Services);
            builder.Services.Configure<ClusterOptions>(options => options.ClusterId = "shared-client-test");
            builder.UseMongoDBClustering(options =>
            {
                options.DatabaseName = selected.Name;
                options.CollectionPrefix = "Shared_";
                options.ClientName = clientName;
                options.Strategy = MongoDBMembershipStrategy.SingleDocument;
            });
        }).Build();
        selectedCommands.Clear();
        otherCommands.Clear();
        await ExerciseSiloProviders(siloHost.Services, "Accounts");
        await ExerciseGateway(clientHost.Services, selected.Client, selected.Name, "Shared_", includeExistingMember: true);
        AssertCommands(selectedCommands, selected.Name, "Shared_", "Shared_", "Shared_Account");
        Assert.Empty(otherCommands);

        selectedCommands.Clear();
        await ExerciseStorage(siloHost.Services.GetRequiredKeyedService<IGrainStorage>("Archive"), "ArchiveState");
        Assert.Empty(selectedCommands);
        Assert.Contains(otherCommands, command => command.CommandName == "update"
            && command.DatabaseName == other.Name
            && command.Command["update"].AsString == "Archive_ArchiveState");
    }

    [Theory]
    [InlineData("Membership")]
    [InlineData("Gateway")]
    [InlineData("Reminders")]
    [InlineData("Storage")]
    public void MissingKeyedClient_FailsExplicitlyWithoutResolvingGlobalClient(string capability)
    {
        var globalCalls = 0;
        var error = Assert.Throws<InvalidOperationException>(() =>
        {
            if (capability == "Gateway")
            {
                using var host = new HostBuilder().UseOrleansClient(builder =>
                {
                    Register(builder.Services);
                    builder.UseMongoDBClustering(options =>
                    {
                        options.DatabaseName = "unused";
                        options.ClientName = "missing";
                    });
                }).Build();
                _ = host.Services.GetRequiredService<IGatewayListProvider>();
            }
            else
            {
                using var host = MongoTestHost.Create(silo =>
                {
                    Register(silo.Services);
                    if (capability == "Membership") silo.UseMongoDBClustering(options =>
                    {
                        options.DatabaseName = "unused";
                        options.ClientName = "missing";
                    });
                    else if (capability == "Reminders") silo.UseMongoDBReminders(options =>
                    {
                        options.DatabaseName = "unused";
                        options.ClientName = "missing";
                    });
                    else silo.AddMongoDBGrainStorage("Accounts", (MongoDBGrainStorageOptions options) =>
                    {
                        options.DatabaseName = "unused";
                        options.ClientName = "missing";
                    });
                });
                if (capability == "Membership") _ = host.Services.GetRequiredService<IMembershipTable>();
                else if (capability == "Reminders") _ = host.Services.GetRequiredService<IReminderTable>();
                else _ = host.Services.GetRequiredKeyedService<IGrainStorage>("Accounts");
            }
        });
        Assert.Contains(nameof(IMongoClient), error.Message);
        Assert.Equal(0, globalCalls);

        void Register(IServiceCollection services) => services.AddSingleton<IMongoClient>(_ =>
        {
            globalCalls++;
            throw new InvalidOperationException("The global client must not be used as a keyed fallback.");
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("external")]
    public async Task ExternalClient_ProviderStopAndHostDisposalPreserveUsableClient(string? clientName)
    {
        await using var database = await MongoDatabase.CreateAsync();
        IMembershipTable membership;
        IReminderTable reminders;
        IGrainStorage storage;
        using (var host = MongoTestHost.Create(silo =>
        {
            if (clientName is null) silo.Services.AddSingleton<IMongoClient>(database.Client);
            else silo.Services.AddKeyedSingleton<IMongoClient>(clientName, database.Client);
            ConfigureSilo(silo, database.Name, clientName);
        }))
        {
            await ExerciseSiloProviders(host.Services, "Accounts");
            membership = host.Services.GetRequiredService<IMembershipTable>();
            reminders = host.Services.GetRequiredService<IReminderTable>();
            storage = host.Services.GetRequiredKeyedService<IGrainStorage>("Accounts");
            await reminders.StopAsync(TestContext.Current.CancellationToken);
        }
        var ping = await database.Database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, ping["ok"].ToInt32());
        Assert.Equal("shared-host", Assert.Single((await membership.ReadAllAsync(TestContext.Current.CancellationToken)).Members).Item1.HostName);
        await reminders.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var entry = await reminders.ReadRow(GrainId.Create("account", "shared-client"), "bill",
                TestContext.Current.CancellationToken);
            Assert.Equal(TimeSpan.FromMinutes(7), entry!.Period);
            Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), entry.StartAt);
            await ExerciseStorage(storage, "AfterHostDisposal");
        }
        finally { await reminders.StopAsync(TestContext.Current.CancellationToken); }
    }

    [Fact]
    public void SourceSharedOptions_ExposeDistinctProviderIdentitiesAndNoPublicInfrastructure()
    {
        Type[] bases =
        [
            typeof(Orleans.Clustering.MongoDB.MongoDBOptions),
            typeof(Orleans.Persistence.MongoDB.MongoDBOptions),
            typeof(Orleans.Reminders.MongoDB.MongoDBOptions)
        ];
        Assert.Equal(new[]
        {
            "Orleans.Clustering.MongoDB.MongoDBOptions",
            "Orleans.Persistence.MongoDB.MongoDBOptions",
            "Orleans.Reminders.MongoDB.MongoDBOptions"
        }, bases.Select(type => type.FullName));
        Assert.Equal(new[]
        {
            "Orleans.Clustering.MongoDB", "Orleans.Persistence.MongoDB", "Orleans.Reminders.MongoDB"
        }, bases.Select(type => type.Assembly.GetName().Name));
        Assert.Equal(3, bases.Distinct().Count());
        Assert.Equal(3, bases.Select(type => type.Assembly).Distinct().Count());
        Assert.Equal(bases[0], typeof(MongoDBMembershipTableOptions).BaseType);
        Assert.Equal(bases[0], typeof(MongoDBGatewayListProviderOptions).BaseType);
        Assert.Equal(bases[1], typeof(MongoDBGrainStorageOptions).BaseType);
        Assert.Equal(bases[2], typeof(MongoDBRemindersOptions).BaseType);
        Type[] providerTypes =
        [
            typeof(MongoDBMembershipTableOptions), typeof(MongoDBGatewayListProviderOptions),
            typeof(MongoDBMembershipStrategy), typeof(MongoDBGrainStorageOptions),
            typeof(JsonGrainStateSerializerOptions), typeof(GrainStorageKeyGenerator),
            typeof(MongoDBRemindersOptions)
        ];
        Assert.Equal(new[]
        {
            "Orleans.Clustering.MongoDB", "Orleans.Clustering.MongoDB", "Orleans.Clustering.MongoDB",
            "Orleans.Persistence.MongoDB", "Orleans.Persistence.MongoDB", "Orleans.Persistence.MongoDB",
            "Orleans.Reminders.MongoDB"
        }, providerTypes.Select(type => type.Namespace));
        var exported = bases.SelectMany(type => type.Assembly.GetExportedTypes()).ToArray();
        Assert.Equal(exported.Length, exported.Select(type => type.FullName).Distinct().Count());
        Assert.DoesNotContain(exported, type => type.Namespace is "Orleans.Providers.MongoDB.Configuration"
            or "Orleans.Providers.MongoDB.Utils");
        Assert.DoesNotContain(exported, type => type.Name is "IMongoClientFactory" or "DefaultMongoClientFactory"
            or "CollectionBase`1" or "MongoDBOptionsValidator`1");
        Assert.DoesNotContain(exported.SelectMany(type => type.GetMethods()),
            method => method.Name is "UseMongoDBClient" or "AddMongoDBClient");
        Assert.Equal("Orleans.Providers.MongoDB.Membership.MongoMembershipTable", typeof(MongoMembershipTable).FullName);
        Assert.Equal("Orleans.Providers.MongoDB.Reminders.MongoReminderTable", typeof(MongoReminderTable).FullName);
        Assert.Equal("Orleans.Providers.MongoDB.StorageProviders.MongoGrainStorage", typeof(MongoGrainStorage).FullName);
        Assert.Equal("Orleans.Providers.MongoDB.StorageProviders.Serializers.JsonGrainStateSerializer", typeof(JsonGrainStateSerializer).FullName);
        Assert.Equal("Orleans.Providers.MongoDB.StorageProviders.Serializers.BsonGrainStateSerializer", typeof(BsonGrainStateSerializer).FullName);
        Assert.Equal("Orleans.Providers.MongoDB.StorageProviders.Serializers.BinaryGrainStateSerializer", typeof(BinaryGrainStateSerializer).FullName);
    }

    internal static void ConfigureSilo(ISiloBuilder silo, string databaseName, string? clientName)
    {
        silo.Services.Configure<ClusterOptions>(options => options.ClusterId = "shared-client-test");
        silo.UseMongoDBClustering(options =>
        {
            options.DatabaseName = databaseName;
            options.CollectionPrefix = "Shared_";
            options.ClientName = clientName;
            options.Strategy = MongoDBMembershipStrategy.SingleDocument;
        });
        silo.UseMongoDBReminders(options =>
        {
            options.DatabaseName = databaseName;
            options.CollectionPrefix = "Shared_";
            options.ClientName = clientName;
            options.CreateIndexes = false;
        });
        silo.AddMongoDBGrainStorage("Accounts", (MongoDBGrainStorageOptions options) =>
        {
            options.DatabaseName = databaseName;
            options.CollectionPrefix = "Shared_";
            options.ClientName = clientName;
        });
    }

    internal static async Task ExerciseSiloProviders(IServiceProvider services, string storageName)
    {
        var token = TestContext.Current.CancellationToken;
        var membership = services.GetRequiredService<IMembershipTable>();
        await membership.InitializeMembershipTableAsync(true, token);
        var member = Member(11111, 30001);
        Assert.True(await membership.InsertRowAsync(member, (await membership.ReadAllAsync(token)).Version.Next(), token));
        var retained = Assert.Single((await membership.ReadAllAsync(token)).Members);
        Assert.Equal(member.SiloAddress, retained.Item1.SiloAddress);
        Assert.Equal("shared-host", retained.Item1.HostName);
        Assert.Equal(SiloStatus.Active, retained.Item1.Status);
        Assert.Equal(30001, retained.Item1.ProxyPort);
        var reminders = services.GetRequiredService<IReminderTable>();
        await reminders.StartAsync(token);
        try
        {
            var entry = new ReminderEntry
            {
                GrainId = GrainId.Create("account", "shared-client"),
                ReminderName = "bill",
                StartAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Period = TimeSpan.FromMinutes(7)
            };
            var tag = await reminders.UpsertRow(entry, token);
            Assert.False(string.IsNullOrWhiteSpace(tag));
            var read = await reminders.ReadRow(entry.GrainId, "bill", token);
            Assert.Equal(tag, read!.ETag);
            Assert.Equal(entry.GrainId, read.GrainId);
            Assert.Equal("bill", read.ReminderName);
            Assert.Equal(TimeSpan.FromMinutes(7), read.Period);
            Assert.Equal(entry.StartAt, read.StartAt);
        }
        finally { await reminders.StopAsync(token); }
        await ExerciseStorage(services.GetRequiredKeyedService<IGrainStorage>(storageName), "Account");
    }

    internal static async Task ExerciseStorage(IGrainStorage storage, string stateName)
    {
        var token = TestContext.Current.CancellationToken;
        var id = GrainId.Create("account", stateName);
        var state = new GrainState<MongoTestState>
        {
            State = new MongoTestState { Name = "Ada", Count = 42 }
        };
        await storage.WriteStateAsync(stateName, id, state, token);
        Assert.True(state.RecordExists);
        Assert.False(string.IsNullOrWhiteSpace(state.ETag));
        var restored = new GrainState<MongoTestState> { State = new MongoTestState { Name = "stale", Count = -1 } };
        await storage.ReadStateAsync(stateName, id, restored, token);
        Assert.Equal("Ada", restored.State.Name);
        Assert.Equal(42, restored.State.Count);
        Assert.True(restored.RecordExists);
        Assert.Equal(state.ETag, restored.ETag);
    }

    internal static async Task ExerciseGateway(IServiceProvider services, IMongoClient client, string databaseName,
        string prefix, MongoDBMembershipStrategy strategy = MongoDBMembershipStrategy.SingleDocument,
        bool includeExistingMember = false)
    {
        var cluster = services.GetRequiredService<IOptions<ClusterOptions>>().Value;
        var table = new MongoMembershipTable(client, NullLogger<MongoMembershipTable>.Instance,
            Options.Create(cluster), Options.Create(new MongoDBMembershipTableOptions
            {
                DatabaseName = databaseName,
                CollectionPrefix = prefix,
                Strategy = strategy
            }));
        var token = TestContext.Current.CancellationToken;
        await table.InitializeMembershipTableAsync(true, token);
        var member = Member(11112, 30002);
        Assert.True(await table.InsertRowAsync(member, (await table.ReadAllAsync(token)).Version.Next(), token));
        var gateway = services.GetRequiredService<IGatewayListProvider>();
        await gateway.InitializeGatewayListProvider();
        var addresses = await gateway.GetGateways();
        var expected = includeExistingMember
            ? new[]
            {
                SiloAddress.New(IPAddress.Loopback, 30001, 1).ToGatewayUri(),
                SiloAddress.New(IPAddress.Loopback, 30002, 1).ToGatewayUri()
            }
            : new[] { SiloAddress.New(IPAddress.Loopback, 30002, 1).ToGatewayUri() };
        Assert.Equal(expected.OrderBy(uri => uri.Port), addresses.OrderBy(uri => uri.Port));
    }

    internal static void AssertCommands(IEnumerable<CapturedMongoCommand> commands, string databaseName,
        string membershipPrefix, string reminderPrefix, string storageCollection)
    {
        var owned = commands.Where(command => command.DatabaseName == databaseName).ToArray();
        Assert.Contains(owned, command => command.CommandName == "update"
            && command.Command["update"].AsString.StartsWith(membershipPrefix + "OrleansMembership", StringComparison.Ordinal));
        Assert.Contains(owned, command => command.CommandName == "find"
            && command.Command["find"].AsString == reminderPrefix + "OrleansReminderV2");
        Assert.Contains(owned, command => command.CommandName == "update"
            && command.Command["update"].AsString == storageCollection);
        Assert.Contains(owned, command => command.CommandName == "find"
            && command.Command["find"].AsString == storageCollection);
    }

    private static MembershipEntry Member(int port, int gatewayPort) => new()
    {
        SiloAddress = SiloAddress.New(IPAddress.Loopback, port, 1),
        HostName = "shared-host",
        SiloName = "shared-silo",
        Status = SiloStatus.Active,
        ProxyPort = gatewayPort,
        StartTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        IAmAliveTime = new DateTime(2026, 1, 1, 0, 1, 0, DateTimeKind.Utc)
    };
}

// Driver event documents are disposed after the callback; own the captured BSON before returning.
internal sealed record CapturedMongoCommand(string CommandName, string DatabaseName, BsonDocument Command)
{
    public CapturedMongoCommand(CommandStartedEvent command)
        : this(command.CommandName, command.DatabaseNamespace.DatabaseName, command.Command.DeepClone().AsBsonDocument) { }
}
