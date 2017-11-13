using System.Diagnostics.CodeAnalysis;

#if CLUSTERING_AZURESTORAGE
namespace Orleans.Clustering.AzureStorage.Utilities
#else
namespace Orleans.AzureUtils.Utilities
#endif
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    internal enum ErrorCode
    {
        Runtime = 100000,
        AzureTableBase = Runtime + 800,

        AzureTable_01 = AzureTableBase + 1,
        AzureTable_02 = AzureTableBase + 2,
        AzureTable_03 = AzureTableBase + 3,
        AzureTable_04 = AzureTableBase + 4,
        AzureTable_06 = AzureTableBase + 6,
        AzureTable_07 = AzureTableBase + 7,
        AzureTable_08 = AzureTableBase + 8,
        AzureTable_09 = AzureTableBase + 9,
        AzureTable_10 = AzureTableBase + 10,
        AzureTable_11 = AzureTableBase + 11,
        AzureTable_12 = AzureTableBase + 12,
        AzureTable_13 = AzureTableBase + 13,
        AzureTable_14 = AzureTableBase + 14,
        AzureTable_15 = AzureTableBase + 15,
        AzureTable_17 = AzureTableBase + 17,
        AzureTable_18 = AzureTableBase + 18,
        AzureTable_20 = AzureTableBase + 20,
        AzureTable_21 = AzureTableBase + 21,
        AzureTable_22 = AzureTableBase + 22,
        AzureTable_23 = AzureTableBase + 23,
        AzureTable_24 = AzureTableBase + 24,
        AzureTable_25 = AzureTableBase + 25,
        AzureTable_26 = AzureTableBase + 26,
        AzureTable_32 = AzureTableBase + 32,
        AzureTable_33 = AzureTableBase + 33,
        AzureTable_34 = AzureTableBase + 34,
        AzureTable_37 = AzureTableBase + 37,
        // reminders related
        AzureTable_38 = AzureTableBase + 38,
        AzureTable_39 = AzureTableBase + 39,
        AzureTable_40 = AzureTableBase + 40,
        AzureTable_42 = AzureTableBase + 42,
        AzureTable_43 = AzureTableBase + 43,
        AzureTable_44 = AzureTableBase + 44,
        AzureTable_45 = AzureTableBase + 45,
        AzureTable_46 = AzureTableBase + 46,
        AzureTable_47 = AzureTableBase + 47,
        AzureTable_49 = AzureTableBase + 49,
        // Azure storage provider related
        AzureTable_DataNotFound = AzureTableBase + 50,
        AzureTable_60 = AzureTableBase + 60,
        AzureTable_61 = AzureTableBase + 61,
        AzureTable_ReadWrongReminder = AzureTableBase + 64
    }
}
