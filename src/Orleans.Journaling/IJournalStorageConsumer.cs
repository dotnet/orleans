namespace Orleans.Journaling;

/// <summary>
/// Consumes raw journal data read from an <see cref="IJournalStorage"/> instance.
/// </summary>
public interface IJournalStorageConsumer
{
    /// <summary>
    /// Consumes buffered raw journal data.
    /// </summary>
    /// <param name="buffer">The buffered journal data available to the consumer.</param>
    /// <param name="metadata">The metadata associated with the journal file being read.</param>
    void Consume(JournalReadBuffer buffer, IJournalFileMetadata metadata);
}

/// <summary>
/// Metadata associated with a journal file being read from storage.
/// </summary>
public interface IJournalFileMetadata
{
    /// <summary>
    /// Gets the journal format key stored with the journal data, or <see langword="null"/> if no key is present.
    /// </summary>
    string? Format { get; }
}

/// <summary>
/// Default implementation of <see cref="IJournalFileMetadata"/>.
/// </summary>
public sealed class JournalFileMetadata : IJournalFileMetadata
{
    /// <summary>
    /// Gets an empty metadata instance for journal data without storage metadata.
    /// </summary>
    public static IJournalFileMetadata Empty { get; } = new JournalFileMetadata(format: null);

    /// <summary>
    /// Initializes a new instance of the <see cref="JournalFileMetadata"/> class.
    /// </summary>
    /// <param name="format">The journal format key stored with the journal data, or <see langword="null"/> if no key is present.</param>
    public JournalFileMetadata(string? format)
    {
        Format = format;
    }

    /// <inheritdoc/>
    public string? Format { get; }
}
