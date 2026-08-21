using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Orleans.Runtime;
using Orleans.Storage;
using Xunit;

namespace Orleans.Persistence.FileStorage.Tests;

[TestProvider("None"), TestSuite("BVT"), TestCategory("FileStorage"), TestCategory("Persistence")]
public sealed class FileGrainStorageStateTransitionTests
{
    private const string StateName = "state";

    [Fact]
    public async Task ReadMissingRecord_ResetsStateRecordExistsAndETag()
    {
        using var directory = new TemporaryDirectory();
        var storage = FileGrainStorageTestContext.CreateStorage(directory.RootDirectory);
        var originalState = new FileStorageTestState { Value = "stale", Revision = 17 };
        var grainState = new GrainState<FileStorageTestState>(originalState, "stale-etag")
        {
            RecordExists = true,
        };

        await storage.ReadStateAsync(StateName, CreateGrainId(), grainState);

        Assert.NotSame(originalState, grainState.State);
        Assert.Equal(new FileStorageTestState(), grainState.State);
        Assert.False(grainState.RecordExists);
        Assert.Null(grainState.ETag);
        Assert.Empty(FileGrainStorageTestContext.GetRecordFiles(directory.RootDirectory));
    }

    [Fact]
    public async Task WriteNewRecord_WithNullETag_SetsRecordExistsAndFreshETag()
    {
        using var directory = new TemporaryDirectory();
        var storage = FileGrainStorageTestContext.CreateStorage(directory.RootDirectory);
        var state = new FileStorageTestState { Value = "created", Revision = 1 };
        var grainState = new GrainState<FileStorageTestState>(state);

        await storage.WriteStateAsync(StateName, CreateGrainId(), grainState);

        Assert.Same(state, grainState.State);
        Assert.True(grainState.RecordExists);
        Assert.False(string.IsNullOrEmpty(grainState.ETag));
        Assert.Single(FileGrainStorageTestContext.GetRecordFiles(directory.RootDirectory));
    }

    [Fact]
    public async Task ReadExistingRecord_RestoresStateRecordExistsAndPersistedETag()
    {
        using var directory = new TemporaryDirectory();
        var grainId = CreateGrainId();
        var written = new GrainState<FileStorageTestState>(
            new FileStorageTestState { Value = "durable", Revision = 2 });
        var writer = FileGrainStorageTestContext.CreateStorage(directory.RootDirectory);
        await writer.WriteStateAsync(StateName, grainId, written);
        var persistedETag = written.ETag;
        var reader = FileGrainStorageTestContext.CreateStorage(directory.RootDirectory);
        var read = new GrainState<FileStorageTestState>(
            new FileStorageTestState { Value = "placeholder", Revision = -1 });

        await reader.ReadStateAsync(StateName, grainId, read);

        Assert.Equal(written.State, read.State);
        Assert.NotSame(written.State, read.State);
        Assert.True(read.RecordExists);
        Assert.Equal(persistedETag, read.ETag);
    }

    [Fact]
    public async Task ClearExistingRecord_WithCurrentETag_ResetsStateAndDeletesRecord()
    {
        using var directory = new TemporaryDirectory();
        var storage = FileGrainStorageTestContext.CreateStorage(directory.RootDirectory);
        var grainId = CreateGrainId();
        var originalState = new FileStorageTestState { Value = "delete", Revision = 3 };
        var grainState = new GrainState<FileStorageTestState>(originalState);
        await storage.WriteStateAsync(StateName, grainId, grainState);

        await storage.ClearStateAsync(StateName, grainId, grainState);

        Assert.NotSame(originalState, grainState.State);
        Assert.Equal(new FileStorageTestState(), grainState.State);
        Assert.False(grainState.RecordExists);
        Assert.Null(grainState.ETag);
        Assert.Empty(FileGrainStorageTestContext.GetRecordFiles(directory.RootDirectory));
    }

