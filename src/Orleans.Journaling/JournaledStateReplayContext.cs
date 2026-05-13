namespace Orleans.Journaling;

/// <summary>
/// Provides services used by journaled states during replay.
/// </summary>
public readonly ref struct JournaledStateReplayContext
{
    /// <summary>
    /// Initializes a new instance of the <see cref="JournaledStateReplayContext"/> struct.
    /// </summary>
    /// <param name="writeJournalFormatKey">The configured write journal format key.</param>
    /// <param name="serviceProvider">The application-level service provider used to resolve codecs for journal formats.</param>
    public JournaledStateReplayContext(string writeJournalFormatKey, IServiceProvider serviceProvider)
    {
        WriteJournalFormatKey = JournalFormatServices.ValidateJournalFormatKey(writeJournalFormatKey);
        ServiceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    /// <summary>
    /// Gets the configured write journal format key.
    /// </summary>
    public string WriteJournalFormatKey { get; } = string.Empty;

    /// <summary>
    /// Gets the application-level service provider used to resolve journal services.
    /// </summary>
    /// <remarks>
    /// This provider is not scoped to a grain activation or replay operation.
    /// </remarks>
    public IServiceProvider ServiceProvider { get; }

    /// <summary>
    /// Gets the command codec for <paramref name="entryFormatKey"/>.
    /// </summary>
    /// <typeparam name="TCodec">The command codec service type.</typeparam>
    /// <param name="entryFormatKey">The journal format key for the entry being replayed.</param>
    /// <param name="writeCommandCodec">The command codec for the configured write journal format.</param>
    /// <returns>The command codec for <paramref name="entryFormatKey"/>.</returns>
    public TCodec GetRequiredCommandCodec<TCodec>(string entryFormatKey, TCodec writeCommandCodec)
        where TCodec : notnull
    {
        ArgumentNullException.ThrowIfNull(writeCommandCodec);
        entryFormatKey = JournalFormatServices.ValidateJournalFormatKey(entryFormatKey);

        if (string.Equals(entryFormatKey, WriteJournalFormatKey, StringComparison.Ordinal))
        {
            return writeCommandCodec;
        }

        return JournalFormatServices.GetRequiredKeyedService<TCodec>(ServiceProvider, entryFormatKey);
    }
}
