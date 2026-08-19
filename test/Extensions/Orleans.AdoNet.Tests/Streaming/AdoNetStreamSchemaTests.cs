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
        Assert.Contains("Payload", script, StringComparison.Ordinal);

        Assert.Contains("'StreamSchemaVersionKey', '2'", script, StringComparison.Ordinal);
        Assert.Contains("'AppendStreamMessageKey'", script, StringComparison.Ordinal);
        Assert.Contains("'AcquireStreamPartitionKey'", script, StringComparison.Ordinal);
        Assert.Contains("'ReadStreamMessagesKey'", script, StringComparison.Ordinal);
        Assert.Contains("'AdvanceStreamCheckpointKey'", script, StringComparison.Ordinal);
        Assert.Contains("'GetStreamPartitionBoundsKey'", script, StringComparison.Ordinal);
        Assert.Contains("'CleanupStreamMessagesKey'", script, StringComparison.Ordinal);

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

    private static string ReadScript(string provider) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, $"{provider}-Streaming.sql"));
}
