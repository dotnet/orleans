namespace Orleans.Runtime
{
    /// <summary>
    /// Functionality to facilitate caching the recipient of a message for performance.
    /// </summary>
    public interface ICachedMessageReceiver
    {
        /// <summary>
        /// Sends a message to this instance.
        /// </summary>
        /// <returns>
        /// <see langword="true"/> if this handler remains valid; otherwise <see langword="false"/> if this handler has become invalid and should be replaced.
        /// </returns>
        bool HandleMessage(object message);
    }
}
