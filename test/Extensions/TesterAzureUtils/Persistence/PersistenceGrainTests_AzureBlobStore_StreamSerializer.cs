using Azure;
using Orleans.Configuration;
using Orleans.Storage;
using Orleans.TestingHost;
using System.Threading;
using UnitTests.GrainInterfaces;
using Xunit;
using Xunit.Abstractions;
using static Tester.AzureUtils.Persistence.Base_PersistenceGrainTests_AzureStore;

namespace Tester.AzureUtils.Persistence;

/// <summary>
/// Validates the default Azure blob storage serializer behavior when stream support is available.
/// </summary>
[TestCategory("Persistence"), TestCategory("AzureStorage")]
public class PersistenceGrainTests_AzureBlobStore_StreamSerializer : IClassFixture<PersistenceGrainTests_AzureBlobStore_StreamSerializer.Fixture>
{
    private readonly Fixture fixture;

    public PersistenceGrainTests_AzureBlobStore_StreamSerializer(ITestOutputHelper output, Fixture fixture)
    {
        this.fixture = fixture;
        fixture.EnsurePreconditionsMet();
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task AzureBlobStorage_DefaultsToBinaryWritesAndStreamReads()
    {
        CountingGrainStorageSerializer.Reset();

        var grain = fixture.GrainFactory.GetGrain<IGrainStorageTestGrain>(Guid.NewGuid(), "UnitTests.Grains");
        await grain.DoWrite(1);
        _ = await grain.DoRead();

        Assert.True(CountingGrainStorageSerializer.BinarySerializeCount > 0);
        Assert.True(CountingGrainStorageSerializer.StreamDeserializeCount > 0);
        Assert.Equal(0, CountingGrainStorageSerializer.StreamSerializeCount);
        Assert.Equal(0, CountingGrainStorageSerializer.BinaryDeserializeCount);
    }

    public class Fixture : BaseAzureTestClusterFixture
    {
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = 1;
            builder.Options.UseTestClusterMembership = false;
            builder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();
            builder.AddSiloBuilderConfigurator<StorageSiloBuilderConfigurator>();
            builder.AddClientBuilderConfigurator<ClientBuilderConfigurator>();
        }

        private class StorageSiloBuilderConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder.AddAzureBlobGrainStorage("GrainStorageForTest", optionsBuilder =>
                {
                    optionsBuilder.Configure(options =>
                    {
                        options.ConfigureTestDefaults();
                        options.UsePooledBufferForReads = true;
                    });
                    optionsBuilder.Configure<IGrainStorageSerializer>((options, serializer) =>
                    {
                        options.GrainStorageSerializer = new CountingGrainStorageSerializer(serializer);
                    });
                });
            }
        }
    }

    private sealed class CountingGrainStorageSerializer : IGrainStorageSerializer, IGrainStorageStreamingSerializer
    {
        private readonly IGrainStorageSerializer _inner;
        private readonly IGrainStorageStreamingSerializer _innerStream;

        public static long BinarySerializeCount;
        public static long BinaryDeserializeCount;
        public static long StreamSerializeCount;
        public static long StreamDeserializeCount;

        public static void Reset()
        {
            Interlocked.Exchange(ref BinarySerializeCount, 0);
            Interlocked.Exchange(ref BinaryDeserializeCount, 0);
            Interlocked.Exchange(ref StreamSerializeCount, 0);
            Interlocked.Exchange(ref StreamDeserializeCount, 0);
        }

        public CountingGrainStorageSerializer(IGrainStorageSerializer inner)
        {
            _inner = inner;
            _innerStream = inner as IGrainStorageStreamingSerializer
                ?? throw new InvalidOperationException("Inner serializer must support stream operations for this test.");
        }

        public BinaryData Serialize<T>(T input)
        {
            Interlocked.Increment(ref BinarySerializeCount);
            return _inner.Serialize(input);
        }

        public T Deserialize<T>(BinaryData input)
        {
            Interlocked.Increment(ref BinaryDeserializeCount);
            return _inner.Deserialize<T>(input);
        }

        public ValueTask SerializeAsync<T>(T input, Stream destination, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref StreamSerializeCount);
            return _innerStream.SerializeAsync(input, destination, cancellationToken);
        }

        public ValueTask<T> DeserializeAsync<T>(Stream input, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref StreamDeserializeCount);
            return _innerStream.DeserializeAsync<T>(input, cancellationToken);
        }
    }
}

