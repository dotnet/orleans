using System;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Orleans.ServiceBus
{
    /// <summary>
    /// Orleans ServiceBus error codes
    /// </summary>
    internal enum OrleansServiceBusErrorCode
    {
        /// <summary>
        /// Start of orlean servicebus error codes
        /// </summary>
        ServiceBus = 1<<16,

        FailedPartitionRead = ServiceBus + 1,
        RetryReceiverInit   = ServiceBus + 2,
    }
}
