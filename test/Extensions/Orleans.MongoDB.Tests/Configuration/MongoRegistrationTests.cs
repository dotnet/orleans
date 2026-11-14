using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using Orleans.Clustering.MongoDB;
using Orleans.Hosting;
using Orleans.Messaging;
using Orleans.Persistence.MongoDB;
using Orleans.Providers;
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
public sealed class MongoRegistrationTests
{
    [Fact]
    public async Task ConfigurationDiscovery_SiloBindsAllProvidersAndReusesHostRegisteredClient()
    {
        var commands = new ConcurrentQueue<CapturedMongoCommand>();
        await using var database = await MongoDatabase.CreateAsync(settings =>
            settings.ClusterConfigurator = cluster => cluster.Subscribe<CommandStartedEvent>(e => commands.Enqueue(new(e))));
        var client = database.Client;
        using var host = ConfiguredHost(new Dictionary<string, string?>
        {
            ["Orleans:Clustering:ProviderType"] = "MongoDB",
            ["Orleans:Clustering:DatabaseName"] = database.Name,
            ["Orleans:Clustering:CollectionPrefix"] = "members_",
            ["Orleans:Clustering:Strategy"] = "Multiple",
            ["Orleans:Reminders:ProviderType"] = "MongoDB",
            ["Orleans:Reminders:DatabaseName"] = database.Name,
            ["Orleans:Reminders:CollectionPrefix"] = "timers_",
            ["Orleans:Reminders:CreateIndexes"] = "false",
            ["Orleans:GrainStorage:Accounts:ProviderType"] = "MongoDB",
            ["Orleans:GrainStorage:Accounts:DatabaseName"] = database.Name,
            ["Orleans:GrainStorage:Accounts:CollectionPrefix"] = "accounts_",
            ["Orleans:GrainStorage:Accounts:ClientName"] = "host-client",
            ["Orleans:GrainStorage:Archive:ProviderType"] = "MongoDB",
            ["Orleans:GrainStorage:Archive:DatabaseName"] = database.Name
        }, services =>
        {
            services.AddSingleton<IMongoClient>(client);
            services.AddKeyedSingleton<IMongoClient>("host-client", client);
        });
        Assert.IsType<MongoMembershipTable>(host.Services.GetRequiredService<IMembershipTable>());
        Assert.IsType<MongoReminderTable>(host.Services.GetRequiredService<IReminderTable>());
        var membership = host.Services.GetRequiredService<IOptions<MongoDBMembershipTableOptions>>().Value;
        Assert.Equal(database.Name, membership.DatabaseName);
        Assert.Equal("members_", membership.CollectionPrefix);
        Assert.Equal(MongoDBMembershipStrategy.Multiple, membership.Strategy);
        var reminders = host.Services.GetRequiredService<IOptions<MongoDBRemindersOptions>>().Value;
        Assert.Equal(database.Name, reminders.DatabaseName);
        Assert.Equal("timers_", reminders.CollectionPrefix);
        Assert.False(reminders.CreateIndexes);
        var monitor = host.Services.GetRequiredService<IOptionsMonitor<MongoDBGrainStorageOptions>>();
        Assert.Equal(database.Name, monitor.Get("Accounts").DatabaseName);
        Assert.Equal("accounts_", monitor.Get("Accounts").CollectionPrefix);
        Assert.Equal("host-client", monitor.Get("Accounts").ClientName);
        Assert.Equal(database.Name, monitor.Get("Archive").DatabaseName);
        Assert.Equal("Grains", monitor.Get("Archive").CollectionPrefix);
        Assert.IsType<JsonGrainStateSerializer>(monitor.Get("Accounts").GrainStateSerializer);
        var accounts = host.Services.GetRequiredKeyedService<IGrainStorage>("Accounts");
        var archive = host.Services.GetRequiredKeyedService<IGrainStorage>("Archive");
        Assert.IsType<MongoGrainStorage>(accounts);
        Assert.IsType<MongoGrainStorage>(archive);
        Assert.NotSame(accounts, archive);
        commands.Clear();
        await MongoClientSharingTests.ExerciseSiloProviders(host.Services, "Accounts");
        await MongoClientSharingTests.ExerciseStorage(archive, "ArchiveState");
        MongoClientSharingTests.AssertCommands(commands, database.Name, "members_", "timers_", "accounts_Account");
        Assert.Contains(commands, command => command.CommandName == "update"
            && command.Command["update"].AsString == "GrainsArchiveState");
    }

