using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Persistence.Cosmos;
using Orleans.Runtime;

namespace Tester.Cosmos.Persistence;

[TestCategory("Cosmos"), TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("Cosmos")]
[TestArea("Persistence")]
public class CosmosHostingExtensionsTests
{
    [Fact]
    public void AddCosmosGrainStorage_ProviderTypesAreRegisteredByKey()
    {
        using var host = new HostBuilder()
            .UseOrleans(builder =>
            {
                builder.AddCosmosGrainStorage("first", _ => { }, typeof(FirstDocumentIdProvider));
                builder.AddCosmosGrainStorage("second", _ => { }, typeof(SecondDocumentIdProvider));
            })
            .Build();

        Assert.IsType<FirstDocumentIdProvider>(host.Services.GetRequiredKeyedService<IDocumentIdProvider>("first"));
        Assert.IsType<SecondDocumentIdProvider>(host.Services.GetRequiredKeyedService<IDocumentIdProvider>("second"));
    }

    [Fact]
    public void CosmosGrainStorageProviderBuilder_UsesConfiguredDocumentIdProviderFromDI()
    {
        const string storageName = "configured-storage";
        const string documentIdProviderKey = "custom-document-ids";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cosmos:DatabaseName"] = "configured-database",
                ["Cosmos:DocumentIdProviderKey"] = documentIdProviderKey
            })
            .Build();

        using var host = new HostBuilder()
            .UseOrleans(builder =>
            {
                builder.Services.AddSingleton<DocumentIdProviderDependency>();
                builder.Services.AddKeyedSingleton<IDocumentIdProvider, ConfiguredDocumentIdProvider>(documentIdProviderKey);
                new CosmosGrainStorageProviderBuilder().Configure(builder, storageName, configuration.GetSection("Cosmos"));
            })
            .Build();

        var configuredProvider = host.Services.GetRequiredKeyedService<IDocumentIdProvider>(storageName);
        var registeredProvider = host.Services.GetRequiredKeyedService<IDocumentIdProvider>(documentIdProviderKey);
        var options = host.Services.GetRequiredService<IOptionsMonitor<CosmosGrainStorageOptions>>().Get(storageName);

        Assert.Same(registeredProvider, configuredProvider);
        Assert.Same(
            host.Services.GetRequiredService<DocumentIdProviderDependency>(),
            Assert.IsType<ConfiguredDocumentIdProvider>(configuredProvider).Dependency);
        Assert.Equal("configured-database", options.DatabaseName);
    }

    [Fact]
    public void CosmosGrainStorageProviderBuilder_ThrowsWhenNamedConnectionStringIsMissing()
    {
        const string storageName = "configured-storage";
        const string connectionName = "missing";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cosmos:ConnectionName"] = connectionName
            })
            .Build();

        using var host = new HostBuilder()
            .UseOrleans(builder =>
                new CosmosGrainStorageProviderBuilder().Configure(builder, storageName, configuration.GetSection("Cosmos")))
            .Build();

        var options = host.Services.GetRequiredService<IOptionsMonitor<CosmosGrainStorageOptions>>();
        var exception = Assert.Throws<InvalidOperationException>(() => options.Get(storageName));

        Assert.Equal($"Connection string '{connectionName}' was not found.", exception.Message);
    }

#pragma warning disable CS0618 // Type or member is obsolete
    [Fact]
    public void AddCosmosGrainStorage_LegacyPartitionKeyProvidersAreRegisteredByKey()
    {
        using var host = new HostBuilder()
            .UseOrleans(builder =>
            {
                builder.AddCosmosGrainStorage<FirstPartitionKeyProvider>(
                    "legacy-generic",
                    (CosmosGrainStorageOptions _) => { });
                builder.AddCosmosGrainStorage(
                    "legacy-type",
                    (CosmosGrainStorageOptions _) => { },
                    customPartitionKeyProviderType: typeof(SecondPartitionKeyProvider));
                builder.AddCosmosGrainStorage<FirstPartitionKeyProvider>("legacy-options-generic");
                builder.AddCosmosGrainStorage(
                    "legacy-options-type",
                    customPartitionKeyProviderType: typeof(SecondPartitionKeyProvider));
            })
            .Build();

        Assert.IsType<FirstPartitionKeyProvider>(host.Services.GetRequiredKeyedService<IPartitionKeyProvider>("legacy-generic"));
        Assert.IsType<SecondPartitionKeyProvider>(host.Services.GetRequiredKeyedService<IPartitionKeyProvider>("legacy-type"));
        Assert.IsType<FirstPartitionKeyProvider>(host.Services.GetRequiredKeyedService<IPartitionKeyProvider>("legacy-options-generic"));
        Assert.IsType<SecondPartitionKeyProvider>(host.Services.GetRequiredKeyedService<IPartitionKeyProvider>("legacy-options-type"));
    }

    [Fact]
    public async Task DefaultDocumentIdProvider_UsesLegacyPartitionKeyProvider()
    {
        var provider = new DefaultDocumentIdProvider(
            Microsoft.Extensions.Options.Options.Create(new ClusterOptions { ServiceId = "service" }),
            new FirstPartitionKeyProvider());

        var identifiers = await provider.GetDocumentIdentifiers("grain-type", GrainId.Create("type", "key"));

        Assert.Equal("first", identifiers.PartitionKey);
        Assert.Equal("service__type_key", identifiers.DocumentId);
    }

