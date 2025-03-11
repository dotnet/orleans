namespace Orleans.Persistence.Cosmos
{
    /// <summary>
    /// Provides ability to get or set a TTL value on the record.
    /// </summary>
    /// <remarks>
    /// Implement this interface on the grain state to provide
    /// the record TTL support. The backing value for the TTL
    /// should be a non-serializable property or a field.
    /// </remarks>
    public interface ITimeToLiveAware
    {
        /// <summary>
        /// Gets the record TTL in seconds.
        /// </summary>
        /// <returns>
        /// TTL in seconds. Valid values are <c>null</c>, <c>-1</c>, or a positive integer.
        /// </returns>
        int? GetTimeToLive();

        /// <summary>
        /// Sets the record TTL in seconds.
        /// </summary>
        /// <param name="value">
        /// TTL in seconds. Valid values are <c>null</c>, <c>-1</c>, or a positive integer.
        /// </param>
        void SetTimeToLive(int? value);
    }
}
