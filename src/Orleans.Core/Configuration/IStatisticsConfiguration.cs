namespace Orleans.Runtime.Configuration
{
    /// <summary>
    /// The level of runtime statistics to collect and report periodically.
    /// The default level is Info.
    /// </summary>
    public enum StatisticsLevel
    {
        Critical,
        Info,
        Verbose,
        Verbose2,
        Verbose3,
    }
}
