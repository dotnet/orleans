using System.Diagnostics.CodeAnalysis;

namespace Orleans.AzureUtils.Utilities
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    internal enum AzureQueueErrorCode
    {
        Runtime = 100000,
        AzureQueueBase = Runtime + 3200,

        AzureQueue_01 = AzureQueueBase + 1,
        AzureQueue_02 = AzureQueueBase + 2,
        AzureQueue_03 = AzureQueueBase + 3,
        AzureQueue_04 = AzureQueueBase + 4,
        AzureQueue_05 = AzureQueueBase + 5,
        AzureQueue_06 = AzureQueueBase + 6,
        AzureQueue_07 = AzureQueueBase + 7,
        AzureQueue_08 = AzureQueueBase + 8,
        AzureQueue_09 = AzureQueueBase + 9,
        AzureQueue_10 = AzureQueueBase + 10,
        AzureQueue_11 = AzureQueueBase + 11,
        AzureQueue_12 = AzureQueueBase + 12,
        AzureQueue_13 = AzureQueueBase + 13,
        AzureQueue_14 = AzureQueueBase + 14,
        AzureQueue_15 = AzureQueueBase + 15,
    }
}
