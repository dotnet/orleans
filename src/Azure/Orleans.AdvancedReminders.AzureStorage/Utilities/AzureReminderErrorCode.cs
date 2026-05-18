using System.Diagnostics.CodeAnalysis;

namespace Orleans.AzureUtils.Utilities
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    internal enum AzureReminderErrorCode
    {
        Runtime = 100000,
        AzureTableBase = Runtime + 800,

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

        AzureTable_ReadWrongReminder = AzureTableBase + 64
    }
}
