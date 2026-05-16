namespace Orleans.Journaling;

/// <summary>
/// Provides catalog operations for journal storage instances.
/// </summary>
/// <remarks>
/// A catalog manages storage identities and storage properties. Journal data deletion remains on
/// <see cref="IJournalStorage.DeleteAsync(CancellationToken)"/>.
/// </remarks>
public interface IJournalStorageCatalog
{
    /// <summary>
    /// Lists journal storage ids which match <paramref name="prefix"/>.
    /// </summary>
    /// <param name="prefix">The storage id prefix.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Matching ids in lexicographic <see cref="JournalStorageId.Value"/> order.</returns>
    IAsyncEnumerable<JournalStorageId> ListAsync(JournalStoragePrefix prefix, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a storage instance if it does not already exist.
    /// </summary>
    /// <param name="storageId">The storage id to create.</param>
    /// <param name="properties">Initial caller-owned properties. Provider-owned properties are reserved and must be rejected by providers.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The create result.</returns>
    ValueTask<JournalStorageCreateResult> CreateIfNotExistsAsync(
        JournalStorageId storageId,
        IReadOnlyDictionary<string, string>? properties = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current storage properties for <paramref name="storageId"/>.
    /// </summary>
    /// <param name="storageId">The storage id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The storage properties, or <see langword="null"/> if the storage instance does not exist.</returns>
    ValueTask<JournalStorageProperties?> GetPropertiesAsync(JournalStorageId storageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Conditionally updates caller-owned storage properties.
    /// </summary>
    /// <remarks>
    /// Providers apply <paramref name="update"/> atomically against the current properties. When
    /// <paramref name="expectedETag"/> is not <see langword="null"/>, providers which support ETags
    /// must only apply the update if the current properties ETag matches it. Provider-owned properties
    /// must be preserved.
    /// </remarks>
    /// <param name="storageId">The storage id.</param>
    /// <param name="update">The property update.</param>
    /// <param name="expectedETag">The expected properties ETag, or <see langword="null"/> for an unconditional update.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The update result.</returns>
    ValueTask<JournalStoragePropertiesUpdateResult> UpdatePropertiesAsync(
        JournalStorageId storageId,
        JournalStoragePropertiesUpdate update,
        string? expectedETag,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Describes the result of creating a journal storage instance.
/// </summary>
public sealed class JournalStorageCreateResult
{
    /// <summary>
    /// Initializes a new instance of the <see cref="JournalStorageCreateResult"/> class.
    /// </summary>
    /// <param name="status">The create status.</param>
    /// <param name="properties">The current storage properties when available.</param>
    public JournalStorageCreateResult(JournalStorageCreateStatus status, JournalStorageProperties? properties)
    {
        Status = status;
        Properties = properties;
    }

    /// <summary>
    /// Gets the create status.
    /// </summary>
    public JournalStorageCreateStatus Status { get; }

    /// <summary>
    /// Gets the current storage properties when available.
    /// </summary>
    public JournalStorageProperties? Properties { get; }
}

/// <summary>
/// The result status for a journal storage create operation.
/// </summary>
public enum JournalStorageCreateStatus
{
    /// <summary>
    /// The storage instance was created.
    /// </summary>
    Created,

    /// <summary>
    /// The storage instance already existed.
    /// </summary>
    AlreadyExists,

    /// <summary>
    /// The storage instance could not be created because of an identity or property conflict.
    /// </summary>
    Conflict
}

/// <summary>
/// Describes the result of a journal storage properties update.
/// </summary>
public sealed class JournalStoragePropertiesUpdateResult
{
    /// <summary>
    /// Initializes a new instance of the <see cref="JournalStoragePropertiesUpdateResult"/> class.
    /// </summary>
    /// <param name="status">The update status.</param>
    /// <param name="properties">The current storage properties when available.</param>
    public JournalStoragePropertiesUpdateResult(JournalStoragePropertiesUpdateStatus status, JournalStorageProperties? properties)
    {
        Status = status;
        Properties = properties;
    }

    /// <summary>
    /// Gets the update status.
    /// </summary>
    public JournalStoragePropertiesUpdateStatus Status { get; }

    /// <summary>
    /// Gets the current storage properties when available.
    /// </summary>
    public JournalStorageProperties? Properties { get; }
}

/// <summary>
/// The result status for a journal storage properties update.
/// </summary>
public enum JournalStoragePropertiesUpdateStatus
{
    /// <summary>
    /// The properties were updated.
    /// </summary>
    Updated,

    /// <summary>
    /// The storage instance does not exist.
    /// </summary>
    NotFound,

    /// <summary>
    /// The update was not applied because the supplied ETag did not match the current ETag.
    /// </summary>
    ETagMismatch,

    /// <summary>
    /// The update did not change any properties.
    /// </summary>
    NoChange
}
