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
    /// Gets the operation codec for <paramref name="operationFormatKey"/>.
    /// </summary>
    /// <typeparam name="TCodec">The operation codec service type.</typeparam>
    /// <param name="operationFormatKey">The journal format key for the operation being replayed.</param>
    /// <param name="writeOperationCodec">The operation codec for the configured write journal format.</param>
    /// <returns>The operation codec for <paramref name="operationFormatKey"/>.</returns>
    public TCodec GetRequiredOperationCodec<TCodec>(string operationFormatKey, TCodec writeOperationCodec)
        where TCodec : notnull
    {
        ArgumentNullException.ThrowIfNull(writeOperationCodec);
        operationFormatKey = JournalFormatServices.ValidateJournalFormatKey(operationFormatKey);

        if (string.Equals(operationFormatKey, WriteJournalFormatKey, StringComparison.Ordinal))
        {
            return writeOperationCodec;
        }

        return JournalFormatServices.GetRequiredKeyedService<TCodec>(ServiceProvider, operationFormatKey);
    }
}
