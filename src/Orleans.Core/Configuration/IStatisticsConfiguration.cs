namespace Orleans.Runtime.Configuration
{
    /// <summary>
    /// The level of runtime statistics to collect and report periodically.
    /// The default level is Info.
    /// </summary>
    public enum StatisticsLevel
    {
        /// <summary>
        /// Critical statistics.
        /// </summary>
        Critical,

        /// <summary>
        /// Informational statistics.
        /// </summary>
        Info,

        /// <summary>
        /// Verbose statistics.
        /// </summary>
        Verbose,

        /// <summary>
        /// More verbose statistics.
        /// </summary>
        Verbose2,

        /// <summary>
        /// The most verbose statistics level.
        /// </summary>
        Verbose3,
    }
}
