namespace Orleans.Persistence.Migration
{
    public enum MigrationErrorCodes
    {
        Runtime = 100000,
        MigrationBase = Runtime + 10000,

        MigrationMetadataNotWritten = MigrationBase + 1,
    }
}
