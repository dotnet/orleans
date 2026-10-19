using System.Buffers;
using System.Security.Cryptography;
using CsCheck;
using Orleans.Storage;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Verifies the <see cref="IJournalStorage"/> contract and <see cref="IJournalHistoryStorage"/>. Every journal storage
/// provider in this repository runs this suite.
/// </summary>
/// <remarks>
/// Reads return a journal's content as one byte sequence, so these tests verify segment atomicity and ordering by comparing
/// that sequence with the concatenation of the whole segments which the contract allows to be visible.
/// </remarks>
public abstract class JournalStorageConformanceTests : IAsyncLifetime
{
    private IJournalStorageProvider? _provider;
    private IJournalStorageProvider? _faultingProvider;

    /// <summary>
    /// Gets the faults injected into storage instances created by <see cref="CreateFaultingStorage"/>.
    /// </summary>
    protected JournalStorageFaultInjector Faults { get; } = new();

    /// <summary>
    /// Gets the largest segment which one <see cref="IJournalStorage.AppendAsync"/> accepts, or <see langword="null"/> when the
    /// provider enforces no per-append limit of its own.
    /// </summary>
    protected virtual long? MaxAppendBytes => null;

    /// <summary>
    /// Gets the faults which <see cref="CreateProviderAsync"/> injects when given a <see cref="JournalStorageFaultInjector"/>.
    /// </summary>
    protected virtual IReadOnlySet<JournalStorageFaultKind> SupportedFaults { get; } = new HashSet<JournalStorageFaultKind>();

    private bool SupportsFaultInjection => SupportedFaults.Count > 0;

    /// <summary>
    /// Gets the number of random operation sequences which the model-based test runs.
    /// </summary>
    protected virtual int PropertyIterations => 25;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    /// <summary>
    /// Creates a provider. Providers created by one test class instance share the same backing store.
    /// </summary>
    /// <param name="faults">The faults to inject into the provider's write requests, or <see langword="null"/> for none.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    protected abstract ValueTask<IJournalStorageProvider> CreateProviderAsync(JournalStorageFaultInjector? faults, CancellationToken cancellationToken);

    public virtual async ValueTask InitializeAsync()
    {
        _provider = await CreateProviderAsync(faults: null, CancellationToken);
        if (SupportsFaultInjection)
        {
            _faultingProvider = await CreateProviderAsync(Faults, CancellationToken);
        }
    }

    public virtual ValueTask DisposeAsync() => default;

    [Fact]
    public async Task Append_ReadReturnsWholeSegmentsInWriteOrder()
    {
        var journalId = CreateJournalId();
        var writer = CreateStorage(journalId);
        byte[][] segments = [Bytes(3, 1), Bytes(70_000, 2), Bytes(1, 3), Bytes(4_096, 4)];
        foreach (var segment in segments)
        {
            await writer.AppendAsync(new ReadOnlySequence<byte>(segment), CancellationToken);
        }

        Assert.Equal(Concat(segments), await ReadContentAsync(CreateStorage(journalId)));
        Assert.Equal(Concat(segments), await ReadContentAsync(writer));
    }

    [Fact]
    public async Task Append_MultiSegmentSequence_IsStoredAsOneSegment()
    {
        var journalId = CreateJournalId();
        var writer = CreateStorage(journalId);
        var first = new SequenceSegment(Bytes(5, 1));
        var last = first.Append(Bytes(7, 2));

        await writer.AppendAsync(new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length), CancellationToken);

