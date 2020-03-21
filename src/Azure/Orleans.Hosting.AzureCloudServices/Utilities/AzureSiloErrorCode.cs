using System.Diagnostics.CodeAnalysis;

namespace Orleans.AzureUtils.Utilities
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    internal enum AzureSiloErrorCode
    {
        Runtime = 100000,
        AzureTableBase = Runtime + 800,

        AzureTable_34 = AzureTableBase + 34,
    }
}
