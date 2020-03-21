using System.Diagnostics.CodeAnalysis;

namespace Orleans.Clustering.AzureStorage.Utilities
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    internal enum TableStorageErrorCode
    {
        Runtime = 100000,
        AzureTableBase = Runtime + 800,

        AzureTable_20 = AzureTableBase + 20,
        AzureTable_21 = AzureTableBase + 21,
        AzureTable_22 = AzureTableBase + 22,
        AzureTable_23 = AzureTableBase + 23,
        AzureTable_24 = AzureTableBase + 24,
        AzureTable_25 = AzureTableBase + 25,
        AzureTable_26 = AzureTableBase + 26,

        AzureTable_32 = AzureTableBase + 32,
        AzureTable_33 = AzureTableBase + 33,

        AzureTable_60 = AzureTableBase + 60,
        AzureTable_61 = AzureTableBase + 61,

        AzureTable_65 = AzureTableBase + 65,
        AzureTable_66 = AzureTableBase + 66,
    }
}