        Assert.Equal(Concat([Bytes(5, 1), Bytes(7, 2)]), await ReadContentAsync(CreateStorage(journalId)));
    }

    [Fact]
    public async Task Replace_FollowedByAppends_ReadReturnsReplacementThenAppends()
    {
        var journalId = CreateJournalId();
        var writer = CreateStorage(journalId);
        await writer.AppendAsync(Sequence(10, 1), CancellationToken);
        await writer.AppendAsync(Sequence(20, 2), CancellationToken);

        await writer.ReplaceAsync(Sequence(100_000, 3), CancellationToken);
        await writer.AppendAsync(Sequence(30, 4), CancellationToken);
        await writer.AppendAsync(Sequence(40, 5), CancellationToken);

        var expected = Concat([Bytes(100_000, 3), Bytes(30, 4), Bytes(40, 5)]);
        Assert.Equal(expected, await ReadContentAsync(CreateStorage(journalId)));

        var reader = CreateStorage(journalId);
        Assert.Equal(expected, await ReadContentAsync(reader));
        await reader.ReplaceAsync(Sequence(1, 6), CancellationToken);
        await reader.AppendAsync(Sequence(2, 7), CancellationToken);
        Assert.Equal(Concat([Bytes(1, 6), Bytes(2, 7)]), await ReadContentAsync(CreateStorage(journalId)));
    }

    [Fact]
    public async Task Append_AtProviderLimit_Succeeds()
    {
        Assert.SkipUnless(MaxAppendBytes is not null, "The provider enforces no per-append limit of its own.");
        var journalId = CreateJournalId();
        var writer = CreateStorage(journalId);
        await writer.AppendAsync(Sequence(16, 1), CancellationToken);
        var segment = LargeSequence(MaxAppendBytes!.Value, 2);

        await writer.AppendAsync(segment, CancellationToken);

        var expected = new HashingConsumer();
        expected.Add(Bytes(16, 1));
        foreach (var memory in segment)
        {
            expected.Add(memory.Span);
        }

        var actual = new HashingConsumer();
        await CreateStorage(journalId).ReadAsync(actual, CancellationToken);
        Assert.True(actual.IsCompleted);
        Assert.Equal(expected.Length, actual.Length);
        Assert.Equal(expected.GetHash(), actual.GetHash());
    }

    [Fact]
    public async Task Append_OverProviderLimit_ThrowsAndLeavesStorageUnchanged()
    {
        Assert.SkipUnless(MaxAppendBytes is not null, "The provider enforces no per-append limit of its own.");
        var journalId = CreateJournalId();
        var writer = CreateStorage(journalId);
        await writer.AppendAsync(Sequence(16, 1), CancellationToken);
        var versionBefore = History(writer).WriteVersion;

        await Assert.ThrowsAnyAsync<Exception>(
            () => writer.AppendAsync(LargeSequence(MaxAppendBytes!.Value + 1, 2), CancellationToken).AsTask());

        Assert.Equal(Bytes(16, 1), await ReadContentAsync(CreateStorage(journalId)));
        Assert.Equal(versionBefore, History(writer).WriteVersion);
        await writer.AppendAsync(Sequence(8, 3), CancellationToken);
        Assert.Equal(Concat([Bytes(16, 1), Bytes(8, 3)]), await ReadContentAsync(CreateStorage(journalId)));
    }

    [Fact]
    public async Task Write_AfterAnotherWriterAppended_ThrowsAndLeavesStorageUnchanged()
    {
        var journalId = CreateJournalId();
        var writer = CreateStorage(journalId);
        await writer.AppendAsync(Sequence(4, 1), CancellationToken);
        var other = CreateStorage(journalId);
        await ReadContentAsync(other);
        await other.AppendAsync(Sequence(5, 2), CancellationToken);

        await Assert.ThrowsAsync<InconsistentStateException>(() => writer.AppendAsync(Sequence(6, 3), CancellationToken).AsTask());
        await Assert.ThrowsAsync<InconsistentStateException>(() => writer.ReplaceAsync(Sequence(6, 3), CancellationToken).AsTask());
        await Assert.ThrowsAsync<InconsistentStateException>(() => writer.DeleteAsync(CancellationToken).AsTask());

        Assert.Equal(Concat([Bytes(4, 1), Bytes(5, 2)]), await ReadContentAsync(CreateStorage(journalId)));
    }

    [Fact]
    public async Task Write_AfterAnotherWriterDeleted_ThrowsAndKeepsPrecondition()
    {
        var journalId = CreateJournalId();
        var writer = CreateStorage(journalId);
        await writer.AppendAsync(Sequence(4, 1), CancellationToken);
        var writeVersion = History(writer).WriteVersion;
        var other = CreateStorage(journalId);
        await ReadContentAsync(other);
        await other.DeleteAsync(CancellationToken);

        // Each rejected write leaves the writer's precondition in place, so every later write is rejected too.
        await Assert.ThrowsAsync<InconsistentStateException>(() => writer.DeleteAsync(CancellationToken).AsTask());
        await Assert.ThrowsAsync<InconsistentStateException>(() => writer.ReplaceAsync(Sequence(5, 2), CancellationToken).AsTask());
        await Assert.ThrowsAsync<InconsistentStateException>(() => writer.AppendAsync(Sequence(6, 3), CancellationToken).AsTask());

        Assert.Equal(writeVersion, History(writer).WriteVersion);
        Assert.Empty(await ReadContentAsync(CreateStorage(journalId)));
    }

    [Fact]
    public async Task Write_AfterObservingAbsenceAndAnotherWriterCreated_Throws()
    {
        var journalId = CreateJournalId();
        var appender = CreateStorage(journalId);
        var replacer = CreateStorage(journalId);
        var deleter = CreateStorage(journalId);
        Assert.Empty(await ReadContentAsync(appender));
        Assert.Empty(await ReadContentAsync(replacer));
        Assert.Empty(await ReadContentAsync(deleter));
        await CreateStorage(journalId).AppendAsync(Sequence(5, 1), CancellationToken);

        await Assert.ThrowsAsync<InconsistentStateException>(() => appender.AppendAsync(Sequence(6, 2), CancellationToken).AsTask());
        await Assert.ThrowsAsync<InconsistentStateException>(() => replacer.ReplaceAsync(Sequence(6, 2), CancellationToken).AsTask());
        await Assert.ThrowsAsync<InconsistentStateException>(() => deleter.DeleteAsync(CancellationToken).AsTask());

        Assert.Equal(Bytes(5, 1), await ReadContentAsync(CreateStorage(journalId)));
    }

    [Fact]
    public async Task Write_AfterOwnDeleteAndAnotherWriterCreated_Throws()
    {
        var journalId = CreateJournalId();
        var writer = CreateStorage(journalId);
        await writer.AppendAsync(Sequence(5, 1), CancellationToken);
        await writer.DeleteAsync(CancellationToken);
        Assert.Equal(JournalStorageVersion.Absent, History(writer).WriteVersion);
        await CreateStorage(journalId).AppendAsync(Sequence(6, 2), CancellationToken);

        await Assert.ThrowsAsync<InconsistentStateException>(() => writer.AppendAsync(Sequence(7, 3), CancellationToken).AsTask());

        Assert.Equal(Bytes(6, 2), await ReadContentAsync(CreateStorage(journalId)));
    }

    [Fact]
    public async Task Write_AfterObservingAbsence_CreatesJournal()
    {
        var journalId = CreateJournalId();
        var appender = CreateStorage(journalId);
        Assert.Empty(await ReadContentAsync(appender));
        await appender.AppendAsync(Sequence(5, 1), CancellationToken);
        await appender.AppendAsync(Sequence(6, 2), CancellationToken);
        Assert.Equal(Concat([Bytes(5, 1), Bytes(6, 2)]), await ReadContentAsync(CreateStorage(journalId)));

        var replacer = CreateStorage(CreateJournalId());
        Assert.Empty(await ReadContentAsync(replacer));
        await replacer.ReplaceAsync(Sequence(7, 3), CancellationToken);
        await replacer.AppendAsync(Sequence(8, 4), CancellationToken);
        Assert.Equal(Concat([Bytes(7, 3), Bytes(8, 4)]), await ReadContentAsync(replacer));
    }

    [Fact]
    public async Task Write_BeforeReadingOrWriting_AdoptsCurrentVersion()
    {
        var journalId = CreateJournalId();
        await CreateStorage(journalId).AppendAsync(Sequence(5, 1), CancellationToken);
        var writer = CreateStorage(journalId);
        Assert.Equal(JournalStorageVersion.Unknown, History(writer).WriteVersion);

        await writer.AppendAsync(Sequence(6, 2), CancellationToken);

        Assert.Equal(Concat([Bytes(5, 1), Bytes(6, 2)]), await ReadContentAsync(CreateStorage(journalId)));
    }

    [Fact]
    public async Task ConcurrentWriters_FromOneVersion_ExactlyOneWinsAndNoPartialContentIsVisible()
    {
        const int WriterCount = 4;
        var journalId = CreateJournalId();
        await CreateStorage(journalId).AppendAsync(Sequence(8, 1), CancellationToken);
        var writers = new IJournalStorage[WriterCount];
        for (var i = 0; i < writers.Length; i++)
        {
            writers[i] = CreateStorage(journalId);
            await ReadContentAsync(writers[i]);
        }

        var segments = Enumerable.Range(0, WriterCount).Select(static i => Bytes(10_000 + i, (byte)(10 + i))).ToArray();
        var results = await Task.WhenAll(writers.Select((writer, i) => CaptureAsync(() => writer.AppendAsync(new ReadOnlySequence<byte>(segments[i]), CancellationToken))));

        var winner = Assert.Single(Enumerable.Range(0, WriterCount), i => results[i] is null);
        Assert.All(results.Where(static exception => exception is not null), static exception => Assert.IsType<InconsistentStateException>(exception));
        Assert.Equal(Concat([Bytes(8, 1), segments[winner]]), await ReadContentAsync(CreateStorage(journalId)));
    }

    [Fact]
    public async Task MetadataOperations_DoNotMoveEstablishedPrecondition()
    {
        var journalId = CreateJournalId();
        var writer = CreateStorage(journalId);
        await writer.AppendAsync(Sequence(4, 1), CancellationToken);
        var other = CreateStorage(journalId);
        await ReadContentAsync(other);
        await other.AppendAsync(Sequence(5, 2), CancellationToken);

        Assert.NotNull(await writer.GetMetadataAsync(CancellationToken));
        Assert.NotNull(await writer.UpdateMetadataAsync(new Dictionary<string, string> { ["conformance"] = "1" }, cancellationToken: CancellationToken));
        Assert.False(await writer.CreateIfNotExistsAsync(cancellationToken: CancellationToken));

        await Assert.ThrowsAsync<InconsistentStateException>(() => writer.AppendAsync(Sequence(6, 3), CancellationToken).AsTask());
        Assert.Equal(Concat([Bytes(4, 1), Bytes(5, 2)]), await ReadContentAsync(CreateStorage(journalId)));
    }

    [Fact]
    public async Task CreateIfNotExists_WhenCreating_EstablishesPrecondition()
    {
        var journalId = CreateJournalId();
        var writer = CreateStorage(journalId);
        Assert.Empty(await ReadContentAsync(writer));

        Assert.True(await writer.CreateIfNotExistsAsync(cancellationToken: CancellationToken));
        await writer.AppendAsync(Sequence(5, 1), CancellationToken);

        Assert.Equal(Bytes(5, 1), await ReadContentAsync(CreateStorage(journalId)));
    }

    [Fact]
    public async Task WriteVersion_IsUnknownUntilReadOrWrite()
    {
        var journalId = CreateJournalId();
        var storage = CreateStorage(journalId);
        Assert.Equal(JournalStorageVersion.Unknown, History(storage).WriteVersion);

        Assert.Equal(JournalStorageVersion.Absent, (await ReadHistoryAsync(storage)).Version);
        Assert.Equal(JournalStorageVersion.Unknown, History(storage).WriteVersion);

        await ReadContentAsync(storage);
        Assert.Equal(JournalStorageVersion.Absent, History(storage).WriteVersion);
    }

    [Fact]
    public async Task ReadHistory_AfterOwnWrites_ReturnsWriteVersion()
    {
        var journalId = CreateJournalId();
        var writer = CreateStorage(journalId);
        await ReadContentAsync(writer);
        await writer.AppendAsync(Sequence(5, 1), CancellationToken);
        await writer.AppendAsync(Sequence(6, 2), CancellationToken);

        var history = await ReadHistoryAsync(writer);
        Assert.Equal(Concat([Bytes(5, 1), Bytes(6, 2)]), history.Content);
        Assert.False(history.Version.IsAbsent);
        Assert.Equal(History(writer).WriteVersion, history.Version);

        await writer.ReplaceAsync(Sequence(7, 3), CancellationToken);
        history = await ReadHistoryAsync(writer);
        Assert.Equal(Bytes(7, 3), history.Content);
        Assert.Equal(History(writer).WriteVersion, history.Version);

        var reader = CreateStorage(journalId);
        await ReadContentAsync(reader);
        Assert.Equal(history.Version, History(reader).WriteVersion);
        Assert.Equal(history.Version, (await ReadHistoryAsync(reader)).Version);
    }

    [Fact]
    public async Task ReadHistory_AfterAnotherWriter_ReturnsNewContentAndKeepsPrecondition()
    {
        var journalId = CreateJournalId();
        var writer = CreateStorage(journalId);
        await writer.AppendAsync(Sequence(5, 1), CancellationToken);
        var writeVersion = History(writer).WriteVersion;
        var other = CreateStorage(journalId);
        await ReadContentAsync(other);
        await other.AppendAsync(Sequence(6, 2), CancellationToken);

        var history = await ReadHistoryAsync(writer);

        Assert.Equal(Concat([Bytes(5, 1), Bytes(6, 2)]), history.Content);
        Assert.NotEqual(writeVersion, history.Version);
        Assert.Equal(History(other).WriteVersion, history.Version);
        Assert.Equal(writeVersion, History(writer).WriteVersion);
        await Assert.ThrowsAsync<InconsistentStateException>(() => writer.AppendAsync(Sequence(7, 3), CancellationToken).AsTask());
    }

    [Fact]
    public async Task ReadHistory_AfterAnotherWriterCreatedObservedAbsentJournal_ReturnsNewVersion()
    {
        var journalId = CreateJournalId();
        var writer = CreateStorage(journalId);
        await ReadContentAsync(writer);
        await CreateStorage(journalId).AppendAsync(Sequence(5, 1), CancellationToken);

        var history = await ReadHistoryAsync(writer);

        Assert.Equal(Bytes(5, 1), history.Content);
        Assert.False(history.Version.IsAbsent);
        Assert.Equal(JournalStorageVersion.Absent, History(writer).WriteVersion);
    }

    [Fact]
    public async Task Append_WhenEveryRequestBodyIsAborted_FailsAndLeavesStorageUnchanged()
    {
        SkipUnlessSupported(JournalStorageFaultKind.AbortRequestBody);
        var journalId = CreateJournalId();
        var writer = CreateFaultingStorage(journalId);
        await writer.AppendAsync(Sequence(16, 1), CancellationToken);

        Faults.Arm(JournalStorageFaultKind.AbortRequestBody, count: int.MaxValue);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => writer.AppendAsync(Sequence(50_000, 2), CancellationToken).AsTask());
            Assert.True(Faults.InjectedCount > 0);
        }
        finally
        {
            Faults.Disarm();
        }

        Assert.Equal(Bytes(16, 1), await ReadContentAsync(CreateStorage(journalId)));
    }

    [Fact]
    public async Task Append_WhenOneRequestBodyIsAborted_WritesSegmentWholeOrNotAtAll()
    {
        SkipUnlessSupported(JournalStorageFaultKind.AbortRequestBody);
        var journalId = CreateJournalId();
        var writer = CreateFaultingStorage(journalId);
        await writer.AppendAsync(Sequence(16, 1), CancellationToken);

        Faults.Arm(JournalStorageFaultKind.AbortRequestBody, count: 1);
        var exception = await CaptureAsync(() => writer.AppendAsync(Sequence(50_000, 2), CancellationToken));
        Faults.Disarm();

        Assert.Equal(1, Faults.InjectedCount);
        var expected = exception is null ? Concat([Bytes(16, 1), Bytes(50_000, 2)]) : Bytes(16, 1);
        Assert.Equal(expected, await ReadContentAsync(CreateStorage(journalId)));
    }

    [Fact]
    public async Task Append_WhenResponseIsLost_WritesWholeSegmentOrNothing()
    {
        SkipUnlessSupported(JournalStorageFaultKind.LoseResponse);
        var journalId = CreateJournalId();
        var writer = CreateFaultingStorage(journalId);
        await writer.AppendAsync(Sequence(16, 1), CancellationToken);

        Faults.Arm(JournalStorageFaultKind.LoseResponse, count: 1);
        var exception = await CaptureAsync(() => writer.AppendAsync(Sequence(50_000, 2), CancellationToken));
        Faults.Disarm();

        Assert.Equal(1, Faults.InjectedCount);
        var content = await ReadContentAsync(CreateStorage(journalId));
        var whole = Concat([Bytes(16, 1), Bytes(50_000, 2)]);
        if (exception is null)
        {
            Assert.Equal(whole, content);
        }
        else
        {
            Assert.True(content.AsSpan().SequenceEqual(Bytes(16, 1)) || content.AsSpan().SequenceEqual(whole), $"Read {content.Length} bytes after a lost append response.");
        }
    }

    [Fact]
    public async Task Replace_WhenEveryRequestBodyIsAborted_FailsAndLeavesStorageUnchanged()
    {
        SkipUnlessSupported(JournalStorageFaultKind.AbortRequestBody);
        var journalId = CreateJournalId();
        var writer = CreateFaultingStorage(journalId);
        await writer.AppendAsync(Sequence(16, 1), CancellationToken);
        await writer.AppendAsync(Sequence(17, 2), CancellationToken);

        Faults.Arm(JournalStorageFaultKind.AbortRequestBody, count: int.MaxValue);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => writer.ReplaceAsync(Sequence(200_000, 3), CancellationToken).AsTask());
        }
        finally
        {
            Faults.Disarm();
        }

        Assert.Equal(Concat([Bytes(16, 1), Bytes(17, 2)]), await ReadContentAsync(CreateStorage(journalId)));
    }

    [Fact]
    public async Task RandomOperations_WithInjectedFailures_MatchModel()
    {
        var operation = Gen.OneOf(
            Gen.Byte.Array[1, 300].Select(static data => new ModelOperation(ModelOperationKind.Append, data)),
            Gen.Byte.Array[1, 300].Select(static data => new ModelOperation(ModelOperationKind.Replace, data)),
            Gen.Byte.Array[1, 64].Select(static data => new ModelOperation(ModelOperationKind.ForeignAppend, data)),
            Gen.Const(new ModelOperation(ModelOperationKind.ForeignDelete, [])),
            Gen.Const(new ModelOperation(ModelOperationKind.Read, [])),
            Gen.Const(new ModelOperation(ModelOperationKind.Delete, [])),
            Gen.Byte.Array[1, 64].Select(static data => new ModelOperation(ModelOperationKind.OversizedAppend, data)),
            Gen.Byte.Array[1, 300].Select(static data => new ModelOperation(ModelOperationKind.AbortedAppend, data)),
            Gen.Byte.Array[1, 300].Select(static data => new ModelOperation(ModelOperationKind.LostResponseAppend, data)));

        await operation.Array[1, 12].SampleAsync(
            RunModelAsync,
            iter: PropertyIterations,
            threads: 1,
            print: static operations => string.Join(", ", operations.Select(static operation => $"{operation.Kind}({operation.Data.Length})")));
    }

    // Runs one operation sequence against a fresh journal through one writer, checking every outcome and the content after
    // every operation against a model of the journal and of the writer's precondition.
    private async Task RunModelAsync(ModelOperation[] operations)
    {
        var journalId = CreateJournalId();
        var writer = CreateWriter();
        var model = new JournalModel();

        foreach (var operation in operations)
        {
            switch (operation.Kind)
            {
                case ModelOperationKind.Append:
                    await ExpectWriteAsync(() => writer.AppendAsync(new ReadOnlySequence<byte>(operation.Data), CancellationToken), () => model.Append(operation.Data));
                    break;
                case ModelOperationKind.Replace:
                    await ExpectWriteAsync(() => writer.ReplaceAsync(new ReadOnlySequence<byte>(operation.Data), CancellationToken), () => model.Replace(operation.Data));
                    break;
                case ModelOperationKind.Delete:
                    await ExpectWriteAsync(() => writer.DeleteAsync(CancellationToken), model.Delete);
                    break;
                case ModelOperationKind.ForeignAppend:
                {
                    var other = CreateStorage(journalId);
                    await ReadContentAsync(other);
                    await other.AppendAsync(new ReadOnlySequence<byte>(operation.Data), CancellationToken);
                    model.ForeignAppend(operation.Data);
                    break;
                }

                case ModelOperationKind.ForeignDelete:
                {
                    var other = CreateStorage(journalId);
                    await ReadContentAsync(other);
                    await other.DeleteAsync(CancellationToken);
                    model.ForeignDelete();
                    break;
                }

                case ModelOperationKind.Read:
                    Assert.Equal(model.Content, await ReadContentAsync(writer));
                    model.Read(exists: !History(writer).WriteVersion.IsAbsent);
                    break;
                case ModelOperationKind.OversizedAppend when MaxAppendBytes is { } maxAppendBytes:
                {
                    var versionBefore = History(writer).WriteVersion;
                    var oversized = LargeSequence(maxAppendBytes + 1, operation.Data[0]);
                    await Assert.ThrowsAnyAsync<Exception>(() => writer.AppendAsync(oversized, CancellationToken).AsTask());
                    Assert.Equal(versionBefore, History(writer).WriteVersion);
                    break;
                }

                case ModelOperationKind.AbortedAppend when SupportedFaults.Contains(JournalStorageFaultKind.AbortRequestBody):
                {
                    Faults.Arm(JournalStorageFaultKind.AbortRequestBody, count: int.MaxValue);
                    Exception? exception;
                    try
                    {
                        exception = await CaptureAsync(() => writer.AppendAsync(new ReadOnlySequence<byte>(operation.Data), CancellationToken));
                    }
                    finally
                    {
                        Faults.Disarm();
                    }

                    Assert.NotNull(exception);
                    await RecoverAfterFailureAsync();
                    break;
                }

                case ModelOperationKind.LostResponseAppend when SupportedFaults.Contains(JournalStorageFaultKind.LoseResponse):
                {
                    Faults.Arm(JournalStorageFaultKind.LoseResponse, count: 1);
                    var exception = await CaptureAsync(() => writer.AppendAsync(new ReadOnlySequence<byte>(operation.Data), CancellationToken));
                    Faults.Disarm();
                    if (exception is null)
                    {
                        Assert.False(model.IsWriterStale);
                        model.Append(operation.Data);
                        break;
                    }

                    // The outcome is indeterminate: the segment was written whole or not at all.
                    var content = await ReadContentAsync(CreateStorage(journalId));
                    var withSegment = Concat([model.Content, operation.Data]);
                    var applied = !model.IsWriterStale && content.AsSpan().SequenceEqual(withSegment);
                    Assert.True(
                        applied || content.AsSpan().SequenceEqual(model.Content),
                        $"Read {content.Length} bytes after a lost append response; expected {model.Content.Length} or {withSegment.Length}.");
                    if (applied)
                    {
                        model.ForeignAppend(operation.Data);
                    }

                    await RecoverAfterFailureAsync();
                    break;
                }

                default:
                    // The provider has no per-append limit or fault injection, so the operation is an ordinary append.
                    await ExpectWriteAsync(() => writer.AppendAsync(new ReadOnlySequence<byte>(operation.Data), CancellationToken), () => model.Append(operation.Data));
                    break;
            }

            Assert.Equal(model.Content, await ReadContentAsync(CreateStorage(journalId)));
        }

        IJournalStorage CreateWriter() => SupportsFaultInjection ? CreateFaultingStorage(journalId) : CreateStorage(journalId);

        async Task ExpectWriteAsync(Func<ValueTask> write, Action apply)
        {
            if (model.IsWriterStale)
            {
                await Assert.ThrowsAsync<InconsistentStateException>(() => write().AsTask());
            }
            else
            {
                await write();
                apply();
            }
        }

        // The journaled state manager recovers from a failed write with a fresh read, so the model does the same. The read also
        // shows whether a failed write left an empty journal behind.
        async Task RecoverAfterFailureAsync()
        {
            writer = CreateWriter();
            Assert.Equal(model.Content, await ReadContentAsync(writer));
            model.Read(exists: !History(writer).WriteVersion.IsAbsent);
        }
    }

    private void SkipUnlessSupported(JournalStorageFaultKind fault)
        => Assert.SkipUnless(SupportedFaults.Contains(fault), $"The provider's conformance tests do not inject {fault} faults.");

    protected IJournalStorage CreateStorage(JournalId journalId)
        => (_provider ?? throw new InvalidOperationException("The test has not been initialized.")).CreateStorage(journalId);

    protected IJournalStorage CreateFaultingStorage(JournalId journalId)
        => (_faultingProvider ?? throw new InvalidOperationException("The provider does not support fault injection.")).CreateStorage(journalId);

    protected virtual JournalId CreateJournalId() => new($"conformance/{Guid.NewGuid():N}");

    protected static async Task<byte[]> ReadContentAsync(IJournalStorage storage)
    {
        var consumer = new CollectingConsumer();
        await storage.ReadAsync(consumer, CancellationToken);
        Assert.True(consumer.IsCompleted);
        return consumer.Content.ToArray();
    }

    private static async Task<(byte[] Content, JournalStorageVersion Version)> ReadHistoryAsync(IJournalStorage storage)
    {
        var consumer = new CollectingConsumer();
        var version = await History(storage).ReadHistoryAsync(consumer, CancellationToken);
        Assert.True(consumer.IsCompleted);
        Assert.NotEqual(JournalStorageVersion.Unknown, version);
        return (consumer.Content.ToArray(), version);
    }

    private static IJournalHistoryStorage History(IJournalStorage storage)
        => Assert.IsAssignableFrom<IJournalHistoryStorage>(storage);

    private static async Task<Exception?> CaptureAsync(Func<ValueTask> operation)
    {
        try
        {
            await operation();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static byte[] Bytes(int length, byte seed)
    {
        var result = new byte[length];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = unchecked((byte)(seed + i * 31));
        }

        return result;
    }

    private static ReadOnlySequence<byte> Sequence(int length, byte seed) => new(Bytes(length, seed));

    // Builds a sequence of the given length from one shared chunk, so limit-sized segments need no limit-sized buffer.
    private static ReadOnlySequence<byte> LargeSequence(long length, byte seed)
    {
        var chunk = Bytes(1024 * 1024, seed);
        var first = new SequenceSegment(chunk.AsMemory(0, (int)Math.Min(chunk.Length, length)));
        var last = first;
        for (var remaining = length - last.Memory.Length; remaining > 0; remaining -= last.Memory.Length)
        {
            last = last.Append(chunk.AsMemory(0, (int)Math.Min(chunk.Length, remaining)));
        }

        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private static byte[] Concat(IEnumerable<byte[]> segments) => segments.SelectMany(static segment => segment).ToArray();

    private enum ModelOperationKind
    {
        Append,
        Replace,
        Delete,
        ForeignAppend,
        ForeignDelete,
        Read,
        OversizedAppend,
        AbortedAppend,
        LostResponseAppend,
    }

    private sealed record ModelOperation(ModelOperationKind Kind, byte[] Data);

    // Models the journal's content and version, and the version which the writer's precondition holds. Versions compare by
    // value for an absent journal, as provider preconditions do.
    private sealed class JournalModel
    {
        private long _version;
        private bool _exists;
        private (bool Known, bool Exists, long Version) _writer;

        public byte[] Content { get; private set; } = [];

        public bool IsWriterStale => _writer.Known && (_writer.Exists != _exists || (_exists && _writer.Version != _version));

        public void Append(byte[] data) => WriterWrote(() => Content = Concat([Content, data]), exists: true);

        public void Replace(byte[] data) => WriterWrote(() => Content = data, exists: true);

        public void Delete() => WriterWrote(() => Content = [], exists: false);

        public void ForeignAppend(byte[] data) => Change(() => Content = Concat([Content, data]), exists: true);

        public void ForeignDelete() => Change(() => Content = [], exists: false);

        public void Read(bool exists)
        {
            if (exists != _exists)
            {
                Change(static () => { }, exists);
            }

            _writer = (true, _exists, _version);
        }

        private void WriterWrote(Action change, bool exists)
        {
            Change(change, exists);
            _writer = (true, _exists, _version);
        }

        private void Change(Action change, bool exists)
        {
            change();
            _exists = exists;
            _version++;
        }
    }

    private sealed class CollectingConsumer : IJournalStorageConsumer
    {
        public List<byte> Content { get; } = [];

        public bool IsCompleted { get; private set; }

        public void Read(JournalBufferReader buffer, IJournalMetadata? metadata)
        {
            Assert.False(IsCompleted);
            Content.AddRange(buffer.ToArray());
            buffer.Skip(buffer.Length);
            IsCompleted = buffer.IsCompleted;
        }
    }

    private sealed class HashingConsumer : IJournalStorageConsumer
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public long Length { get; private set; }

        public bool IsCompleted { get; private set; }

        public void Add(ReadOnlySpan<byte> data)
        {
            _hash.AppendData(data);
            Length += data.Length;
        }

        public byte[] GetHash() => _hash.GetHashAndReset();

        public void Read(JournalBufferReader buffer, IJournalMetadata? metadata)
        {
            Add(buffer.ToArray());
            buffer.Skip(buffer.Length);
            IsCompleted = buffer.IsCompleted;
        }
    }

    private sealed class SequenceSegment : ReadOnlySequenceSegment<byte>
    {
        public SequenceSegment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public SequenceSegment Append(ReadOnlyMemory<byte> memory)
        {
            var segment = new SequenceSegment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = segment;
            return segment;
        }
    }
}

