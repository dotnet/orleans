using System;

namespace Orleans.Runtime.Host
{
    internal sealed class AzureConstants
    {
        public static readonly TimeSpan STARTUP_TIME_PAUSE = TimeSpan.FromSeconds(5); // In seconds
        public const int MAX_RETRIES = 120;  // 120 x 5s = Total: 10 minutes
        public const string DataConnectionConfigurationSettingName = "DataConnectionString";
        public const string SiloEndpointConfigurationKeyName = "OrleansSiloEndpoint";
        public const string ProxyEndpointConfigurationKeyName = "OrleansProxyEndpoint";
    }
}