#pragma warning restore CS0618 // Type or member is obsolete

    [Fact]
    public void AddCosmosGrainStorage_GenericConfigureOptions_NullBuilder_ThrowsArgumentNullException()
    {
        ISiloBuilder builder = null!;
        var configureInvoked = false;

        var exception = Assert.Throws<ArgumentNullException>(() =>
            builder.AddCosmosGrainStorage<FirstDocumentIdProvider>(
                "storage",
                (Action<CosmosGrainStorageOptions>)(_ => configureInvoked = true)));

        Assert.Equal("builder", exception.ParamName);
        Assert.False(configureInvoked);
    }

    [Fact]
    public void AddCosmosGrainStorage_TypeConfigureOptions_NullBuilder_ThrowsArgumentNullException()
    {
        ISiloBuilder builder = null!;
        var configureInvoked = false;

        var exception = Assert.Throws<ArgumentNullException>(() =>
            builder.AddCosmosGrainStorage(
                "storage",
                (Action<CosmosGrainStorageOptions>)(_ => configureInvoked = true),
                typeof(FirstDocumentIdProvider)));

        Assert.Equal("builder", exception.ParamName);
        Assert.False(configureInvoked);
    }

    [Fact]
    public void AddCosmosGrainStorage_PlainConfigureOptions_NullBuilder_ThrowsArgumentNullException()
    {
        ISiloBuilder builder = null!;
        var configureInvoked = false;

        var exception = Assert.Throws<ArgumentNullException>(() =>
            builder.AddCosmosGrainStorage(
                "storage",
                (Action<CosmosGrainStorageOptions>)(_ => configureInvoked = true)));

        Assert.Equal("builder", exception.ParamName);
        Assert.False(configureInvoked);
    }

    [Fact]
    public void AddCosmosGrainStorage_GenericOptionsBuilder_NullBuilder_ThrowsArgumentNullException()
    {
        ISiloBuilder builder = null!;
        var configureInvoked = false;

        var exception = Assert.Throws<ArgumentNullException>(() =>
            builder.AddCosmosGrainStorage<FirstDocumentIdProvider>(
                "storage",
                (Action<OptionsBuilder<CosmosGrainStorageOptions>>)(_ => configureInvoked = true)));

        Assert.Equal("builder", exception.ParamName);
        Assert.False(configureInvoked);
    }

    [Fact]
    public void AddCosmosGrainStorage_TypeOptionsBuilder_NullBuilder_ThrowsArgumentNullException()
    {
        ISiloBuilder builder = null!;
        var configureInvoked = false;

        var exception = Assert.Throws<ArgumentNullException>(() =>
            builder.AddCosmosGrainStorage(
                "storage",
                typeof(FirstDocumentIdProvider),
                (Action<OptionsBuilder<CosmosGrainStorageOptions>>)(_ => configureInvoked = true)));

        Assert.Equal("builder", exception.ParamName);
        Assert.False(configureInvoked);
    }

    [Fact]
    public void AddCosmosGrainStorage_PlainOptionsBuilder_NullBuilder_ThrowsArgumentNullException()
    {
        ISiloBuilder builder = null!;
        var configureInvoked = false;

        var exception = Assert.Throws<ArgumentNullException>(() =>
            builder.AddCosmosGrainStorage(
                "storage",
                (Action<OptionsBuilder<CosmosGrainStorageOptions>>)(_ => configureInvoked = true)));

        Assert.Equal("builder", exception.ParamName);
        Assert.False(configureInvoked);
    }

    [Fact]
    public void AddCosmosGrainStorage_PlainNamedRegistration_ConfiguresNamedOptions()
    {
        const string storageName = "plain-named-storage";
        const string databaseName = "phase-two-database";
        var builder = new TestSiloBuilder();

        var result = builder.AddCosmosGrainStorage(
            storageName,
            (Action<CosmosGrainStorageOptions>)(options => options.DatabaseName = databaseName));
        using var services = builder.Services.BuildServiceProvider();

        var options = services.GetRequiredService<IOptionsMonitor<CosmosGrainStorageOptions>>().Get(storageName);

        Assert.Same(builder, result);
        Assert.Equal(databaseName, options.DatabaseName);
    }

    private sealed class FirstDocumentIdProvider : IDocumentIdProvider
    {
        public ValueTask<(string DocumentId, string PartitionKey)> GetDocumentIdentifiers(string grainType, GrainId grainId) => default;
    }

    private sealed class SecondDocumentIdProvider : IDocumentIdProvider
    {
        public ValueTask<(string DocumentId, string PartitionKey)> GetDocumentIdentifiers(string grainType, GrainId grainId) => default;
    }

    private sealed class ConfiguredDocumentIdProvider(DocumentIdProviderDependency dependency) : IDocumentIdProvider
    {
        public DocumentIdProviderDependency Dependency { get; } = dependency;

        public ValueTask<(string DocumentId, string PartitionKey)> GetDocumentIdentifiers(string grainType, GrainId grainId) => default;
    }

    private sealed class DocumentIdProviderDependency
    {
    }

    private sealed class TestSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }

#pragma warning disable CS0618 // Type or member is obsolete
    private sealed class FirstPartitionKeyProvider : IPartitionKeyProvider
    {
        public ValueTask<string> GetPartitionKey(string grainType, GrainId grainId) => new("first");
    }

    private sealed class SecondPartitionKeyProvider : IPartitionKeyProvider
    {
        public ValueTask<string> GetPartitionKey(string grainType, GrainId grainId) => new("second");
    }
#pragma warning restore CS0618 // Type or member is obsolete
}