    [Fact]
    public async Task ClearMissingRecord_WithNullETag_NormalizesStateAndSucceeds()
    {
        using var directory = new TemporaryDirectory();
        var storage = FileGrainStorageTestContext.CreateStorage(directory.RootDirectory);
        var originalState = new FileStorageTestState { Value = "stale", Revision = 4 };
        var grainState = new GrainState<FileStorageTestState>(originalState)
        {
            RecordExists = true,
        };

        await storage.ClearStateAsync(StateName, CreateGrainId(), grainState);

        Assert.NotSame(originalState, grainState.State);
        Assert.Equal(new FileStorageTestState(), grainState.State);
        Assert.False(grainState.RecordExists);
        Assert.Null(grainState.ETag);
        Assert.Empty(FileGrainStorageTestContext.GetRecordFiles(directory.RootDirectory));
    }

    [Fact]
    public async Task WriteExistingRecord_WithNullETag_ThrowsAndPreservesRecord()
    {
        using var directory = new TemporaryDirectory();
        var storage = FileGrainStorageTestContext.CreateStorage(directory.RootDirectory);
        var grainId = CreateGrainId();
        var original = new GrainState<FileStorageTestState>(
            new FileStorageTestState { Value = "original", Revision = 5 });
        await storage.WriteStateAsync(StateName, grainId, original);
        var originalETag = original.ETag;
        var duplicate = new GrainState<FileStorageTestState>(
            new FileStorageTestState { Value = "duplicate", Revision = 6 });

        await Assert.ThrowsAsync<InconsistentStateException>(
            () => storage.WriteStateAsync(StateName, grainId, duplicate));

        var read = await ReadAsync(storage, grainId);
        Assert.Equal(original.State, read.State);
        Assert.Equal(originalETag, read.ETag);
        Assert.True(read.RecordExists);
        Assert.Null(duplicate.ETag);
    }

    [Fact]
    public async Task WriteExistingRecord_WithStaleETag_ThrowsAndPreservesRecord()
    {
        using var directory = new TemporaryDirectory();
        var storage = FileGrainStorageTestContext.CreateStorage(directory.RootDirectory);
        var grainId = CreateGrainId();
        var current = new GrainState<FileStorageTestState>(
            new FileStorageTestState { Value = "first", Revision = 7 });
        await storage.WriteStateAsync(StateName, grainId, current);
        var staleETag = current.ETag;
        current.State = new FileStorageTestState { Value = "latest", Revision = 8 };
        await storage.WriteStateAsync(StateName, grainId, current);
        var latestETag = current.ETag;
        var recordPath = Assert.Single(FileGrainStorageTestContext.GetRecordFiles(directory.RootDirectory));
        var latestBytes = await File.ReadAllBytesAsync(recordPath);
        var stale = new GrainState<FileStorageTestState>(
            new FileStorageTestState { Value = "stale", Revision = 9 },
            staleETag)
        {
            RecordExists = true,
        };

        await Assert.ThrowsAsync<InconsistentStateException>(
            () => storage.WriteStateAsync(StateName, grainId, stale));

        var read = await ReadAsync(storage, grainId);
        Assert.Equal(current.State, read.State);
        Assert.Equal(latestETag, read.ETag);
        Assert.Equal(latestBytes, await File.ReadAllBytesAsync(recordPath));
    }

    [Fact]
    public async Task WriteMissingRecord_WithStaleETag_ThrowsAndDoesNotCreateRecord()
    {
        using var directory = new TemporaryDirectory();
        var storage = FileGrainStorageTestContext.CreateStorage(directory.RootDirectory);
        var grainState = new GrainState<FileStorageTestState>(
            new FileStorageTestState { Value = "stale", Revision = 10 },
            "fabricated-etag");

        await Assert.ThrowsAsync<InconsistentStateException>(
            () => storage.WriteStateAsync(StateName, CreateGrainId(), grainState));

        Assert.False(grainState.RecordExists);
        Assert.Equal("fabricated-etag", grainState.ETag);
        Assert.Empty(FileGrainStorageTestContext.GetRecordFiles(directory.RootDirectory));
    }

