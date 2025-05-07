using System.Diagnostics.CodeAnalysis;

namespace Orleans.Persistence.Migration
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    internal enum MigrationErrorCode
    {
        Runtime = 400000,

        GrainTypeResolveError = Runtime + 1,
    }
}
