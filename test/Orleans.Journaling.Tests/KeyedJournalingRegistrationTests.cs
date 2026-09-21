using System.Buffers;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.Hosting;
using Orleans.Journaling.Json;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Session;
using Orleans.Providers;
using Orleans.Runtime;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class KeyedJournalingRegistrationTests : JournalingTestBase
{
    private const string CustomFormatKey = "custom-test-format";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultProviderOverride_GrainAndFactoryManagersShareStorageAndLifecycle(bool registerOverrideFirst)
    {
        var builder = CreateNamedProviderBuilder();
        var original = new LifecycleJournalStorageProvider();
        var replacement = new LifecycleJournalStorageProvider();
        var grainId = GrainId.Create("test-grain", "default-override");
        var journalId = JournalId.FromGrainId(grainId);
        builder.Services.AddScoped<CompositionTestLifecycle>();
        builder.Services.AddScoped<IGrainContext>(services =>
        {
            var context = Substitute.For<IGrainContext>();
            context.GrainId.Returns(grainId);
            context.ActivationServices.Returns(services);
            context.ObservableLifecycle.Returns(services.GetRequiredService<CompositionTestLifecycle>());
            return context;
        });
        if (registerOverrideFirst)
        {
            builder.Services.AddSingleton<IJournalStorageProvider>(replacement);
        }

        builder.AddJournalStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, _ => original);
        builder.AddVolatileJournalStorage("other");
        if (!registerOverrideFirst)
        {
            builder.Services.AddSingleton<IJournalStorageProvider>(replacement);
        }

        await using var services = builder.Services.BuildServiceProvider();
        var token = TestContext.Current.CancellationToken;
        await using (var scope = services.CreateAsyncScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<IJournaledStateManager>();
            var value = scope.ServiceProvider.GetRequiredKeyedService<IDurableValue<int>>("value");
            var grainLifecycle = scope.ServiceProvider.GetRequiredService<CompositionTestLifecycle>();
            Assert.Equal(1, grainLifecycle.Subscriptions);
            await grainLifecycle.OnStart(token);
            value.Value = 42;
            await manager.WriteStateAsync(token);
        }

        var factory = services.GetRequiredService<IJournaledStateManagerFactory>();
        var keyedFactory = services.GetRequiredKeyedService<IJournaledStateManagerFactory>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME);
        Assert.Same(factory, keyedFactory);
        var codec = services.GetRequiredKeyedService<IDurableValueCommandCodec<int>>(JsonLinesJournalFormat.JournalFormatKey);
        await using (var manager = keyedFactory.CreateStandalone(journalId))
        {
            var value = new DurableValue<int>("value", manager, codec);
            await manager.InitializeAsync(token);
            Assert.Equal(42, value.Value);
            value.Value = 43;
            await manager.WriteStateAsync(token);
        }

        await using (var scope = services.CreateAsyncScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<IJournaledStateManager>();
            var value = scope.ServiceProvider.GetRequiredKeyedService<IDurableValue<int>>("value");
            var grainLifecycle = scope.ServiceProvider.GetRequiredService<CompositionTestLifecycle>();
            Assert.Equal(1, grainLifecycle.Subscriptions);
            await grainLifecycle.OnStart(token);
            Assert.Equal(43, value.Value);
        }

        Assert.Same(replacement, services.GetRequiredService<IJournalStorageProvider>());
        Assert.Same(replacement, services.GetRequiredKeyedService<IJournalStorageProvider>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
        Assert.Same(replacement, services.GetRequiredService<IJournalStorageCatalog>());
        Assert.Same(replacement, services.GetRequiredKeyedService<IJournalStorageCatalog>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
        var catalog = new List<JournalId>();
        await foreach (var entry in services.GetRequiredService<IJournalStorageCatalog>().ListAsync(cancellationToken: token))
        {
            catalog.Add(entry.Id);
        }

        Assert.Equal([journalId], catalog);
        Assert.Null(await original.CreateStorage(journalId).GetMetadataAsync(token));
        Assert.Null(await services.GetRequiredKeyedService<IJournalStorageProvider>("other").CreateStorage(journalId).GetMetadataAsync(token));
        var participant = Assert.Single(services.GetServices<ILifecycleParticipant<ISiloLifecycle>>());
        Assert.Same(replacement, participant);
        var lifecycle = new SiloLifecycleSubject(services.GetRequiredService<ILogger<SiloLifecycleSubject>>());
        participant.Participate(lifecycle);
        Assert.Equal(1, replacement.ParticipationCount);
        Assert.Equal(0, original.ParticipationCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NamedVolatileProviders_KeepDefaultStableAndFactoriesReplayOnlyTheirOwnNamespace(bool registerDefaultFirst)
    {
        var builder = CreateNamedProviderBuilder();
        if (registerDefaultFirst) builder.AddVolatileJournalStorage();
        builder.AddVolatileJournalStorage("jobs-A").AddVolatileJournalStorage("jobs-B");
        if (!registerDefaultFirst) builder.AddVolatileJournalStorage();
        await using var services = builder.Services.BuildServiceProvider();
        var token = TestContext.Current.CancellationToken;
        var storageA = services.GetRequiredKeyedService<IJournalStorageProvider>("jobs-A");
        var storageB = services.GetRequiredKeyedService<IJournalStorageProvider>("jobs-B");
        var defaultStorage = services.GetRequiredService<IJournalStorageProvider>();
        Assert.NotSame(storageA, storageB);
        Assert.NotSame(storageA, defaultStorage);
        Assert.NotSame(storageB, defaultStorage);
        Assert.Same(defaultStorage, services.GetRequiredKeyedService<IJournalStorageProvider>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
        Assert.Same(defaultStorage, services.GetRequiredService<IJournalStorageCatalog>());
        Assert.Same(services.GetRequiredService<IJournaledStateManagerFactory>(),
            services.GetRequiredKeyedService<IJournaledStateManagerFactory>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
        Assert.Same(storageA, services.GetRequiredKeyedService<IJournalStorageCatalog>("jobs-A"));
        Assert.Same(storageB, services.GetRequiredKeyedService<IJournalStorageCatalog>("jobs-B"));
        var id = new JournalId("jobs/shards/isolated");
        var codec = services.GetRequiredKeyedService<IDurableValueCommandCodec<int>>(JsonLinesJournalFormat.JournalFormatKey);
        var factoryA = services.GetRequiredKeyedService<IJournaledStateManagerFactory>("jobs-A");
        var factoryB = services.GetRequiredKeyedService<IJournaledStateManagerFactory>("jobs-B");
        await using (var manager = factoryA.CreateStandalone(id))
        {
            var value = new DurableValue<int>("value", manager, codec);
            await manager.InitializeAsync(token);
            value.Value = 41;
            await manager.WriteStateAsync(token);
        }

        await using (var manager = factoryB.CreateStandalone(id))
        {
            var value = new DurableValue<int>("value", manager, codec);
            await manager.InitializeAsync(token);
            Assert.Equal(0, value.Value);
            value.Value = 100;
            await manager.WriteStateAsync(token);
        }

        // Same identity in two physical namespaces must not share factory storage.
        await using (var manager = factoryA.CreateStandalone(id))
        {
            var value = new DurableValue<int>("value", manager, codec);
            await manager.InitializeAsync(token);
            value.Value++;
            await manager.WriteStateAsync(token);
            Assert.Equal(42, value.Value);
        }

        await using (var manager = factoryB.CreateStandalone(id))
        {
            var value = new DurableValue<int>("value", manager, codec);
            await manager.InitializeAsync(token);
            Assert.Equal(100, value.Value);
        }

        Assert.Null(await defaultStorage.CreateStorage(id).GetMetadataAsync(token));
        var catalogA = new List<JournalId>();
        await foreach (var entry in services.GetRequiredKeyedService<IJournalStorageCatalog>("jobs-A")
            .ListAsync(new JournalCatalogListOptions { Prefix = new JournalId("jobs/shards/") }, token))
        {
            catalogA.Add(entry.Id);
        }
        Assert.Equal(new[] { id }, catalogA);
        await storageA.CreateStorage(id).DeleteAsync(token);
        Assert.Null(await storageA.CreateStorage(id).GetMetadataAsync(token));
        Assert.NotNull(await storageB.CreateStorage(id).GetMetadataAsync(token));
    }

    [Fact]
    public async Task NamedAzureBlobProviders_UseIndependentOptionsAndInitializeEachBindingOnce()
    {
        var builder = CreateNamedProviderBuilder();
        var serviceA = Substitute.For<BlobServiceClient>();
        var serviceB = Substitute.For<BlobServiceClient>();
        var containerA = Substitute.For<BlobContainerClient>();
        var containerB = Substitute.For<BlobContainerClient>();
        var factoryA = Substitute.For<IBlobContainerFactory>();
        var factoryB = Substitute.For<IBlobContainerFactory>();
        serviceA.GetBlobContainerClient("jobs-container-a").Returns(containerA);
        serviceB.GetBlobContainerClient("jobs-container-b").Returns(containerB);
        containerA.CreateIfNotExistsAsync(cancellationToken: Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Response<BlobContainerInfo>>(null!));
        containerB.CreateIfNotExistsAsync(cancellationToken: Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Response<BlobContainerInfo>>(null!));
        var optionsSeen = new List<string>();
        builder.AddAzureBlobJournalStorage("jobs-A", options =>
        {
            options.ContainerName = "jobs-container-a";
            options.BlobServiceClient = serviceA;
            options.BuildContainerFactory = (_, configured) =>
            {
                optionsSeen.Add(configured.ContainerName);
                return factoryA;
            };
        });
        builder.AddAzureBlobJournalStorage("jobs-B", options =>
        {
            options.ContainerName = "jobs-container-b";
            options.BlobServiceClient = serviceB;
            options.BuildContainerFactory = (_, configured) =>
            {
                optionsSeen.Add(configured.ContainerName);
                return factoryB;
            };
        });
        builder.AddAzureBlobJournalStorage("jobs-A", configure: null); // Repetition must not duplicate lifecycle participation.
        await using var services = builder.Services.BuildServiceProvider();
        var a = services.GetRequiredKeyedService<IJournalStorageProvider>("jobs-A");
        var b = services.GetRequiredKeyedService<IJournalStorageProvider>("jobs-B");
        Assert.NotSame(a, b);
        Assert.Same(a, services.GetRequiredKeyedService<IJournalStorageCatalog>("jobs-A"));
        Assert.Same(b, services.GetRequiredKeyedService<IJournalStorageCatalog>("jobs-B"));
        Assert.Null(services.GetService<IJournalStorageProvider>());
        Assert.Equal(new[] { "jobs-container-a", "jobs-container-b" }, optionsSeen);
        var participants = services.GetServices<ILifecycleParticipant<ISiloLifecycle>>().ToArray();
        Assert.Equal(2, participants.Length);
        Assert.Contains(participants, participant => ReferenceEquals(a, participant));
        Assert.Contains(participants, participant => ReferenceEquals(b, participant));
        var lifecycle = new SiloLifecycleSubject(services.GetRequiredService<ILogger<SiloLifecycleSubject>>());
        foreach (var participant in participants) participant.Participate(lifecycle);

        await lifecycle.OnStart(TestContext.Current.CancellationToken);

        await factoryA.Received(1).InitializeAsync(serviceA, Arg.Any<CancellationToken>());
        await factoryB.Received(1).InitializeAsync(serviceB, Arg.Any<CancellationToken>());
        await containerA.Received(1).CreateIfNotExistsAsync(cancellationToken: Arg.Any<CancellationToken>());
        await containerB.Received(1).CreateIfNotExistsAsync(cancellationToken: Arg.Any<CancellationToken>());
        serviceA.DidNotReceive().GetBlobContainerClient("jobs-container-b");
        serviceB.DidNotReceive().GetBlobContainerClient("jobs-container-a");
        await lifecycle.OnStop(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void NamedTableAndS3Options_DoNotBleedAcrossNamesOrDefault()
    {
        var builder = CreateNamedProviderBuilder();
        builder.AddAzureTableJournalStorage(options => options.TableName = "DefaultTable");
        builder.AddAzureTableJournalStorage("A", options => options.TableName = "TableA");
        builder.AddAzureTableJournalStorage("B", options => options.TableName = "TableB");
        builder.AddS3JournalStorage("s3-A", options => options.BucketName = "bucket-a");
        builder.AddS3JournalStorage("s3-B", options => options.BucketName = "bucket-b");
        using var services = builder.Services.BuildServiceProvider();

        var tables = services.GetRequiredService<IOptionsMonitor<AzureTableJournalStorageOptions>>();
        Assert.Equal("TableA", tables.Get("A").TableName);
        Assert.Equal("TableB", tables.Get("B").TableName);
        Assert.Equal("DefaultTable", services.GetRequiredService<IOptions<AzureTableJournalStorageOptions>>().Value.TableName);
        var buckets = services.GetRequiredService<IOptionsMonitor<S3JournalStorageOptions>>();
        Assert.Equal("bucket-a", buckets.Get("s3-A").BucketName);
        Assert.Equal("bucket-b", buckets.Get("s3-B").BucketName);
        Assert.NotSame(tables.Get("A"), tables.Get("B"));
        Assert.NotSame(buckets.Get("s3-A"), buckets.Get("s3-B"));
    }

    [Fact]
    public void NamedRegistration_ConflictingBackendFailsWithProviderNameWithoutChangingOriginalBinding()
    {
        var builder = CreateNamedProviderBuilder();
        builder.AddVolatileJournalStorage("jobs");

        var exception = Assert.Throws<InvalidOperationException>(() => builder.AddAzureBlobJournalStorage("jobs", configure: null));

        Assert.Contains("jobs", exception.Message);
        using var services = builder.Services.BuildServiceProvider();
        Assert.IsType<VolatileJournalStorageProvider>(services.GetRequiredKeyedService<IJournalStorageProvider>("jobs"));
        Assert.Same(services.GetRequiredKeyedService<IJournalStorageProvider>("jobs"),
            services.GetRequiredKeyedService<IJournalStorageCatalog>("jobs"));
    }

    private static TestSiloBuilder CreateNamedProviderBuilder()
    {
        var builder = new TestSiloBuilder();
        builder.Services.AddSerializer();
        builder.Services.AddLogging();
        builder.Services.AddMetrics();
        builder.Services.AddSingleton<OrleansInstruments>();
        builder.Services.Configure<JsonJournalOptions>(options => options.AddTypeInfoResolver(JournalingTestsJsonContext.Default));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddKeyedSingleton<TimeProvider>(KeyedService.AnyKey, static (sp, _) => sp.GetRequiredService<TimeProvider>());
        return builder;
    }

    [Fact]
    public void AddJournalStorage_RegistersJsonFamilyByDefaultAndBinaryFamilyByFormatKey()
    {
        var builder = new TestSiloBuilder();
        builder.Services.AddSerializer();

        builder.AddJournaling();

        using var serviceProvider = builder.Services.BuildServiceProvider();
        var jsonFormat = Assert.IsType<JsonLinesJournalFormat>(serviceProvider.GetRequiredKeyedService<IJournalFormat>(JsonLinesJournalFormat.JournalFormatKey));
        Assert.Same(jsonFormat, serviceProvider.GetRequiredService<IJournalFormat>());
        Assert.Equal("application/jsonl", jsonFormat.MimeType);
        CodecTestHelpers.AssertCommandCodecServiceRegistrations(serviceProvider, JsonLinesJournalFormat.JournalFormatKey);

        var binaryFormat = Assert.IsType<OrleansBinaryJournalFormat>(serviceProvider.GetRequiredKeyedService<IJournalFormat>(OrleansBinaryJournalFormat.JournalFormatKey));
        Assert.Equal("application/octet-stream", binaryFormat.MimeType);
        CodecTestHelpers.AssertCommandCodecServiceRegistrations(serviceProvider, OrleansBinaryJournalFormat.JournalFormatKey);
    }

    [Fact]
    public void StateManager_MissingKeyedFormat_ThrowsClearConfigurationError()
    {
        using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var storage = new VolatileJournalStorage();
        var logger = LoggerFactory.CreateLogger<JournaledStateManager>();
        var options = new JournaledStateManagerOptions
        {
            JournalFormatKey = CustomFormatKey,
            RetirementGracePeriod = ManagerOptions.RetirementGracePeriod
        };
        var exception = Assert.Throws<InvalidOperationException>(() =>
        {
            var shared = new JournaledStateManagerShared(
                logger,
                Options.Create(options),
                TimeProvider.System,
                serviceProvider);

            _ = new JournaledStateManager(shared, storage);
        });

        Assert.Contains(CustomFormatKey, exception.Message);
        Assert.Contains(nameof(IJournalFormat), exception.Message);
    }

    [Fact]
    public void DurableService_ResolvesCommandCodecFromJournalFormatKey()
    {
        var storage = new VolatileJournalStorage();
        var wasUsed = false;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddSingleton(TimeProvider.System);
        services.AddKeyedSingleton<TimeProvider>(KeyedService.AnyKey, static (sp, _) => sp.GetRequiredService<TimeProvider>());
        services.AddScoped<IGrainContext>(sp => new JournalBatchTests.TestGrainContext(GrainId.Create("test-grain", "keyed"), sp));
        services.AddScoped<IJournalStorageProvider>(_ => new TestJournalStorageProvider(storage));
        services.Configure<JournaledStateManagerOptions>(options => options.JournalFormatKey = CustomFormatKey);
        services.AddScoped<JournaledStateManagerShared>();
        services.AddScoped<IJournaledStateManager, JournaledStateManager>();
        services.AddKeyedScoped(typeof(IDurableValue<>), KeyedService.AnyKey, typeof(DurableValue<>));
        services.AddKeyedSingleton<IJournalFormat>(CustomFormatKey, new TestJournalFormat());
        services.AddKeyedSingleton<IDurableDictionaryCommandCodec<string, uint>>(CustomFormatKey, new TestDictionaryCodec<string, uint>());
        services.AddKeyedSingleton<IDurableDictionaryCommandCodec<string, DateTime>>(CustomFormatKey, new TestDictionaryCodec<string, DateTime>());
        services.AddKeyedSingleton<IDurableValueCommandCodec<int>>(CustomFormatKey, (_, _) =>
        {
            wasUsed = true;
            return new TestValueCodec<int>();
        });

        using var serviceProvider = services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();

        _ = scope.ServiceProvider.GetRequiredKeyedService<IDurableValue<int>>("value");

        Assert.True(wasUsed);
        Assert.Equal(1, Assert.IsType<CompositionTestLifecycle>(
            scope.ServiceProvider.GetRequiredService<IGrainContext>().ObservableLifecycle).Subscriptions);
    }

    [Fact]
    public async Task StateConstruction_InjectsActivationScopedCodecBeforeRecovery()
    {
        var builder = CreateNamedProviderBuilder();
        builder.AddVolatileJournalStorage();
        builder.Services.Configure<JournaledStateManagerOptions>(options =>
            options.JournalFormatKey = OrleansBinaryJournalFormat.JournalFormatKey);
        builder.Services.AddScoped<IGrainContext>(services =>
        {
            var context = Substitute.For<IGrainContext>();
            context.GrainId.Returns(GrainId.Create("codec-scope", Guid.NewGuid().ToString("N")));
            context.ActivationServices.Returns(services);
            context.ObservableLifecycle.Returns(new CompositionTestLifecycle());
            return context;
        });
        builder.Services.AddKeyedScoped<IDurableValueCommandCodec<int>>(OrleansBinaryJournalFormat.JournalFormatKey,
            static (services, _) => new OrleansBinaryDurableValueCommandCodec<int>(
                services.GetRequiredService<ICodecProvider>().GetCodec<int>(),
                services.GetRequiredService<SerializerSessionPool>()));
        builder.Services.AddStateMachine<CodecState, CodecState>(static (services, _) =>
            new CodecState(services.GetRequiredKeyedService<IDurableValueCommandCodec<int>>(OrleansBinaryJournalFormat.JournalFormatKey)));
        await using var services = builder.Services.BuildServiceProvider(validateScopes: true);
        await using var first = services.CreateAsyncScope();
        await using var second = services.CreateAsyncScope();
        var owner = first.ServiceProvider.GetRequiredService<IJournaledStateManager>();
        var manager = first.ServiceProvider.GetRequiredService<IDurableStateManager>();
        Assert.Same(owner, manager);
        var state = manager.GetOrAddState<CodecState>("value");
        var codec = state.Codec;
        Assert.Same(first.ServiceProvider.GetRequiredKeyedService<IDurableValueCommandCodec<int>>(OrleansBinaryJournalFormat.JournalFormatKey), codec);
        Assert.NotSame(codec, second.ServiceProvider.GetRequiredService<IDurableStateManager>()
            .GetOrAddState<CodecState>("value").Codec);
        Assert.Throws<InvalidOperationException>(() =>
            services.GetRequiredKeyedService<IDurableValueCommandCodec<int>>(OrleansBinaryJournalFormat.JournalFormatKey));
        Assert.Same(state, manager.GetOrAddState<CodecState>("value"));
        Assert.Same(state, first.ServiceProvider.GetRequiredKeyedService<CodecState>("value"));
        Assert.True(owner.TryGetStateMachine("value", out var registered));
        Assert.Same(state, registered);
        var lifecycle = Assert.IsType<CompositionTestLifecycle>(first.ServiceProvider.GetRequiredService<IGrainContext>().ObservableLifecycle);
        Assert.Equal(1, lifecycle.Subscriptions);
        await lifecycle.OnStart(TestContext.Current.CancellationToken);
        Assert.Equal(0, state.Value);
        state.Value = 42;
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, owner.PendingWriteByteCount);
        await lifecycle.OnStop(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StateConstruction_InjectsNamedFormatCodecOnEmptyJournal()
    {
        var builder = CreateNamedProviderBuilder();
        builder.AddVolatileJournalStorage();
        var customStorage = new VolatileJournalStorage(CustomFormatKey);
        builder.Services.AddKeyedSingleton<IJournalFormat>(CustomFormatKey, static (services, _) =>
            new NamedBinaryJournalFormat(services.GetRequiredService<OrleansBinaryJournalFormat>()));
        builder.Services.AddKeyedSingleton(typeof(IDurableDictionaryCommandCodec<,>), CustomFormatKey,
            typeof(OrleansBinaryDurableDictionaryCommandCodec<,>));
        builder.Services.AddKeyedScoped<IDurableValueCommandCodec<int>>(CustomFormatKey, static (services, _) =>
            new OrleansBinaryDurableValueCommandCodec<int>(
                services.GetRequiredService<ICodecProvider>().GetCodec<int>(),
                services.GetRequiredService<SerializerSessionPool>()));
        builder.Services.AddKeyedSingleton<IJournaledStateManagerFactory>("custom", (services, _) =>
            new JournaledStateManagerFactory(
                new JournaledStateManagerShared(
                    services.GetRequiredService<ILogger<JournaledStateManager>>(),
                    Options.Create(new JournaledStateManagerOptions { JournalFormatKey = CustomFormatKey }),
                    TimeProvider.System,
                    services),
                new TestJournalStorageProvider(customStorage)));
        await using var services = builder.Services.BuildServiceProvider(validateScopes: true);
        await using var dependencies = services.CreateAsyncScope();
        var factory = services.GetRequiredKeyedService<IJournaledStateManagerFactory>("custom");
        await using var defaultManager = services.GetRequiredService<IJournaledStateManagerFactory>().CreateStandalone(new JournalId("default"));
        await using var customManager = factory.CreateStandalone(new JournalId("custom"));
        IJournaledStateManager delegating = new DelegatingStateManager(customManager);
        var codec = dependencies.ServiceProvider.GetRequiredKeyedService<IDurableValueCommandCodec<int>>(CustomFormatKey);
        Assert.Throws<InvalidOperationException>(() =>
            services.GetRequiredKeyedService<IDurableValueCommandCodec<int>>(CustomFormatKey));
        var defaultCodec = services.GetRequiredKeyedService<IDurableValueCommandCodec<int>>(JsonLinesJournalFormat.JournalFormatKey);
        Assert.NotSame(defaultCodec, codec);
        var state = new CodecState(codec);
        Assert.Same(codec, state.Codec);
        delegating.RegisterStateMachine("value", state);
        var defaultState = new CodecState(defaultCodec);
        defaultManager.RegisterStateMachine("value", defaultState);
        await defaultManager.InitializeAsync(TestContext.Current.CancellationToken);
        await delegating.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, state.Value);
        Assert.Empty(customStorage.Segments);
        state.Value = 42;
        defaultState.Value = 7;
        await delegating.WriteStateAsync(TestContext.Current.CancellationToken);
        await defaultManager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Single(customStorage.Segments);
        await using var recovered = factory.CreateStandalone(new JournalId("custom"));
        var recoveredState = new CodecState(codec);
        recovered.RegisterStateMachine("value", recoveredState);
        await recovered.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(42, recoveredState.Value);

        await using var recoveredDefault = services.GetRequiredService<IJournaledStateManagerFactory>().CreateStandalone(new JournalId("default"));
        var recoveredDefaultState = new CodecState(defaultCodec);
        recoveredDefault.RegisterStateMachine("value", recoveredDefaultState);
        await recoveredDefault.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(7, recoveredDefaultState.Value);
    }

    [Fact]
    public void StateConstruction_MissingFormatCodecFails()
    {
        var builder = CreateNamedProviderBuilder();
        builder.AddJournaling();
        using var services = builder.Services.BuildServiceProvider();
        Assert.NotNull(services.GetRequiredKeyedService<IDurableValueCommandCodec<int>>(JsonLinesJournalFormat.JournalFormatKey));
        var exception = Assert.Throws<InvalidOperationException>(() =>
            new CodecState(services.GetRequiredKeyedService<IDurableValueCommandCodec<int>>(CustomFormatKey)));
        Assert.Contains(nameof(IDurableValueCommandCodec<int>), exception.Message);
    }

    [Fact]
    public async Task StateManagerFactory_CreatesManagerForJournalId()
    {
        var storage = new VolatileJournalStorage(OrleansBinaryJournalFormat.JournalFormatKey);
        var builder = new TestSiloBuilder();
        builder.Services.AddSerializer();
        builder.Services.AddLogging();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddKeyedSingleton<TimeProvider>(KeyedService.AnyKey, static (sp, _) => sp.GetRequiredService<TimeProvider>());
        builder.AddJournaling();
        builder.Services.Configure<JournaledStateManagerOptions>(options => options.JournalFormatKey = OrleansBinaryJournalFormat.JournalFormatKey);
        builder.Services.AddScoped<IJournalStorageProvider>(_ => new TestJournalStorageProvider(storage));

        using var serviceProvider = builder.Services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IJournaledStateManagerFactory>()
            .CreateStandalone(new JournalId("on-demand-journal"));
        await using (manager.ConfigureAwait(false))
        {
            var codecProvider = scope.ServiceProvider.GetRequiredService<ICodecProvider>();
            var sessionPool = scope.ServiceProvider.GetRequiredService<SerializerSessionPool>();
            var value = new DurableValue<int>(
                "value",
                manager,
                new OrleansBinaryDurableValueCommandCodec<int>(codecProvider.GetCodec<int>(), sessionPool));

            await manager.InitializeAsync(CancellationToken.None);
            value.Value = 42;
            await manager.WriteStateAsync(CancellationToken.None);
        }

        Assert.NotEmpty(storage.Segments);
    }

    private sealed class TestJournalStorageProvider(IJournalStorage storage) : IJournalStorageProvider
    {
        public IJournalStorage CreateStorage(JournalId journalId) => storage;
    }

    private sealed class NamedBinaryJournalFormat(IJournalFormat inner) : IJournalFormat
    {
        public string FormatKey => CustomFormatKey;
        public string? MimeType => inner.MimeType;
        public JournalBufferWriter CreateWriter() => inner.CreateWriter();
        public void Replay(JournalBufferReader input, JournalReplayContext context) => inner.Replay(input, context);
    }

    private sealed class CodecState(IDurableValueCommandCodec<int> codec) : IStateMachine, IDurableValueCommandHandler<int>
    {
        public IDurableValueCommandCodec<int> Codec { get; } = codec;
        public int Value { get; set; }
        public void Reset(JournalStreamWriter writer) => Value = 0;
        public void WritePendingEntries(JournalStreamWriter writer) => Codec.WriteSet(Value, writer);
        public void WriteSnapshot(JournalStreamWriter writer) => Codec.WriteSet(Value, writer);
        public void ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
            context.GetRequiredCommandCodec(entry.FormatKey, Codec).Apply(entry.Reader, this);
        public void ApplySet(int value) => Value = value;
    }

    private sealed class DelegatingStateManager(IJournaledStateManager inner) : IJournaledStateManager
    {
        public ValueTask InitializeAsync(CancellationToken cancellationToken) => inner.InitializeAsync(cancellationToken);
        public void RegisterStateMachine(string name, IStateMachine state) => inner.RegisterStateMachine(name, state);
        public bool TryGetStateMachine(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IStateMachine? state)
            => inner.TryGetStateMachine(name, out state);
        public ValueTask WriteStateAsync(CancellationToken cancellationToken) => inner.WriteStateAsync(cancellationToken);
        public ValueTask DeleteStateAsync(CancellationToken cancellationToken) => inner.DeleteStateAsync(cancellationToken);
    }

    private sealed class LifecycleJournalStorageProvider : IJournalStorageProvider, IJournalStorageCatalog, ILifecycleParticipant<ISiloLifecycle>
    {
        private readonly VolatileJournalStorageProvider _storage = new();

        public int ParticipationCount { get; private set; }

        public IJournalStorage CreateStorage(JournalId journalId) => _storage.CreateStorage(journalId);

        public IAsyncEnumerable<JournalCatalogEntry> ListAsync(JournalCatalogListOptions? options = null, CancellationToken cancellationToken = default)
            => _storage.ListAsync(options, cancellationToken);

        public void Participate(ISiloLifecycle lifecycle) => ParticipationCount++;
    }

    private sealed class TestSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }

    private sealed class TestJournalFormat : IJournalFormat
    {
        public string FormatKey => CustomFormatKey;

        public string? MimeType => null;

        public JournalBufferWriter CreateWriter() => new OrleansBinaryJournalBufferWriter();

        public void Replay(JournalBufferReader input, JournalReplayContext context) => throw new NotSupportedException();
    }

    private sealed class TestDictionaryCodec<TKey, TValue> : IDurableDictionaryCommandCodec<TKey, TValue>
        where TKey : notnull
    {
        public void WriteSet(TKey key, TValue value, JournalStreamWriter writer) => throw new NotSupportedException();

        public void WriteRemove(TKey key, JournalStreamWriter writer) => throw new NotSupportedException();

        public void WriteClear(JournalStreamWriter writer) => throw new NotSupportedException();

        public void WriteSnapshot(IReadOnlyCollection<KeyValuePair<TKey, TValue>> items, JournalStreamWriter writer) => throw new NotSupportedException();

        public void Apply(JournalBufferReader input, IDurableDictionaryCommandHandler<TKey, TValue> consumer) => throw new NotSupportedException();
    }

    private sealed class TestValueCodec<T> : IDurableValueCommandCodec<T>
    {
        public void WriteSet(T value, JournalStreamWriter writer) => throw new NotSupportedException();

        public void Apply(JournalBufferReader input, IDurableValueCommandHandler<T> consumer) => throw new NotSupportedException();
    }
}
