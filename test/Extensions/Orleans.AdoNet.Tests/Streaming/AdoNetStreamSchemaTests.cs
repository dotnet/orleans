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

        Assert.Contains("'StreamSchemaVersionKey', '3'", script, StringComparison.Ordinal);
        Assert.Contains("'AppendStreamMessageKey'", script, StringComparison.Ordinal);
        Assert.Contains("'AcquireStreamPartitionKey'", script, StringComparison.Ordinal);
        Assert.Contains("'ReadStreamMessagesKey'", script, StringComparison.Ordinal);
        Assert.Contains("'AdvanceStreamCheckpointKey'", script, StringComparison.Ordinal);
        Assert.Contains("'GetStreamPartitionBoundsKey'", script, StringComparison.Ordinal);
        Assert.Contains("'CleanupStreamMessagesKey'", script, StringComparison.Ordinal);
        Assert.Contains("'AcquireStreamReplayLeaseKey'", script, StringComparison.Ordinal);
        Assert.Contains("'ReadStreamReplayMessagesKey'", script, StringComparison.Ordinal);
        Assert.Contains("'UpdateStreamReplayLeaseKey'", script, StringComparison.Ordinal);
        Assert.Contains("'ReleaseStreamReplayLeaseKey'", script, StringComparison.Ordinal);
        Assert.Contains("NextMessageId", GetStoredQuery(script, "GetStreamPartitionBoundsKey"), StringComparison.Ordinal);

        Assert.DoesNotContain("CREATE TABLE OrleansStreamDeadLetter", script, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE TABLE OrleansStreamControl", script, StringComparison.Ordinal);
        Assert.Contains("no in-place migration", script, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("SQLServer", "ReaderId NVARCHAR(150) NOT NULL", "StreamIdBytes VARBINARY(MAX) NOT NULL")]
    [InlineData("PostgreSQL", "ReaderId VARCHAR(150) NOT NULL", "StreamIdBytes BYTEA NOT NULL")]
    [InlineData("MySQL", "ReaderId VARCHAR(150) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL", "StreamIdBytes LONGBLOB NOT NULL")]
    public void SchemaDefinesReplayLeaseTableAndActiveLookupIndex(
        string provider,
        string readerIdDeclaration,
        string streamIdDeclaration)
    {
        var script = ReadScript(provider);
        var table = GetProcedure(script, "CREATE TABLE OrleansStreamReplayLease", "IX_OrleansStreamReplayLease_Active");

        Assert.Contains(readerIdDeclaration, table, StringComparison.Ordinal);
        Assert.Contains(streamIdDeclaration, table, StringComparison.Ordinal);
        Assert.Contains("StreamNamespaceLength INT NOT NULL", table, StringComparison.Ordinal);
        Assert.Contains("OwnerEpoch BIGINT NOT NULL", table, StringComparison.Ordinal);
        Assert.Contains("Watermark BIGINT NOT NULL", table, StringComparison.Ordinal);
        Assert.Contains("ExpiresOn", table, StringComparison.Ordinal);
        Assert.Contains("CreatedOn", table, StringComparison.Ordinal);
        Assert.Contains("ModifiedOn", table, StringComparison.Ordinal);
        AssertOrder(table, "ServiceId", "ProviderId", "QueueId", "ReaderId");

        Assert.Contains(
            "ServiceId, ProviderId, QueueId, ExpiresOn, Watermark",
            script[script.IndexOf("IX_OrleansStreamReplayLease_Active", StringComparison.Ordinal)..],
            StringComparison.Ordinal);
        Assert.Contains("OrleansStreamReplayLease", script[..script.IndexOf("CREATE TABLE OrleansStreamPartition", StringComparison.Ordinal)], StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("SQLServer", "@")]
    [InlineData("PostgreSQL", "_")]
    [InlineData("MySQL", "_")]
    public void ReplayRoutinesExposeRequiredParametersStatusesAndResults(string provider, string parameterPrefix)
    {
        var script = ReadScript(provider);
        var acquire = GetProcedure(script, "AcquireStreamReplayLease", "ReadStreamReplayMessages");
        var read = GetProcedure(script, "ReadStreamReplayMessages", "UpdateStreamReplayLease");
        var update = GetProcedure(script, "UpdateStreamReplayLease", "ReleaseStreamReplayLease");
        var release = GetProcedure(script, "ReleaseStreamReplayLease", "CleanupStreamMessages");

        AssertParameters(
            acquire,
            parameterPrefix,
            "ServiceId", "ProviderId", "QueueId", "ReaderId", "StreamIdBytes",
            "StreamNamespaceLength", "OwnerEpoch", "AfterMessageId", "ReplayLeaseDurationSeconds");
        AssertParameters(
            read,
            parameterPrefix,
            "ServiceId", "ProviderId", "QueueId", "ReaderId", "OwnerEpoch",
            "AfterMessageId", "MaxCount", "ReplayLeaseDurationSeconds");
        AssertParameters(
            update,
            parameterPrefix,
            "ServiceId", "ProviderId", "QueueId", "ReaderId", "OwnerEpoch",
            "Watermark", "ReplayLeaseDurationSeconds");
        AssertParameters(release, parameterPrefix, "ServiceId", "ProviderId", "QueueId", "ReaderId", "OwnerEpoch");

        Assert.Contains("'Acquired'", acquire, StringComparison.Ordinal);
        Assert.Contains("'Active'", read, StringComparison.Ordinal);
        Assert.Contains("'Active'", update, StringComparison.Ordinal);
        Assert.Contains("'Released'", release, StringComparison.Ordinal);
        Assert.All([acquire, read, update, release], routine => Assert.Contains("'OwnershipLost'", routine, StringComparison.Ordinal));
        Assert.All([acquire, read, update], routine => Assert.Contains("'HistoryUnavailable'", routine, StringComparison.Ordinal));
        Assert.All([read, update], routine => Assert.Contains("'Expired'", routine, StringComparison.Ordinal));

        foreach (var column in new[]
        {
            "Status", "OwnerEpoch", "Watermark", "ExpiresOn", "NextMessageId",
            "Checkpoint", "EarliestMessageId", "TailMessageId"
        })
        {
            Assert.All([acquire, read, update, release], routine => Assert.Contains(column, routine, StringComparison.Ordinal));
        }

        AssertParameters(acquire, string.Empty, "ServiceId", "ProviderId", "QueueId", "ReaderId");
        AssertParameters(read, string.Empty, "MessageId", "StreamIdBytes", "StreamNamespaceLength", "CreatedOn", "Payload");
    }

    [Theory]
    [InlineData("SQLServer", "FROM OrleansStreamPartition WITH", "FROM OrleansStreamReplayLease WITH", "FROM OrleansStreamMessage WITH")]
    [InlineData("PostgreSQL", "FROM OrleansStreamPartition AS P", "FROM OrleansStreamReplayLease AS L", "FROM OrleansStreamMessage AS M")]
    [InlineData("MySQL", "FROM OrleansStreamPartition", "FROM OrleansStreamReplayLease", "FROM OrleansStreamMessage")]
    public void ReplayRoutinesLockPartitionThenLeaseThenMessages(
        string provider,
        string partitionLock,
        string leaseLock,
        string messageLock)
    {
        var script = ReadScript(provider);

        AssertOrder(GetProcedure(script, "AcquireStreamReplayLease", "ReadStreamReplayMessages"), partitionLock, leaseLock, messageLock);
        AssertOrder(GetProcedure(script, "ReadStreamReplayMessages", "UpdateStreamReplayLease"), partitionLock, leaseLock, messageLock);
        AssertOrder(GetProcedure(script, "UpdateStreamReplayLease", "ReleaseStreamReplayLease"), partitionLock, leaseLock, messageLock);
        AssertOrder(GetProcedure(script, "ReleaseStreamReplayLease", "CleanupStreamMessages"), partitionLock, leaseLock, messageLock);
        AssertOrder(GetProcedure(script, "CleanupStreamMessages", "INSERT INTO OrleansQuery"), partitionLock, leaseLock, messageLock);
    }

    [Theory]
    [InlineData("SQLServer", "@AfterMessageId < COALESCE(@EarliestMessageId, @NextMessageId) - 1")]
    [InlineData("PostgreSQL", "_AfterMessageId < COALESCE(_EarliestMessageId, _NextMessageId) - 1")]
    [InlineData("MySQL", "_AfterMessageId < COALESCE(_EarliestMessageId, _NextMessageId) - 1")]
    public void AcquireValidatesOwnerAndRetainedBoundsBeforeInsertingLease(string provider, string retainedBound)
    {
        var acquire = GetProcedure(ReadScript(provider), "AcquireStreamReplayLease", "ReadStreamReplayMessages");

        AssertOrder(acquire, "OwnerEpoch", retainedBound, "'HistoryUnavailable'", "INSERT INTO OrleansStreamReplayLease");
        Assert.Contains("<> ", acquire, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SQLServer", "WHERE NOT EXISTS (SELECT 1 FROM @Messages)")]
    [InlineData("PostgreSQL", "OR NOT FOUND THEN")]
    [InlineData("MySQL", "WHERE NOT EXISTS")]
    public void ReplayReadReturnsAHeaderWhenNoMessagesAndRepeatsMetadata(string provider, string headerMarker)
    {
        var read = GetProcedure(ReadScript(provider), "ReadStreamReplayMessages", "UpdateStreamReplayLease");

        Assert.Contains(headerMarker, read, StringComparison.Ordinal);
        Assert.Contains("MessageId >", read, StringComparison.Ordinal);
        Assert.Contains("ORDER BY", read, StringComparison.Ordinal);
        Assert.Contains("MaxCount", read, StringComparison.Ordinal);
        Assert.Contains("NULL", read, StringComparison.Ordinal);
        Assert.True(CountOccurrences(read, "TailMessageId") >= 3);
        Assert.DoesNotContain("GREATEST(_Watermark, _AfterMessageId)", read, StringComparison.Ordinal);
        Assert.DoesNotContain("CASE WHEN @Watermark < @AfterMessageId", read, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SQLServer", "CASE WHEN @CurrentWatermark < @Watermark THEN @Watermark ELSE @CurrentWatermark END", "AND OwnerEpoch = @OwnerEpoch")]
    [InlineData("PostgreSQL", "GREATEST(_CurrentWatermark, _Watermark)", "AND L.OwnerEpoch = _OwnerEpoch")]
    [InlineData("MySQL", "GREATEST(_CurrentWatermark, _Watermark)", "AND OwnerEpoch = _OwnerEpoch")]
    public void ReplayMutationsMoveForwardAndFenceStaleOwners(string provider, string forwardOnly, string ownerFence)
    {
        var script = ReadScript(provider);
        var update = GetProcedure(script, "UpdateStreamReplayLease", "ReleaseStreamReplayLease");
        var release = GetProcedure(script, "ReleaseStreamReplayLease", "CleanupStreamMessages");

        Assert.Contains(forwardOnly, update, StringComparison.Ordinal);
        Assert.Contains(ownerFence, update, StringComparison.Ordinal);
        Assert.Contains(ownerFence, release, StringComparison.Ordinal);
        Assert.Contains("'OwnershipLost'", release, StringComparison.Ordinal);
        Assert.Contains("'Released'", release, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SQLServer", "ExpiresOn <= @Now", "MIN(Watermark)", "@ActiveReplayWatermark IS NULL OR MessageId <= @ActiveReplayWatermark", "Deleted.CreatedOn < DATEADD", "ORDER BY MessageId")]
    [InlineData("PostgreSQL", "L.ExpiresOn <= _Now", "MIN(L.Watermark)", "_ActiveReplayWatermark IS NULL OR M.MessageId <= _ActiveReplayWatermark", "D.CreatedOn < _Now", "ORDER BY M.MessageId")]
    [InlineData("MySQL", "ExpiresOn <= _Now", "MIN(Watermark)", "_ActiveReplayWatermark IS NULL OR MessageId <= _ActiveReplayWatermark", "CreatedOn < DATE_SUB", "ORDER BY MessageId")]
    public void CleanupFencesOwnersProtectsLeasesAndPreservesHardRetentionOverride(
        string provider,
        string expiryMarker,
        string activeWatermarkMarker,
        string replayProtection,
        string hardRetention,
        string orderMarker)
    {
        var cleanup = GetProcedure(ReadScript(provider), "CleanupStreamMessages", "INSERT INTO OrleansQuery");

        Assert.Contains("OwnerEpoch", cleanup, StringComparison.Ordinal);
        Assert.Contains(expiryMarker, cleanup, StringComparison.Ordinal);
        Assert.Contains(activeWatermarkMarker, cleanup, StringComparison.Ordinal);
        Assert.Contains("ActiveReplayWatermark", cleanup, StringComparison.Ordinal);
        Assert.Contains(replayProtection, cleanup, StringComparison.Ordinal);
        Assert.Contains("MaximumRetentionPeriodSeconds", cleanup, StringComparison.Ordinal);
        Assert.Contains(hardRetention, cleanup, StringComparison.Ordinal);
        Assert.Contains("AND NOT", cleanup, StringComparison.Ordinal);
        Assert.Contains("HardDeletedCount", cleanup, StringComparison.Ordinal);
        Assert.Contains(orderMarker, cleanup, StringComparison.Ordinal);
        Assert.Contains("CleanupOn", cleanup, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SQLServer", "BEGIN TRANSACTION", "SYSUTCDATETIME()", "UPDLOCK")]
    [InlineData("PostgreSQL", "LANGUAGE plpgsql", "clock_timestamp() AT TIME ZONE 'UTC'", "FOR UPDATE")]
    [InlineData("MySQL", "START TRANSACTION", "UTC_TIMESTAMP(6)", "FOR UPDATE")]
    public void ReplayRoutinesUseVendorTransactionsLocksAndDatabaseUtc(
        string provider,
        string transactionMarker,
        string utcMarker,
        string lockMarker)
    {
        var script = ReadScript(provider);

        foreach (var routineName in new[] { "AcquireStreamReplayLease", "ReadStreamReplayMessages", "UpdateStreamReplayLease" })
        {
            var routine = GetProcedure(
                script,
                routineName,
                routineName switch
                {
                    "AcquireStreamReplayLease" => "ReadStreamReplayMessages",
                    "ReadStreamReplayMessages" => "UpdateStreamReplayLease",
                    _ => "ReleaseStreamReplayLease"
                });
            Assert.Contains(transactionMarker, routine, StringComparison.Ordinal);
            Assert.Contains(utcMarker, routine, StringComparison.Ordinal);
            Assert.Contains(lockMarker, routine, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("SQLServer")]
    [InlineData("PostgreSQL")]
    [InlineData("MySQL")]
    public void ReplayQueryRowsBindThePublicParameterNames(string provider)
    {
        var script = ReadScript(provider);

        AssertQueryParameters(
            script,
            "AcquireStreamReplayLeaseKey",
            "ServiceId", "ProviderId", "QueueId", "ReaderId", "StreamIdBytes",
            "StreamNamespaceLength", "OwnerEpoch", "AfterMessageId", "ReplayLeaseDurationSeconds");
        AssertQueryParameters(
            script,
            "ReadStreamReplayMessagesKey",
            "ServiceId", "ProviderId", "QueueId", "ReaderId", "OwnerEpoch",
            "AfterMessageId", "MaxCount", "ReplayLeaseDurationSeconds");
        AssertQueryParameters(
            script,
            "UpdateStreamReplayLeaseKey",
            "ServiceId", "ProviderId", "QueueId", "ReaderId", "OwnerEpoch",
            "Watermark", "ReplayLeaseDurationSeconds");
        AssertQueryParameters(script, "ReleaseStreamReplayLeaseKey", "ServiceId", "ProviderId", "QueueId", "ReaderId", "OwnerEpoch");
        AssertQueryParameters(script, "CleanupStreamMessagesKey", "ServiceId", "ProviderId", "QueueId", "OwnerEpoch");
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
    public void MySqlSchemaUsesUnicodePartitionIdentifiers()
    {
        var script = ReadScript("MySQL");

        Assert.Contains("ServiceId VARCHAR(150) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL", script, StringComparison.Ordinal);
        Assert.Contains("IN _ServiceId VARCHAR(150) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin", script, StringComparison.Ordinal);
        Assert.DoesNotContain("ServiceId NVARCHAR(150)", script, StringComparison.Ordinal);
        Assert.DoesNotContain("ProviderId NVARCHAR(150)", script, StringComparison.Ordinal);
        Assert.DoesNotContain("QueueId NVARCHAR(150)", script, StringComparison.Ordinal);
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

    [Theory]
    [InlineData("SQLServer", "AND (@LockedCheckpoint IS NULL OR MessageId > @LockedCheckpoint)")]
    [InlineData("PostgreSQL", "AND (_PreviousCheckpoint IS NULL OR M.MessageId > _PreviousCheckpoint)")]
    [InlineData("MySQL", "AND (_CurrentCheckpoint IS NULL OR MessageId > _CurrentCheckpoint)")]
    public void CheckpointEligibilityUpdateStartsAfterPreviousCheckpoint(string provider, string lowerBound)
    {
        var checkpointProcedure = GetProcedure(ReadScript(provider), "AdvanceStreamCheckpoint", "CleanupStreamMessages");

        Assert.Contains(lowerBound, checkpointProcedure, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SQLServer", "READPAST")]
    [InlineData("PostgreSQL", "SKIP LOCKED")]
    [InlineData("MySQL", "SKIP LOCKED")]
    public void CleanupWaitsForLeadingEligibleRows(string provider, string skipLockedMarker)
    {
        var script = ReadScript(provider);
        var cleanupStart = script.IndexOf("CleanupStreamMessages", StringComparison.Ordinal);
        Assert.True(cleanupStart >= 0);
        var cleanupProcedure = script[cleanupStart..];

        Assert.Contains("ORDER BY MessageId", cleanupProcedure, StringComparison.Ordinal);
        Assert.DoesNotContain(skipLockedMarker, cleanupProcedure, StringComparison.Ordinal);
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

    private static string GetProcedure(string script, string startMarker, string endMarker)
    {
        var start = script.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = script.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start);
        return script[start..end];
    }

    private static void AssertParameters(string text, string prefix, params string[] parameterNames)
    {
        foreach (var parameterName in parameterNames)
        {
            Assert.Contains($"{prefix}{parameterName}", text, StringComparison.Ordinal);
        }
    }

    private static void AssertQueryParameters(string script, string queryKey, params string[] parameterNames) =>
        AssertParameters(GetStoredQuery(script, queryKey), "@", parameterNames);

    private static int CountOccurrences(string text, string value) =>
        (text.Length - text.Replace(value, string.Empty, StringComparison.Ordinal).Length) / value.Length;

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
