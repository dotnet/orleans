namespace Orleans.Journaling;

/// <summary>
/// Identifies a version of a journal's content in storage: the value which a storage instance's write precondition compares.
/// </summary>
/// <remarks>
/// Equal versions of an existing journal identify the same content version, with no intervening content write or deletion.
/// All observations of an absent journal use <see cref="Absent"/>: equality confirms absence at both observations, including
/// when another writer created and deleted the journal between them. A metadata update leaves the version unchanged unless
/// the provider rewrites the stored journal to apply it, as the S3 provider does.
/// Providers create versions with <see cref="Create"/>; the default value is <see cref="Unknown"/>.
/// </remarks>
internal readonly record struct JournalStorageVersion
{
    private JournalStorageVersion(string value) => Value = value;

    /// <summary>
    /// Gets the version of a storage instance which has neither read nor written the journal.
    /// </summary>
    public static JournalStorageVersion Unknown => default;

    /// <summary>
    /// Gets the version of an absent journal.
    /// </summary>
    public static JournalStorageVersion Absent { get; } = new(string.Empty);

    /// <summary>
    /// Gets the provider-specific version token: <see langword="null"/> when unknown and empty when absent.
    /// </summary>
    public string? Value { get; }

    /// <summary>
    /// Gets a value indicating whether the journal is absent at this version.
    /// </summary>
    public bool IsAbsent => Value is { Length: 0 };

    /// <summary>
    /// Creates the version of an existing journal.
    /// </summary>
    /// <param name="token">The provider-specific, non-empty token which identifies the content version.</param>
    public static JournalStorageVersion Create(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        return new(token);
    }

    /// <inheritdoc/>
    public override string ToString() => Value switch
    {
        null => "<unknown>",
        { Length: 0 } => "<absent>",
        var value => value,
    };
}

/// <summary>
/// Reads journal history without changing the storage instance's write precondition.
/// </summary>
/// <remarks>
/// Every journal storage provider in this repository implements this interface. A caller which already writes through a storage
/// instance reads the journal with <see cref="ReadHistoryAsync"/> and compares the returned version with
/// <see cref="WriteVersion"/>: equal versions of an existing journal mean the content read is exactly the content this instance
/// last read or wrote, and that no other writer changed the journal since. Equal <see cref="JournalStorageVersion.Absent"/>
/// versions confirm absence at both observations, including after an intervening creation and deletion.
/// </remarks>
internal interface IJournalHistoryStorage
{
    /// <summary>
    /// Gets the version which the next <see cref="IJournalStorage.AppendAsync"/>, <see cref="IJournalStorage.ReplaceAsync"/>,
    /// or <see cref="IJournalStorage.DeleteAsync"/> through this instance requires the journal to have.
    /// </summary>
    /// <remarks>
    /// The value is <see cref="JournalStorageVersion.Unknown"/> until this instance reads the journal with
    /// <see cref="IJournalStorage.ReadAsync"/> or writes it. Read it while no write through this instance is in flight.
    /// </remarks>
    JournalStorageVersion WriteVersion { get; }

    /// <summary>
    /// Reads the whole journal as <see cref="IJournalStorage.ReadAsync"/> does, and returns the version of the content read.
    /// </summary>
    /// <remarks>
    /// The read leaves <see cref="WriteVersion"/> and every other part of this instance's write state unchanged.
    /// </remarks>
    /// <param name="consumer">The consumer of ordered raw journal data.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The version of the content read, or <see cref="JournalStorageVersion.Absent"/> when the journal does not exist.</returns>
    ValueTask<JournalStorageVersion> ReadHistoryAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken);
}
