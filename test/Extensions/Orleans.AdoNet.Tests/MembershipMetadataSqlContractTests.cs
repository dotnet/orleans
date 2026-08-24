namespace Tester.AdoNet;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Clustering")]
public class MembershipMetadataSqlContractTests
{
    private static readonly string[] LegacyQueryKeys =
    [
        "InsertMembershipKey",
        "UpdateMembershipKey",
        "MembershipReadRowKey",
        "MembershipReadAllKey"
    ];

    private static readonly string[] V2QueryKeys =
    [
        "InsertMembershipV2Key",
        "UpdateMembershipV2Key",
        "MembershipReadRowV2Key",
        "MembershipReadAllV2Key"
    ];

    [Theory]
    [InlineData("MySQL")]
    [InlineData("Oracle")]
    [InlineData("PostgreSQL")]
    [InlineData("SQLServer")]
    public void FreshSetup_PreservesLegacyQueriesAndAddsCompleteV2Bundle(string provider)
    {
        var script = ReadScript($"{provider}-Clustering.sql");

        foreach (var legacyKey in LegacyQueryKeys)
        {
            Assert.DoesNotContain("MetadataJson", GetLegacyQueryDefinition(script, legacyKey), StringComparison.OrdinalIgnoreCase);
        }

        foreach (var v2Key in V2QueryKeys)
        {
            Assert.Contains($"'{v2Key}'", script, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("MySQL", "CREATE PROCEDURE InsertMembershipKey(", "\nBEGIN", "CREATE PROCEDURE InsertMembershipV2Key(")]
    [InlineData("Oracle", "CREATE OR REPLACE FUNCTION InsertMembership(", "\n  RETURN", "CREATE OR REPLACE FUNCTION InsertMembershipV2(")]
    [InlineData("PostgreSQL", "CREATE FUNCTION insert_membership(", "\n  RETURNS", "CREATE FUNCTION insert_membership_v2(")]
    public void FreshSetup_PreservesLegacyInsertRoutineSignatureAndAddsV2(
        string provider,
        string legacyRoutine,
        string signatureTerminator,
        string v2Routine)
    {
        var script = ReadScript($"{provider}-Clustering.sql");
        var signature = GetRoutineSignature(script, legacyRoutine, signatureTerminator);

        Assert.DoesNotContain("MetadataJson", signature, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(v2Routine, script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Oracle", "CREATE OR REPLACE FUNCTION UpdateMembership(", "\n  RETURN", "CREATE OR REPLACE FUNCTION UpdateMembershipV2(")]
    [InlineData("PostgreSQL", "CREATE FUNCTION update_membership(", "\n  RETURNS", "CREATE FUNCTION update_membership_v2(")]
    public void FreshSetup_PreservesLegacyUpdateRoutineSignatureAndAddsV2(
        string provider,
        string legacyRoutine,
        string signatureTerminator,
        string v2Routine)
    {
        var script = ReadScript($"{provider}-Clustering.sql");
        var signature = GetRoutineSignature(script, legacyRoutine, signatureTerminator);

        Assert.DoesNotContain("MetadataJson", signature, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(v2Routine, script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("MySQL")]
    [InlineData("Oracle")]
    [InlineData("PostgreSQL")]
    [InlineData("SQLServer")]
    public void MetadataMigration_OnlyUpsertsCompleteV2QueryBundle(string provider)
    {
        var script = ReadScript($"{provider}-Clustering-Metadata.sql");

        foreach (var legacyKey in LegacyQueryKeys)
        {
            Assert.DoesNotContain($"'{legacyKey}'", script, StringComparison.Ordinal);
        }

        foreach (var v2Key in V2QueryKeys)
        {
            Assert.Contains($"'{v2Key}'", script, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("MySQL")]
    [InlineData("Oracle")]
    [InlineData("PostgreSQL")]
    [InlineData("SQLServer")]
    public void RollingUpgrade_OldClientQueriesRemainMetadataFreeAfterMigration(string provider)
    {
        var setup = ReadScript($"{provider}-Clustering.sql");
        var migration = ReadScript($"{provider}-Clustering-Metadata.sql");

        Assert.DoesNotContain("MetadataJson", GetLegacyQueryDefinition(setup, "InsertMembershipKey"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MetadataJson", GetLegacyQueryDefinition(setup, "UpdateMembershipKey"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("'InsertMembershipKey'", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("'UpdateMembershipKey'", migration, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("MySQL", "ON DUPLICATE KEY UPDATE")]
    [InlineData("Oracle", "MERGE INTO OrleansQuery")]
    [InlineData("PostgreSQL", "ON CONFLICT (QueryKey) DO UPDATE")]
    [InlineData("SQLServer", "IF @@ROWCOUNT = 0")]
    public void MetadataMigration_UpsertsV2QueriesIdempotently(string provider, string upsertMarker)
    {
        var script = ReadScript($"{provider}-Clustering-Metadata.sql");

        Assert.Equal(4, CountOccurrences(script, upsertMarker));
    }

    [Theory]
    [InlineData("MySQL", "InsertMembershipKey", "CREATE PROCEDURE InsertMembershipKey(", "InsertMembershipV2Key")]
    [InlineData("Oracle", "InsertMembership", "CREATE OR REPLACE FUNCTION InsertMembership(", "InsertMembershipV2(")]
    [InlineData("PostgreSQL", "insert_membership", "CREATE OR REPLACE FUNCTION insert_membership(", "insert_membership_v2(")]
    public void MetadataMigration_DoesNotReplaceLegacyInsertRoutine(
        string provider,
        string legacyRoutine,
        string legacyRoutineDeclaration,
        string v2Routine)
    {
        var script = ReadScript($"{provider}-Clustering-Metadata.sql");

        Assert.DoesNotContain(legacyRoutineDeclaration, script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain($"DROP PROCEDURE IF EXISTS {legacyRoutine}", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain($"DROP FUNCTION IF EXISTS {legacyRoutine}", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(v2Routine, script, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Oracle", "UpdateMembership", "CREATE OR REPLACE FUNCTION UpdateMembership(", "UpdateMembershipV2(")]
    [InlineData("PostgreSQL", "update_membership", "CREATE OR REPLACE FUNCTION update_membership(", "update_membership_v2(")]
    public void MetadataMigration_DoesNotReplaceLegacyUpdateRoutine(
        string provider,
        string legacyRoutine,
        string legacyRoutineDeclaration,
        string v2Routine)
    {
        var script = ReadScript($"{provider}-Clustering-Metadata.sql");

        Assert.DoesNotContain(legacyRoutineDeclaration, script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain($"DROP PROCEDURE IF EXISTS {legacyRoutine}", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain($"DROP FUNCTION IF EXISTS {legacyRoutine}", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(v2Routine, script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MySqlContracts_UseLongTextForMetadata()
    {
        var setup = ReadScript("MySQL-Clustering.sql");
        var migration = ReadScript("MySQL-Clustering-Metadata.sql");

        Assert.Contains("MetadataJson LONGTEXT NULL", setup, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("_MetadataJson LONGTEXT", setup, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MODIFY COLUMN MetadataJson LONGTEXT NULL", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("_MetadataJson LONGTEXT", migration, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MySqlMigration_GuardsColumnCreationUsingInformationSchema()
    {
        var migration = ReadScript("MySQL-Clustering-Metadata.sql");

        Assert.Contains("INFORMATION_SCHEMA.COLUMNS", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CREATE PROCEDURE EnsureMembershipMetadataColumn()", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CALL EnsureMembershipMetadataColumn()", migration, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ADD COLUMN IF NOT EXISTS", migration, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@metadata_column", migration, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MySqlScriptSplitter_DiscardsWhitespaceOnlyBatches()
    {
        var storage = new UnitTests.General.MySqlStorageForTesting("Server=localhost;Database=test");
        var batches = storage.SplitScript("SELECT 1;\nDELIMITER ;\n\nDELIMITER $$\nSELECT 2;").ToArray();

        Assert.Equal(2, batches.Length);
        Assert.All(batches, batch => Assert.False(string.IsNullOrWhiteSpace(batch)));
    }

    [Fact]
    public void SqlServerFreshSetup_UpgradesExistingMembershipTableBeforeAddingV2Queries()
    {
        var setup = ReadScript("SQLServer-Clustering.sql");
        var columnGuard = setup.IndexOf("COL_LENGTH(N'OrleansMembershipTable', N'MetadataJson')", StringComparison.Ordinal);
        var firstV2Query = setup.IndexOf("'InsertMembershipV2Key'", StringComparison.Ordinal);

        Assert.True(columnGuard >= 0);
        Assert.True(firstV2Query > columnGuard);
    }

    [Fact]
    public void OracleMigration_ResumesInterruptedMetadataColumnConversion()
    {
        var migration = ReadScript("Oracle-Clustering-Metadata.sql");

        Assert.Contains("COLUMN_NAME = 'METADATAJSONV2'", migration, StringComparison.Ordinal);
        Assert.Contains("column_count = 0 AND temporary_column_count = 1", migration, StringComparison.Ordinal);
        Assert.Contains("RENAME COLUMN MetadataJsonV2 TO MetadataJson", migration, StringComparison.Ordinal);
    }

    private static string ReadScript(string fileName)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, fileName));

    private static string GetLegacyQueryDefinition(string script, string queryKey)
    {
        var queryStart = script.IndexOf($"'{queryKey}'", StringComparison.Ordinal);
        Assert.True(queryStart >= 0, $"Query key '{queryKey}' was not found.");

        var v2Key = queryKey.Replace("Key", "V2Key", StringComparison.Ordinal);
        if (queryKey.StartsWith("MembershipRead", StringComparison.Ordinal))
        {
            v2Key = queryKey.Replace("Key", "V2Key", StringComparison.Ordinal);
        }

        var queryEnd = script.IndexOf($"'{v2Key}'", queryStart + queryKey.Length, StringComparison.Ordinal);
        Assert.True(queryEnd > queryStart, $"V2 query key '{v2Key}' did not follow '{queryKey}'.");
        return script[queryStart..queryEnd];
    }

    private static string GetRoutineSignature(string script, string routineDeclaration, string signatureTerminator)
    {
        var signatureStart = script.IndexOf(routineDeclaration, StringComparison.OrdinalIgnoreCase);
        Assert.True(signatureStart >= 0, $"Routine '{routineDeclaration}' was not found.");

        var signatureEnd = script.IndexOf(signatureTerminator, signatureStart, StringComparison.Ordinal);
        Assert.True(signatureEnd > signatureStart);
        return script[signatureStart..signatureEnd];
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(search, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += search.Length;
        }

        return count;
    }
}
