using System.Text.RegularExpressions;
using Xunit;

namespace UnitTests.AdvancedRemindersTest;

[TestCategory("Reminders"), TestCategory("AdoNet")]
public sealed class AdvancedReminderSqlScriptTests
{
    [Theory]
    [InlineData("MySQL")]
    [InlineData("Oracle")]
    [InlineData("PostgreSQL")]
    [InlineData("SQLServer")]
    public void BuildOutput_ContainsDistinctClassicAndAdvancedReminderScripts(string database)
    {
        var classicPath = Path.Combine(AppContext.BaseDirectory, $"{database}-Reminders.sql");
        var advancedPath = Path.Combine(AppContext.BaseDirectory, $"{database}-Reminders-Advanced.sql");

        Assert.True(File.Exists(classicPath), $"Missing classic reminder schema: {classicPath}");
        Assert.True(File.Exists(advancedPath), $"Missing advanced reminder schema: {advancedPath}");
        Assert.Contains("OrleansRemindersTable", File.ReadAllText(classicPath), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OrleansAdvancedRemindersTable", File.ReadAllText(advancedPath), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OracleAdvancedScript_UsesOneConsistentTableName()
    {
        var script = ReadAdvancedScript("Oracle");
        var tableNames = Regex.Matches(
                script,
                @"\bORLEANS(?:ADVANCED)?REMINDERSTABLE\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Select(match => match.Value.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["ORLEANSADVANCEDREMINDERSTABLE"], tableNames);
        Assert.Contains("PARAM_VERSION", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WHERE Version IS NOT NULL", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClassicAndAdvancedSchemas_UseDistinctDatabaseObjectNames()
    {
        var classicSqlServer = ReadClassicScript("SQLServer");
        var advancedSqlServer = ReadAdvancedScript("SQLServer");
        Assert.Contains("PK_RemindersTable_ServiceId_GrainId_ReminderName", classicSqlServer, StringComparison.Ordinal);
        Assert.Contains("PK_AdvancedReminders_ServiceId_GrainId_ReminderName", advancedSqlServer, StringComparison.Ordinal);
        Assert.Contains("WITH(UPDLOCK, HOLDLOCK)", advancedSqlServer, StringComparison.OrdinalIgnoreCase);

        var classicPostgreSql = ReadClassicScript("PostgreSQL");
        var advancedPostgreSql = ReadAdvancedScript("PostgreSQL");
        Assert.Contains("upsert_reminder_row", classicPostgreSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("upsert_advanced_reminder_row", advancedPostgreSql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("MySQL")]
    [InlineData("Oracle")]
    [InlineData("PostgreSQL")]
    [InlineData("SQLServer")]
    public void AdvancedUpsertScript_ContainsCompareExchangeVersionGate(string database)
    {
        var script = ReadAdvancedScript(database);

        Assert.Contains("AdvancedRemindersUpsertReminderRowKey", script, StringComparison.Ordinal);
        Assert.Contains("Version", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(database == "Oracle" ? "PARAM_VERSION" : database == "PostgreSQL" ? "ExpectedVersionArg" : "@Version", script, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadClassicScript(string database)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, $"{database}-Reminders.sql"));

    private static string ReadAdvancedScript(string database)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, $"{database}-Reminders-Advanced.sql"));
}
