namespace Orleans.Journaling;

/// <summary>
/// Reads raw journal data from an <see cref="IJournalStorage"/> instance.
/// </summary>
public interface IJournalStorageConsumer
{
    /// <summary>
    /// Reads buffered raw journal data.
    /// </summary>
    /// <param name="buffer">The buffered journal data available to the consumer.</param>
    /// <param name="metadata">The metadata associated with the journal data being read, or <see langword="null"/> if no metadata is available.</param>
    void Read(JournalBufferReader buffer, IJournalMetadata? metadata);
}

/// <summary>
/// Metadata associated with journal storage.
/// </summary>
public interface IJournalMetadata
{
    /// <summary>
    /// Gets the journal format key stored with the journal data, or <see langword="null"/> if no key is present.
    /// </summary>
    string? Format { get; }

    /// <summary>
    /// Gets the storage metadata ETag, or <see langword="null"/> if none is available.
    /// </summary>
    string? ETag { get; }

    /// <summary>
    /// Gets caller-owned storage metadata properties.
    /// </summary>
    IReadOnlyDictionary<string, string> Properties { get; }
}

/// <summary>
/// Default implementation of <see cref="IJournalMetadata"/>.
/// </summary>
public sealed class JournalMetadata : IJournalMetadata
{
    /// <summary>
    /// Gets an empty metadata instance.
    /// </summary>
    public static IJournalMetadata Empty { get; } = new JournalMetadata(format: null, eTag: null, properties: null);

    /// <summary>
    /// Initializes a new instance of the <see cref="JournalMetadata"/> class.
    /// </summary>
    /// <param name="format">The journal format key stored with the journal data, or <see langword="null"/> if no key is present.</param>
    /// <param name="eTag">The storage metadata ETag, or <see langword="null"/> if none is available.</param>
    /// <param name="properties">Caller-owned storage metadata properties.</param>
    public JournalMetadata(string? format, string? eTag = null, IReadOnlyDictionary<string, string>? properties = null)
    {
        Format = format;
        ETag = eTag;
        Properties = CopyProperties(properties);
    }

    /// <inheritdoc/>
    public string? Format { get; }

    /// <inheritdoc/>
    public string? ETag { get; }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, string> Properties { get; }

    internal static Dictionary<string, string> CopyProperties(IReadOnlyDictionary<string, string>? properties)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (properties is null)
        {
            return result;
        }

        foreach (var (key, value) in properties)
        {
            ValidateCallerProperty(key, value);
            result.Add(key, value);
        }

        return result;
    }

    internal static void ValidatePropertyName(string propertyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
        if (propertyName.IndexOf('\0') >= 0)
        {
            throw new ArgumentException("Journal metadata property names must not contain null characters.", nameof(propertyName));
        }
    }

    internal static void ValidateCallerPropertyName(string propertyName)
    {
        ValidatePropertyName(propertyName);
        if (IsProviderOwned(propertyName))
        {
            throw new ArgumentException(
                $"Journal metadata property '{propertyName}' is provider-owned. Caller updates must not set or remove provider-owned properties.",
                nameof(propertyName));
        }
    }

    internal static void ValidateCallerProperty(string propertyName, string value)
    {
        ValidateCallerPropertyName(propertyName);
        ArgumentNullException.ThrowIfNull(value);
    }

    internal static bool IsProviderOwned(string propertyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
        return propertyName.StartsWith("$", StringComparison.Ordinal);
    }
}
