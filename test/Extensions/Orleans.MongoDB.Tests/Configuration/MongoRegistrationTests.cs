using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Orleans.Hosting;
using Orleans.Messaging;
using Orleans.Providers;
using Orleans.Providers.MongoDB.Configuration;
using Orleans.Providers.MongoDB.Membership;
using Orleans.Providers.MongoDB.Reminders;
using Orleans.Providers.MongoDB.StorageProviders;
using Orleans.Providers.MongoDB.StorageProviders.Serializers;
using Orleans.Providers.MongoDB.Utils;
using Orleans.Runtime;
using Orleans.Storage;
using Xunit;

namespace Orleans.MongoDB.Tests.Configuration;

[TestProvider("MongoDB"), TestSuite("BVT"), TestArea("Configuration")]
public sealed class MongoRegistrationTests
{
    [Fact]
    public void ConfigurationDiscovery_SiloBindsAllProvidersAndReusesHostRegisteredClient()
    {
        using var client = new MongoClient("mongodb://localhost:27017");
        using var host = ConfiguredHost(new Dictionary<string, string?>
        {
            ["Orleans:Clustering:ProviderType"] = "MongoDB",
            ["Orleans:Clustering:DatabaseName"] = "configured-membership",
            ["Orleans:Clustering:CollectionPrefix"] = "members_",
            ["Orleans:Clustering:Strategy"] = "Multiple",
            ["Orleans:Reminders:ProviderType"] = "MongoDB",
            ["Orleans:Reminders:DatabaseName"] = "configured-reminders",
            ["Orleans:Reminders:CollectionPrefix"] = "timers_",
            ["Orleans:Reminders:CreateIndexes"] = "false",
            ["Orleans:GrainStorage:Accounts:ProviderType"] = "MongoDB",
            ["Orleans:GrainStorage:Accounts:DatabaseName"] = "configured-accounts",
            ["Orleans:GrainStorage:Accounts:CollectionPrefix"] = "accounts_",
            ["Orleans:GrainStorage:Accounts:ClientName"] = "host-client",
            ["Orleans:GrainStorage:Archive:ProviderType"] = "MongoDB",
            ["Orleans:GrainStorage:Archive:DatabaseName"] = "configured-archive"
        }, services => services.AddSingleton<IMongoClient>(client));
        Assert.IsType<MongoMembershipTable>(host.Services.GetRequiredService<IMembershipTable>());
        Assert.IsType<MongoReminderTable>(host.Services.GetRequiredService<IReminderTable>());
        var membership = host.Services.GetRequiredService<IOptions<MongoDBMembershipTableOptions>>().Value;
        Assert.Equal("configured-membership", membership.DatabaseName);
        Assert.Equal("members_", membership.CollectionPrefix);
        Assert.Equal(MongoDBMembershipStrategy.Multiple, membership.Strategy);
        var reminders = host.Services.GetRequiredService<IOptions<MongoDBRemindersOptions>>().Value;
        Assert.Equal("configured-reminders", reminders.DatabaseName);
        Assert.Equal("timers_", reminders.CollectionPrefix);
        Assert.False(reminders.CreateIndexes);
        var monitor = host.Services.GetRequiredService<IOptionsMonitor<MongoDBGrainStorageOptions>>();
        Assert.Equal("configured-accounts", monitor.Get("Accounts").DatabaseName);
        Assert.Equal("accounts_", monitor.Get("Accounts").CollectionPrefix);
        Assert.Equal("host-client", monitor.Get("Accounts").ClientName);
        Assert.Equal("configured-archive", monitor.Get("Archive").DatabaseName);
        Assert.Equal("Grains", monitor.Get("Archive").CollectionPrefix);
        Assert.IsType<JsonGrainStateSerializer>(monitor.Get("Accounts").GrainStateSerializer);
        var accounts = host.Services.GetRequiredKeyedService<IGrainStorage>("Accounts");
        var archive = host.Services.GetRequiredKeyedService<IGrainStorage>("Archive");
        Assert.IsType<MongoGrainStorage>(accounts);
        Assert.IsType<MongoGrainStorage>(archive);
        Assert.NotSame(accounts, archive);
        Assert.Same(client, host.Services.GetRequiredService<IMongoClientFactory>().Create("Membership"));
        Assert.Same(client, host.Services.GetRequiredService<IMongoClientFactory>().Create(monitor.Get("Accounts"), "Storage"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfigurationDiscovery_ClientBindsGatewayOptionsAndUsesConfiguredClient(bool explicitClient)
    {
        using var client = new MongoClient("mongodb://localhost:27017");
        var builder = new HostBuilder().ConfigureAppConfiguration(config => config.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Orleans:Clustering:ProviderType"] = "MongoDB",
                ["Orleans:Clustering:DatabaseName"] = "configured-gateway",
                ["Orleans:Clustering:CollectionPrefix"] = "gateway_",
                ["Orleans:Clustering:Strategy"] = "MultipleDeprecated"
            }));
        if (!explicitClient) builder.ConfigureServices(services => services.AddSingleton<IMongoClient>(client));
        builder.UseOrleansClient(orleans =>
        {
            if (explicitClient) orleans.UseMongoDBClient("mongodb://localhost:27017");
        });
        using var host = builder.Build();
        Assert.IsType<MongoGatewayListProvider>(host.Services.GetRequiredService<IGatewayListProvider>());
        var options = host.Services.GetRequiredService<IOptions<MongoDBGatewayListProviderOptions>>().Value;
        Assert.Equal("configured-gateway", options.DatabaseName);
        Assert.Equal("gateway_", options.CollectionPrefix);
        Assert.Equal(MongoDBMembershipStrategy.MultipleDeprecated, options.Strategy);
        var resolved = host.Services.GetRequiredService<IMongoClient>();
        Assert.Same(resolved, host.Services.GetRequiredService<IMongoClientFactory>().Create(options, "Gateway"));
        if (!explicitClient) Assert.Same(client, resolved);
    }

    [Fact]
    public void ConfigurationDiscovery_DefaultNamedStoragePreservesDefaultsAndExplicitClient()
    {
        using var host = ConfiguredHost(new Dictionary<string, string?>
        {
            [$"Orleans:GrainStorage:{ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME}:ProviderType"] = "MongoDB",
            [$"Orleans:GrainStorage:{ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME}:DatabaseName"] = "default-config"
        }, configureSilo: silo => silo
            .UseLocalhostClustering()
            .UseMongoDBClient("mongodb://localhost:27017"));
        var name = ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME;
        Assert.IsType<MongoGrainStorage>(host.Services.GetRequiredKeyedService<IGrainStorage>(name));
        var options = host.Services.GetRequiredService<IOptionsMonitor<MongoDBGrainStorageOptions>>().Get(name);
        Assert.Equal("default-config", options.DatabaseName);
        Assert.Equal("Grains", options.CollectionPrefix);
        Assert.False(options.CreateShardKeyForCosmos);
        Assert.IsType<JsonGrainStateSerializer>(options.GrainStateSerializer);
        Assert.Same(host.Services.GetRequiredService<IMongoClient>(),
            host.Services.GetRequiredService<IMongoClientFactory>().Create(options, "Storage"));
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
            silo.UseMongoDBClient("mongodb://localhost:27017");
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
            silo.UseMongoDBClient("mongodb://localhost:27017");
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
            silo.UseMongoDBClient("mongodb://localhost:27017");
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
            silo.UseMongoDBClient("mongodb://localhost:27017");
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
        using var host = MongoTestHost.Create(silo => silo
            .UseMongoDBClient("mongodb://localhost:27017")
            .UseMongoDBClustering(options =>
            {
                options.DatabaseName = "membership";
                options.CollectionPrefix = "cluster_";
                options.Strategy = MongoDBMembershipStrategy.Multiple;
            })
            .UseMongoDBReminders(options =>
            {
                options.DatabaseName = "reminders";
                options.CollectionPrefix = "timer_";
            }));
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
        using var host = new HostBuilder().UseOrleansClient(client => client
            .UseMongoDBClient("mongodb://localhost:27017")
            .UseMongoDBClustering(options =>
            {
                options.DatabaseName = "gateway";
                options.Strategy = MongoDBMembershipStrategy.SingleDocument;
                options.CollectionPrefix = "client_";
            })).Build();
        Assert.IsType<MongoGatewayListProvider>(host.Services.GetRequiredService<IGatewayListProvider>());
        var options = host.Services.GetRequiredService<IOptions<MongoDBGatewayListProviderOptions>>().Value;
        Assert.Equal("gateway", options.DatabaseName);
        Assert.Equal("client_", options.CollectionPrefix);
        Assert.Equal(MongoDBMembershipStrategy.SingleDocument, options.Strategy);
    }

    [Fact]
    public void ClientRegistration_IsLazyAndReusesSingleClient()
    {
        var calls = 0;
        var services = new ServiceCollection();
        services.AddMongoDBClient(_ =>
        {
            calls++;
            return MongoClientSettings.FromConnectionString("mongodb://localhost:27017");
        });
        services.AddMongoDBClient(_ => throw new InvalidOperationException("Duplicate registration should not replace the client."));
        Assert.Equal(0, calls);
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IMongoClientFactory>();
        var first = factory.Create("Membership");
        Assert.Same(first, factory.Create("Storage"));
        Assert.Same(first, provider.GetRequiredService<IMongoClient>());
        Assert.Equal(1, calls);
    }

    [Fact]
    public void ClientRegistration_RejectsNullSettingsFactoryBeforeRegistration()
    {
        var services = new ServiceCollection();
        var exception = Assert.Throws<ArgumentNullException>(() =>
            services.AddMongoDBClient((Func<IServiceProvider, MongoClientSettings>)null!));
        Assert.Equal("settingsFactory", exception.ParamName);
        Assert.Empty(services);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void OptionsValidators_RejectMissingDatabaseForEveryProvider(string? database)
    {
        IConfigurationValidator[] validators =
        [
            new MongoDBOptionsValidator<MongoDBMembershipTableOptions>(Options.Create(new MongoDBMembershipTableOptions { DatabaseName = database! })),
            new MongoDBOptionsValidator<MongoDBGatewayListProviderOptions>(Options.Create(new MongoDBGatewayListProviderOptions { DatabaseName = database! })),
            new MongoDBOptionsValidator<MongoDBRemindersOptions>(Options.Create(new MongoDBRemindersOptions { DatabaseName = database! })),
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
        new MongoDBOptionsValidator<MongoDBMembershipTableOptions>(Options.Create(new MongoDBMembershipTableOptions { DatabaseName = "valid" })).ValidateConfiguration();
        new MongoDBOptionsValidator<MongoDBGatewayListProviderOptions>(Options.Create(new MongoDBGatewayListProviderOptions { DatabaseName = "valid" })).ValidateConfiguration();
        new MongoDBOptionsValidator<MongoDBRemindersOptions>(Options.Create(new MongoDBRemindersOptions { DatabaseName = "valid" })).ValidateConfiguration();
    }
}
