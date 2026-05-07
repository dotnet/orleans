namespace Orleans.Runtime;

/// <summary>
/// Acts as a cache for a message recipient.
/// </summary>
public interface IMessageReceiverCache
{
    /// <summary>
    /// Gets or sets a cached recipient which can be used to handle messages sent to the target represented by this instance.
    /// </summary>
    object? MessageReceiver { get; set; }
}
