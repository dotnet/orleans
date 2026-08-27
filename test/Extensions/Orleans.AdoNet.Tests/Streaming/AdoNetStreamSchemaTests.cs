namespace Tester.AdoNet.Streaming;

[TestCategory("AdoNet"), TestCategory("Streaming"), TestCategory("BVT")]
[TestProvider("None")]
[TestSuite("BVT")]
[TestArea("Streaming")]
public sealed class AdoNetStreamSchemaTests
{
    [Theory]
    [InlineData("SQLServer")]
    [InlineData("PostgreSQL")]
    [InlineData("MySQL")]
    public void SchemaDefinesVersionedStreamPartitions(string provider)
    {
        var script = ReadScript(provider);

        Assert.Contains("CREATE TABLE OrleansStreamPartition", script, StringComparison.Ordinal);
        Assert.Contains("NextMessageId BIGINT NOT NULL", script, StringComparison.Ordinal);
        Assert.Contains("Checkpoint", script, StringComparison.Ordinal);
        Assert.Contains("OwnerEpoch BIGINT NOT NULL", script, StringComparison.Ordinal);
        Assert.Contains("CleanupOn", script, StringComparison.Ordinal);

        Assert.Contains("CREATE TABLE OrleansStreamMessage", script, StringComparison.Ordinal);
        Assert.Contains("StreamIdBytes", script, StringComparison.Ordinal);
        Assert.Contains("StreamNamespaceLength INT NOT NULL", script, StringComparison.Ordinal);
        Assert.Contains("CheckpointedOn", script, StringComparison.Ordinal);
        Assert.Contains("CheckpointedOn = COALESCE", script, StringComparison.Ordinal);
        Assert.Contains("CheckpointedOn IS NULL", script, StringComparison.Ordinal);
        Assert.Contains("CheckpointedOn <", script, StringComparison.Ordinal);
        Assert.Contains("CreatedOn <", script, StringComparison.Ordinal);
        Assert.Contains("Payload", script, StringComparison.Ordinal);

        Assert.Contains("'StreamSchemaVersionKey', '2'", script, StringComparison.Ordinal);
        Assert.Contains("'AppendStreamMessageKey'", script, StringComparison.Ordinal);
        Assert.Contains("'AcquireStreamPartitionKey'", script, StringComparison.Ordinal);
        Assert.Contains("'ReadStreamMessagesKey'", script, StringComparison.Ordinal);
        Assert.Contains("'AdvanceStreamCheckpointKey'", script, StringComparison.Ordinal);
        Assert.Contains("'GetStreamPartitionBoundsKey'", script, StringComparison.Ordinal);
        Assert.Contains("'CleanupStreamMessagesKey'", script, StringComparison.Ordinal);
        Assert.Contains("NextMessageId", GetStoredQuery(script, "GetStreamPartitionBoundsKey"), StringComparison.Ordinal);

        Assert.DoesNotContain("CREATE TABLE OrleansStreamDeadLetter", script, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE TABLE OrleansStreamControl", script, StringComparison.Ordinal);
        Assert.Contains("no in-place migration", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SqlServerAppendUsesUpdateOutputWhileHoldingPartitionLock()
    {
        var script = ReadScript("SQLServer");

        Assert.Contains("UPDATE OrleansStreamPartition WITH (UPDLOCK, ROWLOCK)", script, StringComparison.Ordinal);
        Assert.Contains("OUTPUT Inserted.NextMessageId - 1", script, StringComparison.Ordinal);
        Assert.Contains("@LockOwner = 'Transaction'", script, StringComparison.Ordinal);
        Assert.Contains("READPAST, READCOMMITTEDLOCK", script, StringComparison.Ordinal);
    }

    [Fact]
    public void PostgreSqlAppendUsesUpdateReturning()
    {
        var script = ReadScript("PostgreSQL");

        Assert.Contains("UPDATE OrleansStreamPartition AS P", script, StringComparison.Ordinal);
        Assert.Contains("RETURNING P.NextMessageId - 1 INTO _MessageId", script, StringComparison.Ordinal);
        Assert.Contains("FOR UPDATE", script, StringComparison.Ordinal);
    }

    [Fact]
    public void MySqlAppendUsesSelectForUpdateThenUpdate()
    {
        var script = ReadScript("MySQL");

        Assert.Contains("FOR UPDATE", script, StringComparison.Ordinal);
        Assert.Contains("NextMessageId = _MessageId + 1", script, StringComparison.Ordinal);
        Assert.Contains("IN _ManageTransaction BOOLEAN", script, StringComparison.Ordinal);
        Assert.Contains("@Payload, TRUE)", script, StringComparison.Ordinal);
        Assert.DoesNotContain("@@session.in_transaction", script, StringComparison.Ordinal);
    }

    [Fact]
    public void MySqlScriptDoesNotProduceWhitespaceOnlyBatches()
    {
        var batches = ReadScript("MySQL")
            .Replace("END$$", "END;", StringComparison.Ordinal)
            .Split(["DELIMITER $$", "DELIMITER ;"], StringSplitOptions.RemoveEmptyEntries);

        Assert.DoesNotContain(batches, string.IsNullOrWhiteSpace);
    }

    [Theory]
    [InlineData("SQLServer", "SELECT @LockedNextMessageId", "SET @Now = SYSUTCDATETIME()", "INSERT INTO OrleansStreamMessage")]
    [InlineData("PostgreSQL", "RETURNING P.NextMessageId - 1 INTO _MessageId", "_Now := clock_timestamp()", "INSERT INTO OrleansStreamMessage")]
    [InlineData("MySQL", "FOR UPDATE;", "SET _Now = UTC_TIMESTAMP(6)", "INSERT INTO OrleansStreamMessage")]
    public void AppendSamplesMessageTimestampAfterPartitionLock(
        string provider,
        string lockMarker,
        string timestampMarker,
        string messageInsertMarker)
    {
        var script = ReadScript(provider);

        AssertOrder(script, lockMarker, timestampMarker, messageInsertMarker);
    }

    [Theory]
    [InlineData("SQLServer", "SELECT @LockedCheckpoint", "SET @Now = SYSUTCDATETIME()", "SET CheckpointedOn")]
    [InlineData("PostgreSQL", "FOR UPDATE;", "_Now := clock_timestamp()", "SET CheckpointedOn")]
    [InlineData("MySQL", "FOR UPDATE;", "SET _Now = UTC_TIMESTAMP(6)", "SET CheckpointedOn")]
    public void CheckpointSamplesEligibilityTimestampAfterPartitionLock(
        string provider,
        string lockMarker,
        string timestampMarker,
        string eligibilityMarker)
    {
        var script = ReadScript(provider);
        var checkpointProcedure = script[script.IndexOf("AdvanceStreamCheckpoint", StringComparison.Ordinal)..];

        AssertOrder(checkpointProcedure, lockMarker, timestampMarker, eligibilityMarker);
    }

    private static string ReadScript(string provider) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, $"{provider}-Streaming.sql"));

    private static string GetStoredQuery(string script, string queryKey)
    {
        var start = script.IndexOf($"('{queryKey}'", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = script.IndexOfAny(['\r', '\n'], start);
        return script[start..end];
    }

    private static void AssertOrder(string text, params string[] markers)
    {
        var previous = -1;
        foreach (var marker in markers)
        {
            var current = text.IndexOf(marker, previous + 1, StringComparison.Ordinal);
            Assert.True(current > previous, $"Expected '{marker}' after index {previous}.");
            previous = current;
        }
    }
}
