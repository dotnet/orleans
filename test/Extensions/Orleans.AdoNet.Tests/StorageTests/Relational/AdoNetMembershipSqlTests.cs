using System.Text.RegularExpressions;

namespace UnitTests.StorageTests.Relational;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Clustering")]
public sealed class AdoNetMembershipSqlTests
{
    public static TheoryData<string> Engines => new() { "SQLServer", "PostgreSQL", "MySQL", "Oracle" };

    [Theory]
    [MemberData(nameof(Engines))]
    public void Cleanup_UsesCapturedValuesWithoutChangingVersion(string engine)
    {
        var query = Queries(ReadScript(engine))["CleanupDefunctSiloEntryKey"];

        Assert.Equal(
            ["Address", "DeploymentId", "Generation", "IAmAliveTime", "Port", "StartTime", "SuspectTimes"],
            Parameters(query));
        Assert.StartsWith("DELETE FROM OrleansMembershipTable", query.Trim(), StringComparison.Ordinal);
        Assert.Contains("Status = 6", query, StringComparison.Ordinal);
        foreach (var column in new[] { "DeploymentId", "Address", "Port", "Generation", "StartTime", "IAmAliveTime" })
        {
            Assert.Matches($@"\b{column} = [@:]{column}\b", query);
        }

        Assert.Matches(@"SuspectTimes.*(?:IS NULL|COALESCE)", Regex.Replace(query, @"\s+", " "));
        Assert.DoesNotContain("Version", query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE", query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("2147483647", query, StringComparison.Ordinal);
        if (engine == "SQLServer")
        {
            Assert.Contains("CONVERT(VARCHAR(8000), COALESCE(@SuspectTimes, ''))", query, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void LegacyCleanup_PreservesParametersAndProtectsActivity(string engine)
    {
        var query = Queries(ReadScript(engine))["CleanupDefunctSiloEntriesKey"];

        Assert.Equal(["DeploymentId", "IAmAliveTime"], Parameters(query));
        Assert.Contains("Status = 6", query, StringComparison.Ordinal);
        Assert.Matches(@"IAmAliveTime < [@:]IAmAliveTime", query);
        Assert.Matches(@"StartTime < [@:]IAmAliveTime", query);
        Assert.Matches(@"SuspectTimes(?: IS NULL|, ''\) = '')", query);
        Assert.DoesNotContain("Version", query, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void MembershipReads_JoinRowsAndVersionInOneStatement(string engine)
    {
        var queries = Queries(ReadScript(engine));
        foreach (var key in new[] { "MembershipReadRowKey", "MembershipReadAllKey" })
        {
            var query = queries[key];
            Assert.Single(Regex.Matches(query, @"\bSELECT\b", RegexOptions.IgnoreCase));
            Assert.Contains("v.Version", query, StringComparison.Ordinal);
            Assert.Contains("LEFT OUTER JOIN OrleansMembershipTable m", query, StringComparison.Ordinal);
            Assert.Contains("v.DeploymentId = m.DeploymentId", query, StringComparison.Ordinal);
            if (engine == "SQLServer")
            {
                Assert.Equal(2, Regex.Matches(query, @"WITH\(HOLDLOCK\)").Count);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void Heartbeat_IsOneBlindColumnWriteByPrimaryKey(string engine)
    {
        var script = ReadScript(engine);
        var queries = Queries(script);
        var routines = Routines(script);
        var heartbeat = engine switch
        {
            "PostgreSQL" => routines["update_i_am_alive_time"],
            "Oracle" => routines["UpdateIAmAlivetime"],
            _ => queries["UpdateIAmAlivetimeKey"],
        };
        Assert.Single(Regex.Matches(heartbeat, @"\bUPDATE\b", RegexOptions.IgnoreCase));
        Assert.DoesNotMatch(@"(?i)\b(?:SELECT|JOIN|IF|CASE|GREATEST|Status|Version)\b", heartbeat);
        var statement = Regex.Match(heartbeat, @"UPDATE OrleansMembershipTable.*?;", RegexOptions.Singleline).Value;
        var expected = engine switch
        {
            "PostgreSQL" => "UPDATE OrleansMembershipTable as d SET IAmAliveTime = i_am_alive_time WHERE d.DeploymentId = deployment_id AND d.Address = address_arg AND d.Port = port_arg AND d.Generation = generation_arg;",
            "Oracle" => "UPDATE OrleansMembershipTable SET IAmAliveTime = PARAM_IAMALIVE WHERE DeploymentId = PARAM_DEPLOYMENTID AND Address = PARAM_ADDRESS AND Port = PARAM_PORT AND Generation = PARAM_GENERATION;",
            _ => "UPDATE OrleansMembershipTable SET IAmAliveTime = @IAmAliveTime WHERE DeploymentId = @DeploymentId AND Address = @Address AND Port = @Port AND Generation = @Generation;",
        };
        Assert.Equal(expected, Regex.Replace(statement, @"\s+", " "));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void FullMembershipUpdate_AssignsSuppliedBestEffortHeartbeat(string engine)
    {
        var script = ReadScript(engine);
        var update = UpdateBody(engine, Queries(script), Routines(script));
        var parameter = engine switch
        {
            "PostgreSQL" => "IAmAliveTimeArg",
            "Oracle" => "PARAM_IAMALIVETIME",
            _ => "@IAmAliveTime",
        };
        Assert.Contains($"IAmAliveTime = {parameter}", update, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"(?i)\b(?:CASE|GREATEST)\b", update);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void InsertMembership_AtomicallyChecksVersionAndRollsBackConflicts(string engine)
    {
        var script = ReadScript(engine);
        var queries = Queries(script);
        var routines = Routines(script);
        var insert = engine switch
        {
            "PostgreSQL" => routines["insert_membership"],
            "Oracle" => routines["InsertMembership"],
            "MySQL" => routines["InsertMembershipKey"],
            _ => queries["InsertMembershipKey"],
        };

        var versionWrite = insert.IndexOf("UPDATE OrleansMembershipVersionTable", StringComparison.Ordinal);
        var rowWrite = insert.IndexOf("INSERT INTO OrleansMembershipTable", StringComparison.Ordinal);
        if (engine is "SQLServer" or "MySQL")
        {
            Assert.True(rowWrite >= 0 && versionWrite > rowWrite, insert);
            Assert.Contains(engine == "SQLServer" ? "AND @@ROWCOUNT > 0;" : "AND ROW_COUNT() > 0;", insert[versionWrite..], StringComparison.Ordinal);
        }
        else
        {
            Assert.True(versionWrite >= 0 && rowWrite > versionWrite, insert);
            Assert.Matches(@"WHERE (?:_ROWCOUNT|RowCountVar|rowcount) > 0", insert);
        }

        Assert.Matches(@"Version = (?:@Version|_Version|VersionArg|PARAM_VERSION)", insert);
        Assert.Contains("Version < 2147483647", insert, StringComparison.Ordinal);
        AssertRollback(engine, insert);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void UpdateMembership_AtomicallyRejectsStaleVersionAndMissingRow(string engine)
    {
        var script = ReadScript(engine);
        var update = UpdateBody(engine, Queries(script), Routines(script));

        Assert.Matches(@"Version = (?:@Version|VersionArg|PARAM_VERSION)", update);
        Assert.Contains("Version < 2147483647", update, StringComparison.Ordinal);
        foreach (var column in new[] { "Address", "Port", "Generation" })
        {
            Assert.Matches($@"\b{column} = (?:@{column}|{column}Arg|PARAM_{column.ToUpperInvariant()})", update);
        }

        var rowConditions = update[update.LastIndexOf("WHERE", StringComparison.Ordinal)..];
        Assert.DoesNotContain("IAmAliveTime", rowConditions, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SuspectTimes", rowConditions, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("StartTime", rowConditions, StringComparison.OrdinalIgnoreCase);

        if (engine == "MySQL")
        {
            Assert.Single(Regex.Matches(update, @"\bUPDATE\b"));
            Assert.Contains("INNER JOIN OrleansMembershipTable m ON m.DeploymentId = v.DeploymentId", update, StringComparison.Ordinal);
            Assert.Contains("SET v.Version = v.Version + 1,", update, StringComparison.Ordinal);
            Assert.Contains("SELECT ROW_COUNT() > 0;", update, StringComparison.Ordinal);
        }
        else
        {
            var rowWrite = update.IndexOf("UPDATE OrleansMembershipTable", StringComparison.Ordinal);
            Assert.True(rowWrite > update.IndexOf("UPDATE OrleansMembershipVersionTable", StringComparison.Ordinal), update);
            AssertRollback(engine, update[rowWrite..]);
        }
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void CompatibleUpdate_MatchesFreshInstallQueriesAndRoutines(string engine)
    {
        var install = ReadScript(engine);
        var update = ReadScript(engine, update: true);
        var installQueries = Queries(install);
        var updatedQueries = Queries(update);
        foreach (Match match in Regex.Matches(update, @"UPDATE OrleansQuery SET QueryText = '(?<text>(?:''|[^'])*)'\s*WHERE QueryKey = '(?<key>\w+)';"))
        {
            updatedQueries.Add(match.Groups["key"].Value, match.Groups["text"].Value.Replace("''", "'"));
        }

        Assert.Equal(engine switch { "SQLServer" => 7, "MySQL" => 5, _ => 2 }, updatedQueries.Count);
        foreach (var (key, text) in updatedQueries)
        {
            Assert.Equal(installQueries[key], text.Replace("InsertMembershipKeyAtomic(", "InsertMembershipKey("));
        }

        var installRoutines = Routines(install);
        var updatedRoutines = Routines(update);
        Assert.Equal(engine switch { "PostgreSQL" => 3, "Oracle" => 3, "MySQL" => 1, _ => 0 }, updatedRoutines.Count);
        foreach (var (name, text) in updatedRoutines)
        {
            var installName = name == "InsertMembershipKeyAtomic" ? "InsertMembershipKey" : name;
            Assert.Equal(installRoutines[installName], text.Replace("InsertMembershipKeyAtomic(", "InsertMembershipKey("));
        }

        Assert.DoesNotMatch(@"(?i)\b(?:CREATE|ALTER|DROP)\s+TABLE\b", update);
        Assert.DoesNotMatch(@"(?i)(?:FUNCTION|PROCEDURE)\s+Cleanup", update);
        Assert.Equal("DELETE", updatedQueries["CleanupDefunctSiloEntryKey"].Trim().Split(' ')[0]);
    }

    [Fact]
    public void MySqlUpgrade_PreservesExistingRoutineAndPublishesQueriesAfterAdditiveCreation()
    {
        var update = ReadScript("MySQL", update: true);

        Assert.DoesNotMatch(@"(?i)\b(?:DROP|ALTER)\s+PROCEDURE\b", update);
        Assert.DoesNotMatch(@"(?i)\bCREATE\s+(?:OR REPLACE\s+)?PROCEDURE\s+InsertMembershipKey\s*\(", update);
        Assert.Equal("InsertMembershipKeyAtomic", Assert.Single(Routines(update)).Key);
        var create = update.IndexOf("CREATE PROCEDURE InsertMembershipKeyAtomic(", StringComparison.Ordinal);
        var publication = update.IndexOf("START TRANSACTION;", update.IndexOf("DELIMITER ;", StringComparison.Ordinal), StringComparison.Ordinal);
        var route = update.IndexOf("call InsertMembershipKeyAtomic(", StringComparison.Ordinal);
        Assert.True(create >= 0 && publication > create && route > publication, update);
        Assert.EndsWith("COMMIT;", update.Trim(), StringComparison.Ordinal);
    }

    private static void AssertRollback(string engine, string body)
    {
        if (engine == "PostgreSQL")
        {
            Assert.DoesNotMatch(@"\bASSERT\b", body);
            Assert.Contains("IF RowCountVar = 0 THEN", body, StringComparison.Ordinal);
            Assert.Contains("RAISE EXCEPTION 'no rows affected, rollback' USING ERRCODE = 'assert_failure';", body, StringComparison.Ordinal);
            Assert.Contains("WHEN assert_failure THEN", body, StringComparison.Ordinal);
        }
        else
        {
            Assert.Matches(@"(?i)IF (?:@ROWCOUNT|_ROWCOUNT|rowcount) = 0\s*(?:THEN\s*)?ROLLBACK", body);
            Assert.Matches(@"(?i)ELSE\s*COMMIT", body);
            if (engine == "MySQL")
            {
                Assert.Contains("DECLARE EXIT HANDLER FOR SQLEXCEPTION", body, StringComparison.Ordinal);
                Assert.Contains("RESIGNAL;", body, StringComparison.Ordinal);
            }
        }
    }

    private static string UpdateBody(string engine, Dictionary<string, string> queries, Dictionary<string, string> routines) => engine switch
    {
        "PostgreSQL" => routines["update_membership"],
        "Oracle" => routines["UpdateMembership"],
        _ => queries["UpdateMembershipKey"],
    };

    private static string ReadScript(string engine, bool update = false) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, $"{engine}-Clustering{(update ? "-AtomicWrites" : "")}.sql")).Replace("\r\n", "\n");

    private static string[] Parameters(string query) =>
        Regex.Matches(query, @"[@:](\w+)").Select(match => match.Groups[1].Value).Distinct().Order(StringComparer.Ordinal).ToArray();

    private static Dictionary<string, string> Queries(string script) =>
        Regex.Matches(script, @"'(?<key>\w+Key)'\s*,\s*'(?<text>(?:''|[^'])*)'")
            .ToDictionary(match => match.Groups["key"].Value, match => match.Groups["text"].Value.Replace("''", "'"));

    private static Dictionary<string, string> Routines(string script) =>
        Regex.Matches(script, @"CREATE (?:OR REPLACE )?(?:FUNCTION|PROCEDURE) (?<name>\w+)\(.*?(?:\$func\$ LANGUAGE plpgsql;|END\$\$|END;\n/)", RegexOptions.Singleline)
            .ToDictionary(match => match.Groups["name"].Value, match => match.Value.Replace("CREATE OR REPLACE FUNCTION", "CREATE FUNCTION"));
}
