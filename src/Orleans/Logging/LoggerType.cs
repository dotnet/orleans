namespace Orleans.Runtime
{
    /// <summary>
    /// The ILogConsumer distinguishes between four categories of logs:
    /// <list type="table"><listheader><term>Value</term><description>Description</description></listheader>
    /// <item>
    /// <term>Runtime</term>
    /// <description>Logs that are written by the Orleans run-time itself.
    /// This category should not be used by application code.</description>
    /// </item>
    /// <item>
    /// <term>Grain</term>
    /// <description>Logs that are written by application grains.
    /// This category should be used by code that runs as Orleans grains in a silo.</description>
    /// </item>
    /// <item>
    /// <term>Application</term>
    /// <description>Logs that are written by the client application.
    /// This category should be used by client-side application code.</description>
    /// </item>
    /// <item>
    /// <term>Provider</term>
    /// <description>Logs that are written by providers.
    /// This category should be used by provider code.</description>
    /// </item>
    /// </list>
    /// </summary>
    public enum LoggerType
    {
        Runtime,
        Grain,
        Application,
        Provider
    }
}