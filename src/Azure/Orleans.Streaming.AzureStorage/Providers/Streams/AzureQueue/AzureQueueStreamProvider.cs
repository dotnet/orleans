using System;
using Orleans.Providers.Streams.Common;

namespace Orleans.Providers.Streams.AzureQueue
{
    /// <summary>
    /// Persistent stream provider that uses azure queue for persistence
    /// WARNING: This version is maintained for compatability purposes.  New services should use AzureQueueStreamProviderV2 as it supports external serializers.
    /// </summary>
    [Obsolete("This version is maintained for compatability purposes. New services should use AzureQueueStreamProviderV2 as it supports external serializers.")]
    public class AzureQueueStreamProvider : PersistentStreamProvider<AzureQueueAdapterFactory<AzureQueueDataAdapterV1>>
    {
    }

    /// <summary>
    /// Persistent stream provider that uses azure queue for persistence
    /// </summary>
    public class AzureQueueStreamProviderV2 : PersistentStreamProvider<AzureQueueAdapterFactory<AzureQueueDataAdapterV2>>
    {
    }
}
