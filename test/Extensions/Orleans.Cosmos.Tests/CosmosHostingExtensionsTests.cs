using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Persistence.Cosmos;
using Orleans.Runtime;

namespace Tester.Cosmos.Persistence;

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