/// <summary>
/// Validates that Azure blob storage uses the stream serializer for buffered writes.
/// </summary>
[TestCategory("Persistence"), TestCategory("AzureStorage")]
public class PersistenceGrainTests_AzureBlobStore_StreamSerializerBufferedWrites : IClassFixture<PersistenceGrainTests_AzureBlobStore_StreamSerializerBufferedWrites.Fixture>
{
    private readonly Fixture fixture;

    public PersistenceGrainTests_AzureBlobStore_StreamSerializerBufferedWrites(ITestOutputHelper output, Fixture fixture)
    {
        this.fixture = fixture;
        fixture.EnsurePreconditionsMet();
    }

    [SkippableFact, TestCategory("Functional")]
    public async Task AzureBlobStorage_UsesStreamSerializerForBufferedWrites()
    {
        CountingGrainStorageSerializer.Reset();

        var grain = fixture.GrainFactory.GetGrain<IGrainStorageTestGrain>(Guid.NewGuid(), "UnitTests.Grains");
        await grain.DoWrite(1);
        _ = await grain.DoRead();

        Assert.True(CountingGrainStorageSerializer.StreamSerializeCount > 0);
        Assert.True(CountingGrainStorageSerializer.StreamDeserializeCount > 0);
        Assert.Equal(0, CountingGrainStorageSerializer.BinarySerializeCount);
        Assert.Equal(0, CountingGrainStorageSerializer.BinaryDeserializeCount);
    }

    public class Fixture : BaseAzureTestClusterFixture
    {
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = 1;
            builder.Options.UseTestClusterMembership = false;
            builder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();
            builder.AddSiloBuilderConfigurator<StorageSiloBuilderConfigurator>();
            builder.AddClientBuilderConfigurator<ClientBuilderConfigurator>();
        }

        private class StorageSiloBuilderConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder.AddAzureBlobGrainStorage("GrainStorageForTest", optionsBuilder =>
                {
                    optionsBuilder.Configure(options =>
                    {
                        options.ConfigureTestDefaults();
                        options.UsePooledBufferForReads = true;
                        options.WriteMode = AzureBlobStorageWriteMode.BufferedStream;
                    });
                    optionsBuilder.Configure<IGrainStorageSerializer>((options, serializer) =>
                    {
                        options.GrainStorageSerializer = new CountingGrainStorageSerializer(serializer);
                    });
                });
            }
        }
    }

    private sealed class CountingGrainStorageSerializer : IGrainStorageSerializer, IGrainStorageStreamingSerializer
    {
        private readonly IGrainStorageSerializer _inner;
        private readonly IGrainStorageStreamingSerializer _innerStream;

        public static long BinarySerializeCount;
        public static long BinaryDeserializeCount;
        public static long StreamSerializeCount;
        public static long StreamDeserializeCount;

        public static void Reset()
        {
            Interlocked.Exchange(ref BinarySerializeCount, 0);
            Interlocked.Exchange(ref BinaryDeserializeCount, 0);
            Interlocked.Exchange(ref StreamSerializeCount, 0);
            Interlocked.Exchange(ref StreamDeserializeCount, 0);
        }

        public CountingGrainStorageSerializer(IGrainStorageSerializer inner)
        {
            _inner = inner;
            _innerStream = inner as IGrainStorageStreamingSerializer
                ?? throw new InvalidOperationException("Inner serializer must support stream operations for this test.");
        }

        public BinaryData Serialize<T>(T input)
        {
            Interlocked.Increment(ref BinarySerializeCount);
            return _inner.Serialize(input);
        }

        public T Deserialize<T>(BinaryData input)
        {
            Interlocked.Increment(ref BinaryDeserializeCount);
            return _inner.Deserialize<T>(input);
        }

        public ValueTask SerializeAsync<T>(T input, Stream destination, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref StreamSerializeCount);
            return _innerStream.SerializeAsync(input, destination, cancellationToken);
        }

        public ValueTask<T> DeserializeAsync<T>(Stream input, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref StreamDeserializeCount);
            return _innerStream.DeserializeAsync<T>(input, cancellationToken);
        }
    }
}