    [Fact]
    public async Task ClearExistingRecord_WithNullETag_ThrowsAndPreservesRecord()
    {
        using var directory = new TemporaryDirectory();
        var storage = FileGrainStorageTestContext.CreateStorage(directory.RootDirectory);
        var grainId = CreateGrainId();
        var original = new GrainState<FileStorageTestState>(
            new FileStorageTestState { Value = "keep", Revision = 11 });
        await storage.WriteStateAsync(StateName, grainId, original);
        var originalETag = original.ETag;
        var stale = new GrainState<FileStorageTestState>(new FileStorageTestState());

        await Assert.ThrowsAsync<InconsistentStateException>(
            () => storage.ClearStateAsync(StateName, grainId, stale));

        var read = await ReadAsync(storage, grainId);
        Assert.Equal(original.State, read.State);
        Assert.Equal(originalETag, read.ETag);
        Assert.True(read.RecordExists);
    }

    [Fact]
    public async Task ClearExistingRecord_WithStaleETag_ThrowsAndPreservesRecord()
    {
        using var directory = new TemporaryDirectory();
        var storage = FileGrainStorageTestContext.CreateStorage(directory.RootDirectory);
        var grainId = CreateGrainId();
        var current = new GrainState<FileStorageTestState>(
            new FileStorageTestState { Value = "first", Revision = 12 });
        await storage.WriteStateAsync(StateName, grainId, current);
        var staleETag = current.ETag;
        current.State = new FileStorageTestState { Value = "latest", Revision = 13 };
        await storage.WriteStateAsync(StateName, grainId, current);
        var latestETag = current.ETag;
        var stale = new GrainState<FileStorageTestState>(new FileStorageTestState(), staleETag)
        {
            RecordExists = true,
        };

        await Assert.ThrowsAsync<InconsistentStateException>(
            () => storage.ClearStateAsync(StateName, grainId, stale));

        var read = await ReadAsync(storage, grainId);
        Assert.Equal(current.State, read.State);
        Assert.Equal(latestETag, read.ETag);
        Assert.True(read.RecordExists);
    }

    [Fact]
    public async Task ClearMissingRecord_WithStaleETag_NormalizesStateWithoutCreatingRecord()
    {
        using var directory = new TemporaryDirectory();
        var storage = FileGrainStorageTestContext.CreateStorage(directory.RootDirectory);
        var originalState = new FileStorageTestState { Value = "stale", Revision = 14 };
        var grainState = new GrainState<FileStorageTestState>(originalState, "fabricated-etag")
        {
            RecordExists = true,
        };

        await storage.ClearStateAsync(StateName, CreateGrainId(), grainState);

        Assert.NotSame(originalState, grainState.State);
        Assert.Equal(new FileStorageTestState(), grainState.State);
        Assert.False(grainState.RecordExists);
        Assert.Null(grainState.ETag);
        Assert.Empty(FileGrainStorageTestContext.GetRecordFiles(directory.RootDirectory));
    }

    [Fact]
    public async Task ReadMissingRecord_CreatesStateWithoutPublicParameterlessConstructor()
    {
        using var directory = new TemporaryDirectory();
        var storage = FileGrainStorageTestContext.CreateStorage(directory.RootDirectory);
        var grainState = new GrainState<ConstructorlessState>(new ConstructorlessState("stale"), "stale-etag")
        {
            RecordExists = true,
        };

        await storage.ReadStateAsync(StateName, CreateGrainId(), grainState);

        Assert.NotNull(grainState.State);
        Assert.Null(grainState.State.Value);
        Assert.False(grainState.RecordExists);
        Assert.Null(grainState.ETag);
    }

