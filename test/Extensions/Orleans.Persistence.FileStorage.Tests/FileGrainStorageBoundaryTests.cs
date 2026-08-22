using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Runtime;
using Xunit;

namespace Orleans.Persistence.FileStorage.Tests;

[TestProvider("None"), TestSuite("BVT"), TestCategory("FileStorage"), TestCategory("Persistence")]
public sealed class FileGrainStorageBoundaryTests
{
    [Fact]
    public async Task ArbitraryBinarySerializerBytes_RoundTripWithoutTextConversion()
    {
        using var directory = new TemporaryDirectory();
        byte[] bytes = [0x00, 0x80, 0xFF, 0xC3, 0x28, 0xFE, 0x00, 0x41];
        var reconstructed = new FileStorageTestState { Value = "binary", Revision = 19 };
        var serializer = new RecordingGrainStorageSerializer(bytes, reconstructed);
        var storage = FileGrainStorageTestContext.CreateStorage(
            directory.RootDirectory,
            serializer: serializer);
        var writtenState = new FileStorageTestState { Value = "source", Revision = 20 };
        var written = new GrainState<FileStorageTestState>(writtenState);
        var grainId = GrainId.Create("binary/type", "binary/key");

        await storage.WriteStateAsync("binary/state", grainId, written);
        var read = new GrainState<FileStorageTestState>(new FileStorageTestState());
        await storage.ReadStateAsync("binary/state", grainId, read);

        Assert.Equal(bytes, serializer.DeserializedBytes);
        Assert.Same(writtenState, serializer.SerializedValue);
        Assert.Equal(1, serializer.SerializeCallCount);
        Assert.Equal(1, serializer.DeserializeCallCount);
        Assert.Same(reconstructed, read.State);
        Assert.True(read.RecordExists);
        Assert.Equal(written.ETag, read.ETag);
    }

    [Fact]
    public async Task EmptySerializerPayload_RoundTripsAsHeaderOnlyRecord()
    {
        using var directory = new TemporaryDirectory();
        var reconstructed = new FileStorageTestState { Value = "empty", Revision = 25 };
        var serializer = new RecordingGrainStorageSerializer([], reconstructed);
        var storage = FileGrainStorageTestContext.CreateStorage(
            directory.RootDirectory,
            serializer: serializer);
        var grainId = GrainId.Create("empty/type", "empty/key");
        var written = new GrainState<FileStorageTestState>(
            new FileStorageTestState { Value = "source", Revision = 26 });

        await storage.WriteStateAsync("empty/state", grainId, written);
        var recordPath = Assert.Single(FileGrainStorageTestContext.GetRecordFiles(directory.RootDirectory));
        var read = new GrainState<FileStorageTestState>(new FileStorageTestState());
        await storage.ReadStateAsync("empty/state", grainId, read);

        Assert.Equal(24, new FileInfo(recordPath).Length);
        Assert.Empty(Assert.IsType<byte[]>(serializer.DeserializedBytes));
        Assert.Same(reconstructed, read.State);
        Assert.Equal(written.ETag, read.ETag);
        Assert.True(read.RecordExists);
    }

    [Fact]
    public async Task InvalidRecordMagic_ThrowsInvalidDataException()
    {
        using var directory = new TemporaryDirectory();
        var storage = FileGrainStorageTestContext.CreateStorage(directory.RootDirectory);
        var grainId = GrainId.Create("invalid/type", "invalid-magic");
        var written = new GrainState<FileStorageTestState>(
            new FileStorageTestState { Value = "invalid", Revision = 27 });
        await storage.WriteStateAsync("invalid/state", grainId, written);
        var recordPath = Assert.Single(FileGrainStorageTestContext.GetRecordFiles(directory.RootDirectory));
        var bytes = await File.ReadAllBytesAsync(recordPath);
        bytes[0] ^= 0xFF;
        await File.WriteAllBytesAsync(recordPath, bytes);
        var read = new GrainState<FileStorageTestState>(new FileStorageTestState());

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => storage.ReadStateAsync("invalid/state", grainId, read));

