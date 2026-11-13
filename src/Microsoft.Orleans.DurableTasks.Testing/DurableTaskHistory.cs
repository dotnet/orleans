using System.Buffers;
using System.Globalization;
using System.Text.Json;
using Orleans.Journaling;
using Orleans.Runtime;

namespace Orleans.DurableTasks.Testing;

/// <summary>
/// A grain's recorded durable task history: the committed journal of the grain, which holds its tasks, their recorded
/// operations and outcomes, and the grain's journaled state, captured so that tests replay it against later code.
/// </summary>
/// <remarks>
/// <para>
/// Capture a history with <see cref="CaptureAsync"/> from the journal storage of a test cluster or of a deployment, store
/// it with <see cref="WriteAsync"/> or <see cref="SaveAsync"/>, and replay it with
/// <see cref="DurableTaskReplayer.ReplayAsync"/>. The history keeps the journal's bytes exactly, in the journal format
/// which recorded them, so request, result, and state payloads replay through the current code's serializers.
/// </para>
/// <para>
/// A history file is a JSON document: <c>{ "version": 1, "grainId", "journalFormat", "capturedAt", "journal" }</c>,
/// where <c>journal</c> holds the journal's bytes in base64.
/// </para>
/// </remarks>
public sealed class DurableTaskHistory
{
    private const int FileVersion = 1;
    private readonly byte[] _journal;

    private DurableTaskHistory(GrainId grainId, string journalFormat, DateTimeOffset capturedAt, byte[] journal)
    {
        GrainId = grainId;
        JournalFormat = journalFormat;
        CapturedAt = capturedAt;
        _journal = journal;
    }

    /// <summary>Gets the grain whose journal the history holds, which a replay activates.</summary>
    public GrainId GrainId { get; }

    /// <summary>Gets the key of the journal format which recorded the history, such as <c>json</c> or <c>orleans-binary</c>.</summary>
    public string JournalFormat { get; }

    /// <summary>Gets when the history was captured.</summary>
    public DateTimeOffset CapturedAt { get; }

    /// <summary>Gets the journal's bytes.</summary>
    internal ReadOnlyMemory<byte> Journal => _journal;

    /// <summary>Captures the committed journal of <paramref name="grainId"/> from <paramref name="storageProvider"/>.</summary>
    /// <param name="storageProvider">
    /// The journal storage which the grain's silo uses, such as the <see cref="IJournalStorageProvider"/> service of a test
    /// cluster's silo, or a provider which reads a deployment's storage.
    /// </param>
    /// <param name="grainId">The grain whose history to capture.</param>
    /// <param name="cancellationToken">A token which cancels the capture.</param>
    /// <returns>The captured history.</returns>
    /// <remarks>
    /// The capture reads what the journal has committed. Capture a running grain at a point where its tasks wait, such as
    /// for a signal or a child's result, to record the history up to that point.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The storage holds no journal for <paramref name="grainId"/>.</exception>
    public static async ValueTask<DurableTaskHistory> CaptureAsync(
        IJournalStorageProvider storageProvider,
        GrainId grainId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storageProvider);
        if (grainId.IsDefault)
        {
            throw new ArgumentException("The grain ID must not be the default value.", nameof(grainId));
        }

        var storage = storageProvider.CreateStorage(JournalId.FromGrainId(grainId));
        var consumer = new CapturingConsumer();
        await storage.ReadAsync(consumer, cancellationToken).ConfigureAwait(false);
        if (consumer.Bytes.Length == 0)
        {
            throw new InvalidOperationException($"The journal storage holds no journal for grain '{grainId}'.");
        }

        return new(
            grainId,
            consumer.FormatKey ?? new JournaledStateManagerOptions().JournalFormatKey,
            DateTimeOffset.UtcNow,
            consumer.Bytes.ToArray());
    }

    /// <summary>Writes the history to <paramref name="stream"/> as a JSON document.</summary>
    /// <param name="stream">The stream to write to.</param>
    /// <param name="cancellationToken">A token which cancels the write.</param>
    public async ValueTask WriteAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        await using var writer = new Utf8JsonWriter(stream, new() { Indented = true });
        writer.WriteStartObject();
        writer.WriteNumber("version", FileVersion);
        writer.WriteString("grainId", GrainId.ToString());
        writer.WriteString("journalFormat", JournalFormat);
        writer.WriteString("capturedAt", CapturedAt.ToString("O", CultureInfo.InvariantCulture));
        writer.WriteBase64String("journal", _journal);
        writer.WriteEndObject();
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads a history which <see cref="WriteAsync"/> wrote from <paramref name="stream"/>.</summary>
    /// <param name="stream">The stream to read from.</param>
    /// <param name="cancellationToken">A token which cancels the read.</param>
    /// <returns>The history.</returns>
    /// <exception cref="FormatException">The stream doesn't hold a durable task history.</exception>
    public static async ValueTask<DurableTaskHistory> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new FormatException("The stream doesn't hold a durable task history.", exception);
        }

        using (document)
        {
            try
            {
                var root = document.RootElement;
                var version = root.GetProperty("version").GetInt32();
                if (version != FileVersion)
                {
                    throw new FormatException($"Durable task history version {version} isn't supported; this version reads version {FileVersion}.");
                }

                return new(
                    GrainId.Parse(root.GetProperty("grainId").GetString()!),
                    root.GetProperty("journalFormat").GetString()!,
                    DateTimeOffset.Parse(root.GetProperty("capturedAt").GetString()!, CultureInfo.InvariantCulture),
                    root.GetProperty("journal").GetBytesFromBase64());
            }
            catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or ArgumentException)
            {
                throw new FormatException("The stream doesn't hold a durable task history.", exception);
            }
        }
    }

    /// <summary>Writes the history to the file <paramref name="path"/>, replacing it if it exists.</summary>
    /// <param name="path">The file's path.</param>
    /// <param name="cancellationToken">A token which cancels the write.</param>
    public async Task SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        await using var stream = File.Create(path);
        await WriteAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads a history which <see cref="SaveAsync"/> wrote from the file <paramref name="path"/>.</summary>
    /// <param name="path">The file's path.</param>
    /// <param name="cancellationToken">A token which cancels the read.</param>
    /// <returns>The history.</returns>
    /// <exception cref="FormatException">The file doesn't hold a durable task history.</exception>
    public static async Task<DurableTaskHistory> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        await using var stream = File.OpenRead(path);
        return await ReadAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"Durable task history of grain '{GrainId}' ({JournalFormat}, {_journal.Length} bytes, captured {CapturedAt:O})";

    /// <summary>Copies the journal's bytes and its format key as storage supplies them.</summary>
    private sealed class CapturingConsumer : IJournalStorageConsumer
    {
        public MemoryStream Bytes { get; } = new();

        public string? FormatKey { get; private set; }

        public void Read(JournalBufferReader buffer, IJournalMetadata? metadata)
        {
            FormatKey ??= metadata?.FormatKey;
            while (buffer.Length > 0)
            {
                var chunk = new byte[buffer.Length];
                buffer.Read(chunk);
                Bytes.Write(chunk);
            }
        }
    }
}
