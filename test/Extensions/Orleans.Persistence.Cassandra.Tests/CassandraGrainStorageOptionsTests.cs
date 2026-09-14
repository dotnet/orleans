using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Cassandra;
using System.Reflection;
using Orleans.Configuration;
using Orleans.Cassandra;
using Orleans.Hosting;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Activators;
using Orleans.Serialization.Serializers;
using Orleans.Storage;
using TestExtensions;
using Xunit;

namespace Orleans.Persistence.Cassandra.Tests;

[TestProvider("None"), TestSuite("BVT"), TestCategory("Cassandra"), TestCategory("Persistence")]
public sealed class CassandraGrainStorageOptionsTests
{
    [Theory]
    [InlineData(ConsistencyLevel.Serial)]
    [InlineData(ConsistencyLevel.LocalSerial)]
    public void SerialConsistencyLevelsAreRejectedForOrdinaryOperations(ConsistencyLevel consistencyLevel)
    {
        var options = new CassandraGrainStorageOptions { ConsistencyLevel = consistencyLevel };
        options.ConfigureClient(static _ => Task.FromResult<ISession>(null!));

        var exception = Assert.Throws<OrleansConfigurationException>(
            () => new CassandraGrainStorageOptionsValidator(options, ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME)
                .ValidateConfiguration());

        Assert.Contains(nameof(CassandraGrainStorageOptions.ConsistencyLevel), exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ConsistencyLevel.Any)]
    [InlineData(ConsistencyLevel.One)]
    [InlineData(ConsistencyLevel.Quorum)]
    [InlineData(ConsistencyLevel.LocalQuorum)]
    [InlineData(ConsistencyLevel.All)]
    public void OrdinaryConsistencyLevelsAreRejectedForSerialOperations(ConsistencyLevel consistencyLevel)
    {
        var options = new CassandraGrainStorageOptions { SerialConsistencyLevel = consistencyLevel };
        options.ConfigureClient(static _ => Task.FromResult<ISession>(null!));

        var exception = Assert.Throws<OrleansConfigurationException>(
            () => new CassandraGrainStorageOptionsValidator(options, ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME)
                .ValidateConfiguration());

        Assert.Contains(nameof(CassandraGrainStorageOptions.SerialConsistencyLevel), exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ConsistencyLevel.Quorum, ConsistencyLevel.Serial)]
    [InlineData(ConsistencyLevel.LocalQuorum, ConsistencyLevel.LocalSerial)]
    [InlineData(ConsistencyLevel.One, ConsistencyLevel.Serial)]
    public void ConsistencyLevelsAcceptValidOrdinaryAndSerialPairs(
        ConsistencyLevel consistencyLevel,
        ConsistencyLevel serialConsistencyLevel)
    {
        var options = new CassandraGrainStorageOptions
        {
            ConsistencyLevel = consistencyLevel,
            SerialConsistencyLevel = serialConsistencyLevel
        };
        options.ConfigureClient(static _ => Task.FromResult<ISession>(null!));

        new CassandraGrainStorageOptionsValidator(options, ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME)
            .ValidateConfiguration();
    }

    [Theory]
    [InlineData("")]
    [InlineData("1table")]
    [InlineData("_table")]
    [InlineData("table-name")]
    [InlineData("table.name")]
    public void InvalidIdentifiersAreRejected(string identifier)
    {
        var options = new CassandraGrainStorageOptions { Keyspace = identifier };
        options.ConfigureClient(static _ => Task.FromResult<ISession>(null!));
        var validator = new CassandraGrainStorageOptionsValidator(options, ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME);

        Assert.Throws<OrleansConfigurationException>(validator.ValidateConfiguration);
    }