        Assert.Contains(recordPath, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TruncatedRecord_ThrowsInvalidDataException()
    {
        using var directory = new TemporaryDirectory();
        var storage = FileGrainStorageTestContext.CreateStorage(directory.RootDirectory);
        var grainId = GrainId.Create("invalid/type", "truncated");
        var written = new GrainState<FileStorageTestState>(
            new FileStorageTestState { Value = "truncated", Revision = 28 });
        await storage.WriteStateAsync("invalid/state", grainId, written);
        var recordPath = Assert.Single(FileGrainStorageTestContext.GetRecordFiles(directory.RootDirectory));
        await File.WriteAllBytesAsync(recordPath, "ORLFS001"u8.ToArray());
        var read = new GrainState<FileStorageTestState>(new FileStorageTestState());

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => storage.ReadStateAsync("invalid/state", grainId, read));

        Assert.Contains(recordPath, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecordPathWithFilesystemAccessFailure_PropagatesTheFailure()
    {
        using var directory = new TemporaryDirectory();
        var storage = FileGrainStorageTestContext.CreateStorage(directory.RootDirectory);
        var grainId = GrainId.Create("filesystem-failure", "grain");
        var written = new GrainState<FileStorageTestState>(
            new FileStorageTestState { Value = "persisted", Revision = 29 });
        await storage.WriteStateAsync("state", grainId, written);
        var recordPath = Assert.Single(FileGrainStorageTestContext.GetRecordFiles(directory.RootDirectory));
        File.Delete(recordPath);
        Directory.CreateDirectory(recordPath);
        var read = new GrainState<FileStorageTestState>(
            new FileStorageTestState { Value = "unchanged", Revision = 30 },
            "unchanged-etag")
        {
            RecordExists = true,
        };

        AssertFileSystemAccessFailure(
            await Record.ExceptionAsync(() => storage.ReadStateAsync("state", grainId, read)));
        AssertFileSystemAccessFailure(
            await Record.ExceptionAsync(() => storage.ClearStateAsync("state", grainId, written)));
        AssertFileSystemAccessFailure(
            await Record.ExceptionAsync(() => storage.WriteStateAsync("state", grainId, written)));

        Assert.Equal(new FileStorageTestState { Value = "unchanged", Revision = 30 }, read.State);
        Assert.Equal("unchanged-etag", read.ETag);
        Assert.True(read.RecordExists);
    }

    [Fact]
    public async Task StorageIdentityComponents_MapDeterministicallyWithoutCollisions()
    {
        using var directory = new TemporaryDirectory();
        var cases = new[]
        {
            new IdentityCase("service", "state", GrainId.Create("type", "key"), "baseline"),
            new IdentityCase("service-variant", "state", GrainId.Create("type", "key"), "service"),
            new IdentityCase("service", "state", GrainId.Create("type-variant", "key"), "type"),
            new IdentityCase("service", "state", GrainId.Create("type", "key-variant"), "key"),
            new IdentityCase("service", "state-variant", GrainId.Create("type", "key"), "state"),
            new IdentityCase("a.b", "d", GrainId.Create("type", "c"), "ambiguous-one"),
            new IdentityCase("a", "d", GrainId.Create("type", "b.c"), "ambiguous-two"),
        };

        foreach (var identity in cases)
        {
            await identity.Storage(directory.RootDirectory).WriteStateAsync(
                identity.StateName,
                identity.GrainId,
                identity.GrainState);
        }

        var initialPaths = FileGrainStorageTestContext.GetRecordFiles(directory.RootDirectory);
        Assert.Equal(cases.Length, initialPaths.Length);
        Assert.Equal(cases.Length, initialPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var baseline = cases[0];
        var baselineETag = baseline.GrainState.ETag;
        baseline.GrainState.State = new FileStorageTestState { Value = "baseline-updated", Revision = 21 };
        await baseline.Storage(directory.RootDirectory).WriteStateAsync(
            baseline.StateName,
            baseline.GrainId,
            baseline.GrainState);

        Assert.Equal(cases.Length, FileGrainStorageTestContext.GetRecordFiles(directory.RootDirectory).Length);
        Assert.NotEqual(baselineETag, baseline.GrainState.ETag);
        foreach (var identity in cases)
        {
            var read = new GrainState<FileStorageTestState>(new FileStorageTestState());
            await identity.Storage(directory.RootDirectory).ReadStateAsync(
                identity.StateName,
                identity.GrainId,
                read);

            Assert.Equal(identity.GrainState.State, read.State);
            Assert.Equal(identity.GrainState.ETag, read.ETag);
            Assert.True(read.RecordExists);
        }
    }

    public static TheoryData<string, string> UnsafeIdentityComponents =>
        new()
        {
            { "serviceId", "../outside" },
            { "grainType", @"..\outside" },
            { "grainKey", "segment/child" },
            { "stateName", @"segment\child" },
            { "serviceId", "service:alternate" },
            { "grainType", "type*wildcard" },
            { "grainKey", "key\u0001control" },
            { "stateName", "state.with.delimiters" },
        };

    [Theory]
    [MemberData(nameof(UnsafeIdentityComponents))]
    public async Task UnsafeIdentityComponents_RemainUnderRootWithSafeFileNames(
        string component,
        string unsafeValue)
    {
        using var directory = new TemporaryDirectory();
        const string BaselineServiceId = "safe-service";
        const string BaselineStateName = "safe-state";
        var baselineGrainId = GrainId.Create("safe-type", "safe-key");
        var serviceId = component == "serviceId" ? unsafeValue : BaselineServiceId;
        var stateName = component == "stateName" ? unsafeValue : BaselineStateName;
        var grainType = component == "grainType" ? unsafeValue : "safe-type";
        var grainKey = component == "grainKey" ? unsafeValue : "safe-key";
        var unsafeGrainId = GrainId.Create(grainType, grainKey);
        var baselineStorage = FileGrainStorageTestContext.CreateStorage(
            directory.RootDirectory,
            BaselineServiceId);
        var unsafeStorage = FileGrainStorageTestContext.CreateStorage(directory.RootDirectory, serviceId);
        var baseline = new GrainState<FileStorageTestState>(
            new FileStorageTestState { Value = "baseline", Revision = 22 });
        var unsafeState = new GrainState<FileStorageTestState>(
            new FileStorageTestState { Value = component, Revision = 23 });

        await baselineStorage.WriteStateAsync(BaselineStateName, baselineGrainId, baseline);
        await unsafeStorage.WriteStateAsync(stateName, unsafeGrainId, unsafeState);

        var baselineRead = new GrainState<FileStorageTestState>(new FileStorageTestState());
        var unsafeRead = new GrainState<FileStorageTestState>(new FileStorageTestState());
        await baselineStorage.ReadStateAsync(BaselineStateName, baselineGrainId, baselineRead);
        await unsafeStorage.ReadStateAsync(stateName, unsafeGrainId, unsafeRead);
        Assert.Equal(baseline.State, baselineRead.State);
        Assert.Equal(unsafeState.State, unsafeRead.State);
        Assert.NotEqual(baselineRead.ETag, unsafeRead.ETag);

        var root = Path.GetFullPath(directory.RootDirectory) + Path.DirectorySeparatorChar;
        var recordPaths = FileGrainStorageTestContext.GetRecordFiles(directory.RootDirectory);
        Assert.Equal(2, recordPaths.Length);
        Assert.All(recordPaths, path =>
        {
            var canonicalPath = Path.GetFullPath(path);
            Assert.StartsWith(root, canonicalPath, StringComparison.OrdinalIgnoreCase);
            Assert.Matches("^[0-9A-F]{64}\\.grain$", Path.GetFileName(canonicalPath));
        });

        var allOwnedFiles = Directory.GetFiles(directory.OwnedDirectory, "*", SearchOption.AllDirectories);
        Assert.Equal(
            recordPaths.Order(),
            allOwnedFiles.Where(static path => path.EndsWith(".grain", StringComparison.Ordinal)).Order());
    }

    [Fact]
    public void Participate_RegistersAtApplicationServicesStage()
    {
        using var directory = new TemporaryDirectory();
        var storage = FileGrainStorageTestContext.CreateStorage(directory.RootDirectory);
        var lifecycle = new CapturingSiloLifecycle();

        storage.Participate(lifecycle);

        Assert.Equal($"{typeof(FileGrainStorage).FullName}-FileStore", lifecycle.ObserverName);
        Assert.Equal(ServiceLifecycleStage.ApplicationServices, lifecycle.Stage);
        Assert.NotNull(lifecycle.Observer);
    }

    [Fact]
    public async Task LifecycleStart_CreatesConfiguredNestedRootDirectory()
    {
        using var directory = new TemporaryDirectory();
        var storage = FileGrainStorageTestContext.CreateStorage(directory.RootDirectory);
        var lifecycle = new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance);
        Assert.False(Directory.Exists(directory.RootDirectory));

        storage.Participate(lifecycle);
        await lifecycle.OnStart();

        Assert.True(Directory.Exists(directory.RootDirectory));
        Assert.Empty(Directory.GetFiles(directory.RootDirectory, "*", SearchOption.AllDirectories));
        await lifecycle.OnStop();
    }

    [Fact]
    public async Task WriteToNonexistentRoot_CreatesRootAndPersistsState()
    {
        using var directory = new TemporaryDirectory();
        var storage = FileGrainStorageTestContext.CreateStorage(directory.RootDirectory);
        var grainId = GrainId.Create("root/type", "root/key");
        var written = new GrainState<FileStorageTestState>(
            new FileStorageTestState { Value = "root-created", Revision = 24 });
        Assert.False(Directory.Exists(directory.RootDirectory));

        await storage.WriteStateAsync("root/state", grainId, written);

        Assert.True(Directory.Exists(directory.RootDirectory));
        Assert.Single(FileGrainStorageTestContext.GetRecordFiles(directory.RootDirectory));
        Assert.True(written.RecordExists);
        Assert.False(string.IsNullOrEmpty(written.ETag));
        var read = new GrainState<FileStorageTestState>(new FileStorageTestState());
        await storage.ReadStateAsync("root/state", grainId, read);
        Assert.Equal(written.State, read.State);
        Assert.Equal(written.ETag, read.ETag);
        Assert.True(read.RecordExists);
    }

    private sealed class IdentityCase(
        string serviceId,
        string stateName,
        GrainId grainId,
        string value)
    {
        public GrainId GrainId { get; } = grainId;

        public GrainState<FileStorageTestState> GrainState { get; } =
            new(new FileStorageTestState { Value = value, Revision = value.Length });

        public string StateName { get; } = stateName;

        public FileGrainStorage Storage(string rootDirectory) =>
            FileGrainStorageTestContext.CreateStorage(rootDirectory, serviceId);
    }

    private static void AssertFileSystemAccessFailure(Exception? exception)
    {
        Assert.True(
            exception is IOException or UnauthorizedAccessException,
            $"Expected a filesystem access failure, but received: {exception}");
    }

    private sealed class CapturingSiloLifecycle : ISiloLifecycle
    {
        public int HighestCompletedStage => 0;

        public int LowestStoppedStage => 0;

        public string? ObserverName { get; private set; }

        public ILifecycleObserver? Observer { get; private set; }

        public int? Stage { get; private set; }

        public IDisposable Subscribe(string observerName, int stage, ILifecycleObserver observer)
        {
            ObserverName = observerName;
            Observer = observer;
            Stage = stage;
            return new CancellationTokenSource();
        }
    }
}
