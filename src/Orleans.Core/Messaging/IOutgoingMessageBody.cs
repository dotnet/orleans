namespace Orleans.Runtime;

/// <summary>
/// Transfers resources from an outgoing invocation to its message.
/// </summary>
/// <remarks>
/// The message releases the resources when sending completes, when retained bytes replace its body for retry,
/// or when it is discarded. Callback completion does not end this ownership.
/// Resource release leaves the invocation itself available to its callback for metadata.
/// </remarks>
internal interface IOutgoingMessageBody
{
    void OnMessageCreated();
    void ReleaseResources();
}