/// <summary>
/// The transport faults which <see cref="JournalStorageFaultInjector"/> injects into a provider's write requests.
/// </summary>
public enum JournalStorageFaultKind
{
    /// <summary>
    /// The request body fails partway through sending, so storage receives part of the request.
    /// </summary>
    AbortRequestBody,

    /// <summary>
    /// Storage applies the request, and the client then loses the response.
    /// </summary>
    LoseResponse,
}

/// <summary>
/// Injects transport faults into a provider's write requests.
/// </summary>
public sealed class JournalStorageFaultInjector
{
    private readonly object _lock = new();
    private JournalStorageFaultKind _kind;
    private int _remaining;
    private int _injectedCount;

    /// <summary>
    /// Gets the number of faults injected since the injector was last armed.
    /// </summary>
    public int InjectedCount
    {
        get
        {
            lock (_lock)
            {
                return _injectedCount;
            }
        }
    }

    /// <summary>
    /// Injects <paramref name="kind"/> into the next <paramref name="count"/> write requests which can carry it.
    /// </summary>
    public void Arm(JournalStorageFaultKind kind, int count)
    {
        lock (_lock)
        {
            _kind = kind;
            _remaining = count;
            _injectedCount = 0;
        }
    }

    /// <summary>
    /// Stops injecting faults.
    /// </summary>
    public void Disarm()
    {
        lock (_lock)
        {
            _remaining = 0;
        }
    }

    /// <summary>
    /// Takes one armed fault of <paramref name="kind"/>, if any remain.
    /// </summary>
    public bool TryTake(JournalStorageFaultKind kind)
    {
        lock (_lock)
        {
            if (_remaining == 0 || _kind != kind)
            {
                return false;
            }

            _remaining--;
            _injectedCount++;
            return true;
        }
    }
}
