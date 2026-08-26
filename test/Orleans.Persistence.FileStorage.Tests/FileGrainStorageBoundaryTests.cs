using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Runtime;
using Orleans.Storage;
using Xunit;

namespace Orleans.Persistence.FileStorage.Tests;

[TestProvider("None"), TestSuite("BVT"), TestCategory("FileStorage"), TestCategory("Persistence")]
public sealed class FileGrainStorageBoundaryTests
{
    [Fact]
    public async Task BinaryPayload_RoundTripsWithoutTextConversion()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        byte[] bytes = [0x00, 0x80, 0xFF, 0xC3, 0x28, 0xFE];
        var expected = new FileStorageTestState { Value = "binary", Revision = 1 };
        var serializer = new RecordingGrainStorageSerializer(bytes, expected);
        IGrainStorage storage = FileGrainStorageTestContext.CreateStorage(
            directory.RootDirectory,
            serializer: serializer);
        var grainId = GrainId.Create("binary/type", "binary/key");
        var written = new GrainState<FileStorageTestState>(
            new FileStorageTestState { Value = "source", Revision = 2 });

        await storage.WriteStateAsync("binary/state", grainId, written, cancellationToken);
        var read = new GrainState<FileStorageTestState>(new FileStorageTestState());
        await storage.ReadStateAsync("binary/state", grainId, read, cancellationToken);

        Assert.Equal(bytes, serializer.DeserializedBytes);
        Assert.Same(expected, read.State);
        Assert.Equal(written.ETag, read.ETag);
        Assert.True(read.RecordExists);
    }

    [Fact]
    public async Task UnsafeIdentityValues_ProduceSafeDistinctFileNames()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        IGrainStorage firstStorage = FileGrainStorageTestContext.CreateStorage(directory.RootDirectory, "../service");
        IGrainStorage secondStorage = FileGrainStorageTestContext.CreateStorage(directory.RootDirectory, "service");
        var first = new GrainState<FileStorageTestState>(
            new FileStorageTestState { Value = "first", Revision = 1 });
        var second = new GrainState<FileStorageTestState>(
            new FileStorageTestState { Value = "second", Revision = 2 });

        await firstStorage.WriteStateAsync(
            @"..\state",
            GrainId.Create(@"type\one", "../key"),
            first,
            cancellationToken);
        await secondStorage.WriteStateAsync(
            "state",
            GrainId.Create("type", "key"),
            second,
            cancellationToken);

        var files = FileGrainStorageTestContext.GetRecordFiles(directory.RootDirectory);
        Assert.Equal(2, files.Length);
        Assert.All(files, file => Assert.Matches("^[0-9A-F]{64}\\.grain$", Path.GetFileName(file)));
        Assert.All(files, file => Assert.Equal(
            Path.GetFullPath(directory.RootDirectory),
            Path.GetDirectoryName(Path.GetFullPath(file))));
    }

    [Fact]
    public async Task InvalidRecord_ThrowsInvalidDataException()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        IGrainStorage storage = FileGrainStorageTestContext.CreateStorage(directory.RootDirectory);
        var grainId = GrainId.Create("invalid", "record");
        var state = new GrainState<FileStorageTestState>(new FileStorageTestState());
        await storage.WriteStateAsync("state", grainId, state, cancellationToken);
        var path = Assert.Single(FileGrainStorageTestContext.GetRecordFiles(directory.RootDirectory));
        await File.WriteAllBytesAsync(path, "invalid"u8.ToArray(), cancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => storage.ReadStateAsync(
                "state",
                grainId,
                new GrainState<FileStorageTestState>(new FileStorageTestState()),
                cancellationToken));
    }

    [Fact]
    public async Task LifecycleStart_CreatesRootDirectory()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        var storage = FileGrainStorageTestContext.CreateStorage(
            directory.RootDirectory,
            createRootDirectory: false);
        var lifecycle = new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance);
        Assert.False(Directory.Exists(directory.RootDirectory));

        storage.Participate(lifecycle);
        await lifecycle.OnStart(cancellationToken);

        Assert.True(Directory.Exists(directory.RootDirectory));
        await lifecycle.OnStop(cancellationToken);
    }

    [Fact]
    public async Task MissingRecord_CreatesConstructorlessState()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        IGrainStorage storage = FileGrainStorageTestContext.CreateStorage(directory.RootDirectory);
        var state = new GrainState<ConstructorlessState>(new ConstructorlessState("stale"), "stale")
        {
            RecordExists = true,
        };

        await storage.ReadStateAsync("state", GrainId.Create("type", "key"), state, cancellationToken);

        Assert.NotNull(state.State);
        Assert.Null(state.State.Value);
        Assert.Null(state.ETag);
        Assert.False(state.RecordExists);
    }

    [Fact]
    public async Task CancellationToken_CancelsStorageOperations()
    {
        using var directory = new TemporaryDirectory();
        IGrainStorage storage = FileGrainStorageTestContext.CreateStorage(directory.RootDirectory);
        var grainId = GrainId.Create("type", "key");
        var state = new GrainState<FileStorageTestState>(new FileStorageTestState());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => storage.ReadStateAsync("state", grainId, state, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => storage.WriteStateAsync("state", grainId, state, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => storage.ClearStateAsync("state", grainId, state, cancellation.Token));
    }

    private sealed class ConstructorlessState(string value)
    {
        public string? Value { get; } = value;
    }
}