    [Fact]
    public async Task SuccessfulWrites_ReturnFreshOpaqueETags()
    {
        using var directory = new TemporaryDirectory();
        var storage = FileGrainStorageTestContext.CreateStorage(directory.RootDirectory);
        var grainState = new GrainState<FileStorageTestState>(
            new FileStorageTestState { Value = "same", Revision = 15 });
        var grainId = CreateGrainId();
        var etags = new List<string>();

        for (var index = 0; index < 3; index++)
        {
            await storage.WriteStateAsync(StateName, grainId, grainState);
            etags.Add(Assert.IsType<string>(grainState.ETag));
        }

        var recordPath = Assert.Single(FileGrainStorageTestContext.GetRecordFiles(directory.RootDirectory));
        var timestampETag = File.GetLastWriteTimeUtc(recordPath).ToString(CultureInfo.InvariantCulture);
        Assert.Equal(3, etags.Distinct(StringComparer.Ordinal).Count());
        Assert.All(etags, etag =>
        {
            Assert.NotEmpty(etag);
            Assert.NotEqual(timestampETag, etag);
        });
        var read = await ReadAsync(storage, grainId);
        Assert.Equal(etags[^1], read.ETag);
        Assert.Equal(grainState.State, read.State);
    }

    [Fact]
    public async Task ConcurrentWrites_WithSameCurrentETag_OnlyOneCommitsAtomically()
    {
        using var directory = new TemporaryDirectory();
        var storage = FileGrainStorageTestContext.CreateStorage(directory.RootDirectory);
        var grainId = CreateGrainId();
        var initial = new GrainState<FileStorageTestState>(
            new FileStorageTestState { Value = "initial", Revision = 16 });
        await storage.WriteStateAsync(StateName, grainId, initial);
        var initialETag = initial.ETag;
        var first = new GrainState<FileStorageTestState>(
            new FileStorageTestState { Value = "first", Revision = 17 },
            initialETag)
        {
            RecordExists = true,
        };
        var second = new GrainState<FileStorageTestState>(
            new FileStorageTestState { Value = "second", Revision = 18 },
            initialETag)
        {
            RecordExists = true,
        };
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var attempts = new[]
        {
            AttemptWriteAsync(first),
            AttemptWriteAsync(second),
        };
        start.SetResult();
        var exceptions = await Task.WhenAll(attempts);

        Assert.Single(exceptions, exception => exception is null);
        Assert.Single(exceptions, exception => exception is InconsistentStateException);
        var successful = exceptions[0] is null ? first : second;
        var failed = exceptions[0] is null ? second : first;
        var read = await ReadAsync(storage, grainId);
        Assert.Equal(successful.State, read.State);
        Assert.Equal(successful.ETag, read.ETag);
        Assert.NotEqual(initialETag, successful.ETag);
        Assert.Equal(initialETag, failed.ETag);
        Assert.Single(FileGrainStorageTestContext.GetRecordFiles(directory.RootDirectory));

        async Task<Exception?> AttemptWriteAsync(GrainState<FileStorageTestState> state)
        {
            await start.Task;
            return await Record.ExceptionAsync(() => storage.WriteStateAsync(StateName, grainId, state));
        }
    }