    [Theory]
    [InlineData("state_table")]
    [InlineData("StateTable1")]
    [InlineData("grain_state_2")]
    [InlineData("view")]
    [InlineData("default")]
    [InlineData("unset")]
    [InlineData("double")]
    [InlineData("maxwritetime")]
    [InlineData("from")]
    public void ValidIdentifiersAreAccepted(string identifier)
    {
        var options = new CassandraGrainStorageOptions { Keyspace = identifier };
        options.ConfigureClient(static _ => Task.FromResult<ISession>(null!));

        new CassandraGrainStorageOptionsValidator(options, ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME)
            .ValidateConfiguration();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("grain_state")]
    [InlineData("state_1")]
    public void DefaultProviderAllowsOmittedTableName(string? tableName)
    {
        var options = new CassandraGrainStorageOptions { TableName = tableName };
        options.ConfigureClient(static _ => Task.FromResult<ISession>(null!));

        new CassandraGrainStorageOptionsValidator(options, ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME)
            .ValidateConfiguration();
    }

    [Fact]
    public void NamedProviderRequiresTableName()
    {
        var options = new CassandraGrainStorageOptions();
        options.ConfigureClient(static _ => Task.FromResult<ISession>(null!));

        var exception = Assert.Throws<OrleansConfigurationException>(
            () => new CassandraGrainStorageOptionsValidator(options, "Named").ValidateConfiguration());

        Assert.Contains("table name is required", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("_")]
    [InlineData("state-name")]
    public void InvalidTableNamesAreRejected(string? tableName)
    {
        var options = new CassandraGrainStorageOptions { TableName = tableName };
        options.ConfigureClient(static _ => Task.FromResult<ISession>(null!));

        Assert.Throws<OrleansConfigurationException>(
            () => new CassandraGrainStorageOptionsValidator(options, "Named").ValidateConfiguration());
    }

    [Fact]
    public void IdentifiersLongerThanCassandraLimitAreRejected()
    {
        var options = new CassandraGrainStorageOptions { Keyspace = new string('a', 49) };
        options.ConfigureClient(static _ => Task.FromResult<ISession>(null!));

        Assert.Throws<OrleansConfigurationException>(
            () => new CassandraGrainStorageOptionsValidator(options, ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME)
                .ValidateConfiguration());
    }

    [Theory]
    [InlineData("VIEW", "\"view\"")]
    [InlineData("Default_1", "\"default_1\"")]
    [InlineData("maxwritetime", "\"maxwritetime\"")]
    public void ValidIdentifiersAreQuotedAndNormalized(string identifier, string expected)
    {
        Assert.True(CassandraIdentifier.IsValid(identifier));
        Assert.Equal(expected, CassandraIdentifier.Quote(identifier));
    }

    [Fact]
    public void ProviderOwnedClientNormalizesKeyspaceBeforeConnecting()
    {
        var options = new CassandraGrainStorageOptions();

        options.ConfigureClient("Contact Points=127.0.0.1", "StateKeyspace");

        Assert.Equal("statekeyspace", options.Keyspace);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1keyspace")]
    [InlineData("_keyspace")]
    [InlineData("keyspace-name")]
    [InlineData("keyspace.name")]
    [InlineData("\"keyspace\"")]
    [InlineData("keyspace'")]
    public void ProviderOwnedClientRejectsInvalidKeyspaceBeforeConnecting(string keyspace)
    {
        var options = new CassandraGrainStorageOptions();

        Assert.Throws<ArgumentException>(() => options.ConfigureClient("Contact Points=127.0.0.1", keyspace));
    }

    [Fact]
    public void ProviderOwnedClientRejectsOverlongKeyspaceBeforeConnecting()
    {
        var options = new CassandraGrainStorageOptions();

        Assert.Throws<ArgumentException>(() => options.ConfigureClient("Contact Points=127.0.0.1", new string('a', 49)));
    }

    [Fact]
    public void RegistrationAddsDefaultAndNamedProvider()
    {
        var services = CreateServices();
        services.AddCassandraGrainStorageAsDefault(options => options.Configure(o =>
        {
            o.TableName = "default_state";
            o.ConfigureClient(static _ => Task.FromResult<ISession>(null!));
        }));
        services.AddCassandraGrainStorage("Named", options => options.Configure(o =>
        {
            o.TableName = "named_state";
            o.ConfigureClient(static _ => Task.FromResult<ISession>(null!));
        }));

        using var provider = services.BuildServiceProvider();
        var defaultStorage = Assert.IsType<CassandraGrainStorage>(
            provider.GetRequiredKeyedService<IGrainStorage>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
        var namedStorage = Assert.IsType<CassandraGrainStorage>(
            provider.GetRequiredKeyedService<IGrainStorage>("Named"));
        Assert.Same(
            defaultStorage,
            provider.GetRequiredService<IGrainStorage>());
        Assert.Contains(defaultStorage, provider.GetServices<ILifecycleParticipant<ISiloLifecycle>>());
        Assert.Contains(namedStorage, provider.GetServices<ILifecycleParticipant<ISiloLifecycle>>());
        Assert.Equal(
            ServiceLifecycleStage.ApplicationServices,
            provider.GetRequiredService<IOptionsMonitor<CassandraGrainStorageOptions>>()
                .Get("Named").InitStage);
        Assert.NotNull(provider.GetRequiredService<IOptionsMonitor<CassandraGrainStorageOptions>>()
            .Get("Named").GrainStorageSerializer);
        Assert.Equal(2, provider.GetServices<IConfigurationValidator>()
            .OfType<CassandraGrainStorageOptionsValidator>().Count());
    }

    [Fact]
    public void NamedProviderUsesNamedClusterOptions()
    {
        var services = CreateServices();
        services.AddKeyedSingleton<ClusterOptions>("Named", new ClusterOptions { ServiceId = "named-service", ClusterId = "named-cluster" });
        services.AddCassandraGrainStorage("Named", options => options.Configure(o =>
        {
            o.TableName = "named_state";
            o.ConfigureClient(static _ => Task.FromResult<ISession>(null!));
        }));

        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredKeyedService<IGrainStorage>("Named");
        var clusterOptions = typeof(CassandraGrainStorage)
            .GetField("_clusterOptions", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(storage);

        Assert.Equal("named-service", Assert.IsType<ClusterOptions>(clusterOptions).ServiceId);
    }

    [Fact]
    public void SiloBuilderRegistrationUsesItsServiceCollection()
    {
        var builder = new TestSiloBuilder();
        builder.Services.AddOptions();
        builder.Services.AddLogging();
        builder.Services.AddSerializer();
        builder.Services.AddSingleton<IGrainStorageSerializer>(new JsonGrainStorageSerializer(
            new OrleansJsonSerializer(Options.Create(new OrleansJsonSerializerOptions()))));
        builder.Services.Configure<ClusterOptions>(options => options.ServiceId = "cassandra-tests");
        builder.AddCassandraGrainStorage("Named", options => options.Configure(o =>
        {
            o.TableName = "named_state";
            o.ConfigureClient(static _ => Task.FromResult<ISession>(null!));
        }));

        using var provider = builder.Services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredKeyedService<IGrainStorage>("Named"));
    }

    private static ServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.AddLogging();
        services.AddSerializer();
        services.Configure<ClusterOptions>(options =>
        {
            options.ServiceId = "cassandra-tests";
            options.ClusterId = "cassandra-tests";
        });
        services.AddSingleton<IGrainStorageSerializer>(new JsonGrainStorageSerializer(
            new OrleansJsonSerializer(Options.Create(new OrleansJsonSerializerOptions()))));
        services.AddSingleton<IActivatorProvider, TestActivatorProvider>();
        return services;
    }

    private sealed class TestSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();
        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }

    private sealed class TestActivatorProvider : IActivatorProvider
    {
        public IActivator<T> GetActivator<T>() => new Activator<T>();
    }

    private sealed class Activator<T> : IActivator<T>
    {
        public T Create() => Activator.CreateInstance<T>();
    }
}
