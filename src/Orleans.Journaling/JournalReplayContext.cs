namespace Orleans.Journaling;

/// <summary>
/// Provides services used by journaled states during replay.
/// </summary>
public readonly struct JournalReplayContext
{
    internal JournalReplayContext(JournaledStateManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        Manager = manager;
    }

    private JournaledStateManager Manager =>
        field ?? throw new InvalidOperationException("The journal replay context is not initialized.");

    /// <summary>
    /// Gets the configured write journal format key.
    /// </summary>
    public string WriteJournalFormatKey => Manager.WriteJournalFormatKey;

    /// <summary>
    /// Gets the owning manager's service provider used to resolve journal services.
    /// </summary>
    /// <remarks>
    /// For activation-owned managers, this is the activation's service provider.
    /// Factory-created standalone managers have their own service scope.
    /// </remarks>
    public IServiceProvider ServiceProvider => Manager.ServiceProvider;

    /// <summary>
    /// Resolves the state machine for <paramref name="streamId"/>.
    /// </summary>
    /// <param name="streamId">The persisted journal stream id.</param>
    /// <returns>The state machine for the stream.</returns>
    public IStateMachine ResolveStateMachine(JournalStreamId streamId) => Manager.ResolveStateMachine(streamId);

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
