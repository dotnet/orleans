using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Orleans.AzureUtils;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Providers.Streams.AzureQueue
{
    /// <summary>
    /// Persistent stream provider that uses azure queue for persistence
    /// </summary>
    public class SimpleAzureQueueStreamProvider : PersistentStreamProvider<SimpleAzureQueueAdapterFactory>
    {
    }
}