    [Fact]
    public async Task ConfigurationDiscovery_ClientBindsGatewayOptionsAndUsesConfiguredClient()
    {
        var commands = new ConcurrentQueue<CapturedMongoCommand>();
        await using var database = await MongoDatabase.CreateAsync(settings =>
            settings.ClusterConfigurator = cluster => cluster.Subscribe<CommandStartedEvent>(e => commands.Enqueue(new(e))));
        var builder = new HostBuilder().ConfigureAppConfiguration(config => config.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Orleans:Clustering:ProviderType"] = "MongoDB",
                ["Orleans:Clustering:DatabaseName"] = database.Name,
                ["Orleans:Clustering:CollectionPrefix"] = "gateway_",
                ["Orleans:Clustering:Strategy"] = "MultipleDeprecated"
            }));
        builder.ConfigureServices(services => services.AddSingleton<IMongoClient>(database.Client));
        builder.UseOrleansClient();
        using var host = builder.Build();
        Assert.IsType<MongoGatewayListProvider>(host.Services.GetRequiredService<IGatewayListProvider>());
        var options = host.Services.GetRequiredService<IOptions<MongoDBGatewayListProviderOptions>>().Value;
        Assert.Equal(database.Name, options.DatabaseName);
        Assert.Equal("gateway_", options.CollectionPrefix);
        Assert.Equal(MongoDBMembershipStrategy.MultipleDeprecated, options.Strategy);
        Assert.Same(database.Client, host.Services.GetRequiredService<IMongoClient>());
        commands.Clear();
        await MongoClientSharingTests.ExerciseGateway(host.Services, database.Client, database.Name, "gateway_",
            MongoDBMembershipStrategy.MultipleDeprecated);
        Assert.Contains(commands, command => command.CommandName == "find"
            && command.DatabaseName == database.Name);
    }

    [Fact]
    public async Task ConfigurationDiscovery_DefaultNamedStoragePreservesDefaultsAndExplicitClient()
    {
        var commands = new ConcurrentQueue<CapturedMongoCommand>();
        await using var database = await MongoDatabase.CreateAsync(settings =>
            settings.ClusterConfigurator = cluster => cluster.Subscribe<CommandStartedEvent>(e => commands.Enqueue(new(e))));
        using var host = ConfiguredHost(new Dictionary<string, string?>
        {
            [$"Orleans:GrainStorage:{ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME}:ProviderType"] = "MongoDB",
            [$"Orleans:GrainStorage:{ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME}:DatabaseName"] = database.Name
        }, services => services.AddSingleton<IMongoClient>(database.Client),
            configureSilo: silo => silo.UseLocalhostClustering());
        var name = ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME;
        Assert.IsType<MongoGrainStorage>(host.Services.GetRequiredKeyedService<IGrainStorage>(name));
        var options = host.Services.GetRequiredService<IOptionsMonitor<MongoDBGrainStorageOptions>>().Get(name);
        Assert.Equal(database.Name, options.DatabaseName);
        Assert.Equal("Grains", options.CollectionPrefix);
        Assert.False(options.CreateShardKeyForCosmos);
        Assert.IsType<JsonGrainStateSerializer>(options.GrainStateSerializer);
        commands.Clear();
        await MongoClientSharingTests.ExerciseStorage(host.Services.GetRequiredKeyedService<IGrainStorage>(name), "DefaultState");
        Assert.Contains(commands, command => command.CommandName == "update"
            && command.Command["update"].AsString == "GrainsDefaultState"
            && command.DatabaseName == database.Name);
    }

    [Fact]
    public void ConfigurationBuilder_NullStorageNameUsesOrleansDefaultProvider()
    {
        var registration = Assert.Single(typeof(MongoGrainStorage).Assembly.GetCustomAttributes<RegisterProviderAttribute>(),
            registration => registration.Name == "MongoDB" && registration.Kind == "GrainStorage" && registration.Target == "Silo");
        Assert.Equal("Orleans.Hosting.MongoDBGrainStorageProviderBuilder", registration.Type.FullName);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:DatabaseName"] = "null-name"
        }).Build();
        using var host = MongoTestHost.Create(silo =>
        {
            silo.Services.AddSingleton<IMongoClient>(_ => new MongoClient("mongodb://localhost:27017"));
            var provider = Assert.IsAssignableFrom<IProviderBuilder<ISiloBuilder>>(Activator.CreateInstance(registration.Type));
            provider.Configure(silo, null, configuration.GetSection("Storage"));
        });
        var name = ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME;
        Assert.IsType<MongoGrainStorage>(host.Services.GetRequiredKeyedService<IGrainStorage>(name));
        Assert.Equal("null-name", host.Services.GetRequiredService<IOptionsMonitor<MongoDBGrainStorageOptions>>().Get(name).DatabaseName);
        Assert.Equal("", host.Services.GetRequiredService<IOptionsMonitor<MongoDBGrainStorageOptions>>().Get(Options.DefaultName).DatabaseName);
    }

    [Theory]
    [InlineData("Clustering", "Strategy")]
    [InlineData("ClientClustering", "Strategy")]
    [InlineData("Reminders", "CreateIndexes")]
    [InlineData("GrainStorage:Invalid", "CreateShardKeyForCosmos")]
    public void ConfigurationDiscovery_InvalidTypedOptionFailsBinding(string section, string property)
    {
        using var client = new MongoClient("mongodb://localhost:27017");
        var path = section == "ClientClustering" ? "Clustering" : section;
        var configuration = new Dictionary<string, string?>
        {
            [$"Orleans:{path}:ProviderType"] = "MongoDB",
            [$"Orleans:{path}:DatabaseName"] = "valid",
            [$"Orleans:{path}:{property}"] = "not-a-valid-value"
        };
        // Host creation can eagerly resolve options depending on the runtime's validation path.
        var error = Assert.Throws<InvalidOperationException>(() =>
        {
            using var host = section == "ClientClustering"
                ? new HostBuilder().ConfigureAppConfiguration(config => config.AddInMemoryCollection(configuration))
                    .ConfigureServices(services => services.AddSingleton<IMongoClient>(client)).UseOrleansClient().Build()
                : ConfiguredHost(configuration, services => services.AddSingleton<IMongoClient>(client));
            if (section == "Clustering") _ = host.Services.GetRequiredService<IOptions<MongoDBMembershipTableOptions>>().Value;
            else if (section == "ClientClustering") _ = host.Services.GetRequiredService<IOptions<MongoDBGatewayListProviderOptions>>().Value;
            else if (section == "Reminders") _ = host.Services.GetRequiredService<IOptions<MongoDBRemindersOptions>>().Value;
            else _ = host.Services.GetRequiredService<IOptionsMonitor<MongoDBGrainStorageOptions>>().Get("Invalid");
        });
        Assert.Contains(property, error.Message);
        Assert.NotNull(error.InnerException);
    }

    private static IHost ConfiguredHost(Dictionary<string, string?> values,
        Action<IServiceCollection>? configureServices = null, Action<ISiloBuilder>? configureSilo = null)
    {
        var builder = new HostBuilder().ConfigureAppConfiguration(config => config.AddInMemoryCollection(values));
        if (configureServices is not null) builder.ConfigureServices(configureServices);
        return builder.UseOrleans(silo => configureSilo?.Invoke(silo)).Build();
    }

    [Fact]
    public void NamedSerializerRegistration_SelectsJsonDefaultBsonAndBinary()
    {
        using var host = MongoTestHost.Create(silo =>
        {
            silo.Services.AddSingleton<IMongoClient>(_ => new MongoClient("mongodb://localhost:27017"));
            silo.Services.AddKeyedSingleton<IGrainStateSerializer, BsonGrainStateSerializer>("Bson");
            silo.Services.AddKeyedSingleton<IGrainStateSerializer, BinaryGrainStateSerializer>("Binary");
            silo.AddMongoDBGrainStorage("Json", (MongoDBGrainStorageOptions options) => options.DatabaseName = "json");
            silo.AddMongoDBGrainStorage("Bson", (MongoDBGrainStorageOptions options) => options.DatabaseName = "bson");
            silo.AddMongoDBGrainStorage("Binary", (MongoDBGrainStorageOptions options) => options.DatabaseName = "binary");
        });
        var options = host.Services.GetRequiredService<IOptionsMonitor<MongoDBGrainStorageOptions>>();
        Assert.IsType<JsonGrainStateSerializer>(options.Get("Json").GrainStateSerializer);
        Assert.IsType<BsonGrainStateSerializer>(options.Get("Bson").GrainStateSerializer);
        Assert.IsType<BinaryGrainStateSerializer>(options.Get("Binary").GrainStateSerializer);
        Assert.Equal("json", options.Get("Json").DatabaseName);
        Assert.Equal("bson", options.Get("Bson").DatabaseName);
        Assert.Equal("binary", options.Get("Binary").DatabaseName);
        foreach (var name in new[] { "Json", "Bson", "Binary" })
        {
            var storage = host.Services.GetRequiredKeyedService<IGrainStorage>(name);
            Assert.IsType<MongoGrainStorage>(storage);
            Assert.Same(storage, host.Services.GetRequiredKeyedService<IGrainStorage>(name));
        }
    }

    [Fact]
    public void ExplicitSerializer_TakesPrecedenceOverNamedAndGlobal()
    {
        var explicitSerializer = new BsonGrainStateSerializer();
        using var host = MongoTestHost.Create(silo =>
        {
            silo.Services.AddSingleton<IGrainStateSerializer, BinaryGrainStateSerializer>();
            silo.Services.AddKeyedSingleton<IGrainStateSerializer, JsonGrainStateSerializer>("Explicit");
            silo.AddMongoDBGrainStorage("Explicit", (MongoDBGrainStorageOptions options) =>
            {
                options.DatabaseName = "explicit";
                options.GrainStateSerializer = explicitSerializer;
            });
            silo.AddMongoDBGrainStorage("Global");
        });
        var options = host.Services.GetRequiredService<IOptionsMonitor<MongoDBGrainStorageOptions>>();
        Assert.Same(explicitSerializer, options.Get("Explicit").GrainStateSerializer);
        Assert.IsType<BinaryGrainStateSerializer>(options.Get("Global").GrainStateSerializer);
        Assert.Equal("explicit", options.Get("Explicit").DatabaseName);
    }

    [Fact]
    public void DefaultStorageRegistration_UsesOrleansDefaultProviderName()
    {
        using var host = MongoTestHost.Create(silo =>
        {
            silo.Services.AddSingleton<IMongoClient>(_ => new MongoClient("mongodb://localhost:27017"));
            silo.AddMongoDBGrainStorageAsDefault((MongoDBGrainStorageOptions options) => options.DatabaseName = "default");
        });
        Assert.IsType<MongoGrainStorage>(host.Services.GetRequiredKeyedService<IGrainStorage>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
        var options = host.Services.GetRequiredService<IOptionsMonitor<MongoDBGrainStorageOptions>>()
            .Get(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME);
        Assert.Equal("default", options.DatabaseName);
        Assert.IsType<JsonGrainStateSerializer>(options.GrainStateSerializer);
    }

    [Fact]
    public void ParameterlessDefaultStorageRegistration_UsesPreconfiguredNamedOptions()
    {
        using var host = MongoTestHost.Create(silo =>
        {
            silo.Services.AddSingleton<IMongoClient>(_ => new MongoClient("mongodb://localhost:27017"));
            silo.Services.Configure<MongoDBGrainStorageOptions>(
                ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME,
                options =>
                {
                    options.DatabaseName = "preconfigured-default";
                    options.CollectionPrefix = "existing_";
                });
            // Compile-time regression: this call must resolve without a delegate cast or argument.
            silo.AddMongoDBGrainStorageAsDefault();
        });

        var storage = host.Services.GetRequiredKeyedService<IGrainStorage>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME);
        Assert.IsType<MongoGrainStorage>(storage);
        Assert.Same(storage, host.Services.GetRequiredKeyedService<IGrainStorage>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
        var options = host.Services.GetRequiredService<IOptionsMonitor<MongoDBGrainStorageOptions>>()
            .Get(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME);
        Assert.Equal("preconfigured-default", options.DatabaseName);
        Assert.Equal("existing_", options.CollectionPrefix);
        Assert.IsType<JsonGrainStateSerializer>(options.GrainStateSerializer);
    }

    [Fact]
    public void SiloRegistration_ResolvesMembershipAndRemindersWithConfiguredOptions()
    {
        using var host = MongoTestHost.Create(silo =>
        {
            silo.Services.AddSingleton<IMongoClient>(_ => new MongoClient("mongodb://localhost:27017"));
            silo.UseMongoDBClustering(options =>
            {
                options.DatabaseName = "membership";
                options.CollectionPrefix = "cluster_";
                options.Strategy = MongoDBMembershipStrategy.Multiple;
            })
            .UseMongoDBReminders(options =>
            {
                options.DatabaseName = "reminders";
                options.CollectionPrefix = "timer_";
            });
        });
        Assert.IsType<MongoMembershipTable>(host.Services.GetRequiredService<IMembershipTable>());
        Assert.IsType<MongoReminderTable>(host.Services.GetRequiredService<IReminderTable>());
        var membership = host.Services.GetRequiredService<IOptions<MongoDBMembershipTableOptions>>().Value;
        Assert.Equal("membership", membership.DatabaseName);
        Assert.Equal("cluster_", membership.CollectionPrefix);
        Assert.Equal(MongoDBMembershipStrategy.Multiple, membership.Strategy);
        var reminders = host.Services.GetRequiredService<IOptions<MongoDBRemindersOptions>>().Value;
        Assert.Equal("reminders", reminders.DatabaseName);
        Assert.Equal("timer_", reminders.CollectionPrefix);
    }

    [Fact]
    public void ClientRegistration_ResolvesGatewayAndConfiguredStrategy()
    {
        using var host = new HostBuilder().UseOrleansClient(client =>
        {
            client.Services.AddSingleton<IMongoClient>(_ => new MongoClient("mongodb://localhost:27017"));
            client.UseMongoDBClustering(options =>
            {
                options.DatabaseName = "gateway";
                options.Strategy = MongoDBMembershipStrategy.SingleDocument;
                options.CollectionPrefix = "client_";
            });
        }).Build();
        Assert.IsType<MongoGatewayListProvider>(host.Services.GetRequiredService<IGatewayListProvider>());
        var options = host.Services.GetRequiredService<IOptions<MongoDBGatewayListProviderOptions>>().Value;
        Assert.Equal("gateway", options.DatabaseName);
        Assert.Equal("client_", options.CollectionPrefix);
        Assert.Equal(MongoDBMembershipStrategy.SingleDocument, options.Strategy);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void OptionsValidators_RejectMissingDatabaseForEveryProvider(string? database)
    {
        IConfigurationValidator[] validators =
        [
            new Orleans.Clustering.MongoDB.MongoDBOptionsValidator<MongoDBMembershipTableOptions>(Options.Create(new MongoDBMembershipTableOptions { DatabaseName = database! })),
            new Orleans.Clustering.MongoDB.MongoDBOptionsValidator<MongoDBGatewayListProviderOptions>(Options.Create(new MongoDBGatewayListProviderOptions { DatabaseName = database! })),
            new Orleans.Reminders.MongoDB.MongoDBOptionsValidator<MongoDBRemindersOptions>(Options.Create(new MongoDBRemindersOptions { DatabaseName = database! })),
            new MongoDBGrainStorageOptionsValidator(new MongoDBGrainStorageOptions { DatabaseName = database! }, "TestStorage")
        ];
        foreach (var validator in validators)
        {
            var exception = Assert.Throws<OrleansConfigurationException>(validator.ValidateConfiguration);
            Assert.Contains("DatabaseName", exception.Message);
            Assert.Contains("required", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void StorageOptionsValidator_RejectsNullKeyGenerator()
    {
        var options = new MongoDBGrainStorageOptions { DatabaseName = "valid", KeyGenerator = null! };
        var validator = new MongoDBGrainStorageOptionsValidator(options, "TestStorage");
        var exception = Assert.Throws<OrleansConfigurationException>(validator.ValidateConfiguration);
        Assert.Contains("KeyGenerator", exception.Message);
        Assert.Contains("required", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OptionsValidators_AcceptConfiguredDatabaseAndDefaultKeyGenerator()
    {
        var options = new MongoDBGrainStorageOptions { DatabaseName = "valid" };
        new MongoDBGrainStorageOptionsValidator(options, "TestStorage").ValidateConfiguration();
        var id = GrainId.Create("account", "42");
        Assert.Equal(id.ToString(), options.KeyGenerator(id));
        Assert.Equal("Grains", options.CollectionPrefix);
        new Orleans.Clustering.MongoDB.MongoDBOptionsValidator<MongoDBMembershipTableOptions>(Options.Create(new MongoDBMembershipTableOptions { DatabaseName = "valid" })).ValidateConfiguration();
        new Orleans.Clustering.MongoDB.MongoDBOptionsValidator<MongoDBGatewayListProviderOptions>(Options.Create(new MongoDBGatewayListProviderOptions { DatabaseName = "valid" })).ValidateConfiguration();
        new Orleans.Reminders.MongoDB.MongoDBOptionsValidator<MongoDBRemindersOptions>(Options.Create(new MongoDBRemindersOptions { DatabaseName = "valid" })).ValidateConfiguration();
    }

    [Theory]
    [InlineData("Clustering")]
    [InlineData("ClientClustering")]
    [InlineData("Reminders")]
    [InlineData("GrainStorage:Accounts")]
    public void ConfigurationDiscovery_MatchingServiceKeyAndClientNameInferOnlyKeyedDatabase(string section)
    {
        using var client = new MongoClient("mongodb://localhost:27017");
        using var host = CreateConfigurationHost(section, "selected", services =>
        {
            services.AddKeyedSingleton<IMongoClient>("selected", client);
            services.AddKeyedSingleton<IMongoDatabase>("selected", client.GetDatabase("keyed-database"));
            services.AddSingleton<IMongoDatabase>(client.GetDatabase("wrong-global-database"));
        });
        var options = ReadProviderOptions(host.Services, section);
        Assert.Equal("selected", options.ClientName);
        Assert.Equal("keyed-database", options.DatabaseName);
    }

    [Theory]
    [InlineData("Clustering")]
    [InlineData("ClientClustering")]
    [InlineData("Reminders")]
    [InlineData("GrainStorage:Accounts")]
    public void ConfigurationDiscovery_ConflictingServiceKeyAndClientNameFailExplicitly(string section)
    {
        var error = Assert.Throws<OrleansConfigurationException>(() =>
        {
            using var host = CreateConfigurationHost(section, "different-client");
            _ = ReadProviderOptions(host.Services, section);
        });
        Assert.Contains("ClientName", error.Message);
        Assert.Contains("ServiceKey", error.Message);
    }

    [Theory]
    [InlineData("Clustering")]
    [InlineData("ClientClustering")]
    [InlineData("Reminders")]
    [InlineData("GrainStorage:Accounts")]
    public void ConfigurationDiscovery_MissingKeyedDatabaseDoesNotInferGlobalAndStillRequiresDatabaseName(string section)
    {
        using var client = new MongoClient("mongodb://localhost:27017");
        using var host = CreateConfigurationHost(section, "selected", services =>
        {
            services.AddKeyedSingleton<IMongoClient>("selected", client);
            services.AddSingleton<IMongoDatabase>(client.GetDatabase("wrong-global-database"));
        });
        var options = ReadProviderOptions(host.Services, section);
        Assert.Equal("selected", options.ClientName);
        Assert.Equal("", options.DatabaseName);
        var providerNamespace = section switch
        {
            "Clustering" or "ClientClustering" => "Orleans.Clustering.MongoDB",
            "Reminders" => "Orleans.Reminders.MongoDB",
            _ => "Orleans.Persistence.MongoDB"
        };
        var validator = Assert.Single(host.Services.GetServices<IConfigurationValidator>(),
            validator => validator.GetType().Namespace == providerNamespace);
        var error = Assert.Throws<OrleansConfigurationException>(validator.ValidateConfiguration);
        Assert.Contains("DatabaseName", error.Message);
        Assert.Contains("required", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static IHost CreateConfigurationHost(string section, string clientName,
        Action<IServiceCollection>? configureServices = null)
    {
        var path = section == "ClientClustering" ? "Clustering" : section;
        var values = new Dictionary<string, string?>
        {
            [$"Orleans:{path}:ProviderType"] = "MongoDBDatabase",
            [$"Orleans:{path}:ServiceKey"] = "selected",
            [$"Orleans:{path}:ClientName"] = clientName
        };
        var builder = new HostBuilder().ConfigureAppConfiguration(config => config.AddInMemoryCollection(values));
        if (configureServices is not null) builder.ConfigureServices(configureServices);
        return section == "ClientClustering" ? builder.UseOrleansClient().Build() : builder.UseOrleans(silo =>
        {
            if (section != "Clustering") silo.UseLocalhostClustering();
        }).Build();
    }

    private static (string DatabaseName, string? ClientName) ReadProviderOptions(IServiceProvider services, string section)
    {
        if (section == "Clustering")
        {
            var options = services.GetRequiredService<IOptions<MongoDBMembershipTableOptions>>().Value;
            return (options.DatabaseName, options.ClientName);
        }
        if (section == "ClientClustering")
        {
            var options = services.GetRequiredService<IOptions<MongoDBGatewayListProviderOptions>>().Value;
            return (options.DatabaseName, options.ClientName);
        }
        if (section == "Reminders")
        {
            var options = services.GetRequiredService<IOptions<MongoDBRemindersOptions>>().Value;
            return (options.DatabaseName, options.ClientName);
        }
        var storage = services.GetRequiredService<IOptionsMonitor<MongoDBGrainStorageOptions>>().Get("Accounts");
        return (storage.DatabaseName, storage.ClientName);
    }
}