    [Fact]
    public async Task CrossProcessLock_SerializesETagCheckAndCommit()
    {
        using var directory = new TemporaryDirectory();
        var storage = FileGrainStorageTestContext.CreateStorage(directory.RootDirectory);
        var grainId = CreateGrainId();
        var state = new GrainState<FileStorageTestState>(
            new FileStorageTestState { Value = "initial", Revision = 19 });
        await storage.WriteStateAsync(StateName, grainId, state);

        var recordPath = Assert.Single(FileGrainStorageTestContext.GetRecordFiles(directory.RootDirectory));
        var lockIndex = Path.GetFileName(recordPath)[..2];
        var lockPath = Path.Combine(directory.RootDirectory, $".orleans-file-storage.{lockIndex}.lock");
        using var process = StartLockWorker(lockPath);

        Assert.Equal("READY", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
        state.State = new FileStorageTestState { Value = "updated", Revision = 20 };
        var writeTask = storage.WriteStateAsync(StateName, grainId, state);

        try
        {
            Assert.False(writeTask.IsCompleted);
        }
        finally
        {
            await process.StandardInput.WriteLineAsync("RELEASE");
            await process.StandardInput.FlushAsync();
        }

        await writeTask.WaitAsync(TimeSpan.FromSeconds(15));
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        var standardError = await process.StandardError.ReadToEndAsync();
        Assert.True(process.ExitCode == 0, standardError);

        var read = await ReadAsync(storage, grainId);
        Assert.Equal(state.State, read.State);
        Assert.Equal(state.ETag, read.ETag);
    }

    [Fact]
    public async Task CrossProcessLockTimeout_AppliesToAllQueuedCallers()
    {
        using var directory = new TemporaryDirectory();
        var storage = FileGrainStorageTestContext.CreateStorage(
            directory.RootDirectory,
            lockAcquireTimeout: TimeSpan.FromMilliseconds(250));
        var grainId = CreateGrainId();
        var initial = new GrainState<FileStorageTestState>(
            new FileStorageTestState { Value = "initial", Revision = 21 });
        await storage.WriteStateAsync(StateName, grainId, initial);

        var recordPath = Assert.Single(FileGrainStorageTestContext.GetRecordFiles(directory.RootDirectory));
        var lockIndex = Path.GetFileName(recordPath)[..2];
        var lockPath = Path.Combine(directory.RootDirectory, $".orleans-file-storage.{lockIndex}.lock");
        using var process = StartLockWorker(lockPath);
        Assert.Equal("READY", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));

        var first = new GrainState<FileStorageTestState>(
            new FileStorageTestState { Value = "first", Revision = 22 },
            initial.ETag);
        var second = new GrainState<FileStorageTestState>(
            new FileStorageTestState { Value = "second", Revision = 23 },
            initial.ETag);
        var stopwatch = Stopwatch.StartNew();
        var attempts = await Task.WhenAll(
            Record.ExceptionAsync(() => storage.WriteStateAsync(StateName, grainId, first)).AsTask(),
            Record.ExceptionAsync(() => storage.WriteStateAsync(StateName, grainId, second)).AsTask());

        await process.StandardInput.WriteLineAsync("RELEASE");
        await process.StandardInput.FlushAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));

        Assert.All(attempts, exception => Assert.IsType<TimeoutException>(exception));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Elapsed: {stopwatch.Elapsed}");
    }

    private static GrainId CreateGrainId() =>
        GrainId.Create("file-storage-test-grain", Guid.NewGuid().ToString("N"));

    private static DirectoryInfo FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
            directory is not null;
            directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Orleans.slnx")))
            {
                return directory;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the Orleans repository root.");
    }

    private static Process StartLockWorker(string lockPath)
    {
        var repositoryRoot = FindRepositoryRoot();
        var projectPath = Path.Combine(
            repositoryRoot.FullName,
            "test",
            "Extensions",
            "Orleans.Persistence.FileStorage.LockWorker",
            "Orleans.Persistence.FileStorage.LockWorker.csproj");
        var targetFramework = AppContext.TargetFrameworkName switch
        {
            string value when value.Contains("Version=v8.0", StringComparison.Ordinal) => "net8.0",
            string value when value.Contains("Version=v10.0", StringComparison.Ordinal) => "net10.0",
            string value => throw new InvalidOperationException($"Unsupported target framework '{value}'."),
            null => throw new InvalidOperationException("The target framework is unavailable."),
        };
        var configuration = typeof(FileGrainStorageStateTransitionTests).Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("--framework");
        startInfo.ArgumentList.Add(targetFramework);
        startInfo.ArgumentList.Add("--configuration");
        startInfo.ArgumentList.Add(configuration);
        startInfo.ArgumentList.Add("--no-build");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(lockPath);
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the file storage lock worker.");
    }

    private static async Task<GrainState<FileStorageTestState>> ReadAsync(
        FileGrainStorage storage,
        GrainId grainId)
    {
        var result = new GrainState<FileStorageTestState>(new FileStorageTestState());
        await storage.ReadStateAsync(StateName, grainId, result);
        return result;
    }

    private sealed class ConstructorlessState(string value)
    {
        public string? Value { get; } = value;
    }
}
