namespace Orleans.Streaming.AdoNet;

/// <summary>
/// The model that represents a message that was successfully confirmed.
/// The RDMS implementation is responsible for mapping this message into its own schema.
/// </summary>
internal record AdoNetStreamConfirmation(
    string ServiceId,
    string ProviderId,
    int QueueId,
    int MessageId);