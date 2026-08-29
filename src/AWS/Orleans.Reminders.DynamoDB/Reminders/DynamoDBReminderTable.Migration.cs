using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Runtime;
using System.Globalization;

namespace Orleans.Reminders.DynamoDB;

internal sealed class DynamoDBReminderMigrationTestHooks
{
    public Func<Task>? AfterLegacyPageRead { get; init; }

    public Func<Task>? BeforeVerification { get; init; }

    public Func<Task>? AfterPageCheckpoint { get; init; }

    public Func<IReadOnlyList<ReminderEntry>, IReadOnlyList<ReminderEntry>>? LegacyDiscoveryResults { get; init; }
}

internal sealed partial class DynamoDBReminderTable
{
    private const string MigrationStateSortKey = "STATE";
    private const string MigrationLeaseSortKey = "LEASE";
    private const string NodeSortKeyPrefix = "NODE#";
    private const string StatusAttribute = "MigrationStatus";
    private const string OwnerAttribute = "LeaseOwner";
    private const string LeaseTokenAttribute = "LeaseToken";
    private const string ExpiresAtAttribute = "ExpiresAt";
    private const string CheckpointReminderIdAttribute = "CheckpointReminderId";
    private const string CheckpointGrainHashAttribute = "CheckpointGrainHash";
    private const string SourceCountAttribute = "SourceCount";
    private const string TargetCountAttribute = "TargetCount";
    private static readonly TimeSpan MigrationLeaseDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CompatibilityMarkerLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CompatibilityHeartbeatPeriod = TimeSpan.FromSeconds(30);

    private void ValidateOptions()
    {
        if (options.MigrationPageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MigrationPageSize), "Migration page size must be positive.");
        }

        if (string.IsNullOrWhiteSpace(options.TableName))
        {
            throw new ArgumentException("The legacy reminder table name must not be empty.", nameof(options.TableName));
        }

        if (string.IsNullOrWhiteSpace(v2TableName) || v2TableName.Length > 255)
        {
            throw new ArgumentException("The V2 reminder table name must contain between 1 and 255 characters.", nameof(options.V2TableName));
        }

        if (string.Equals(options.TableName, v2TableName, StringComparison.Ordinal))
        {
            throw new ArgumentException("The V2 reminder table must be different from the legacy table.", nameof(options.V2TableName));
        }
    }

    private async Task RefreshReadMode()
    {
        if (options.TableMode == DynamoDBReminderTableMode.Legacy)
        {
            return;
        }

        var state = await ReadMigrationState();
        useV2Reads = state?.Status is MigrationStatus.Cutover or MigrationStatus.Retired;
        useV2OnlyWrites = state?.Status == MigrationStatus.Retired;
    }

    private async Task InitializeMigration(CancellationToken cancellationToken)
    {
        var state = await ReadMigrationState();
        if (state?.Status == MigrationStatus.Retired)
        {
            if (options.TableMode == DynamoDBReminderTableMode.Rollback)
            {
                throw new InvalidOperationException("The DynamoDB reminder V1 schema has been retired and can no longer be rolled back.");
            }

            await EnsureCompatibleCluster(cancellationToken);
            useV2Reads = true;
            useV2OnlyWrites = true;
            return;
        }

        if (state?.Status == MigrationStatus.Cutover && options.TableMode != DynamoDBReminderTableMode.Rollback)
        {
            if (options.TableMode == DynamoDBReminderTableMode.V2Only)
            {
                await RetireLegacySchema(cancellationToken);
                return;
            }

            await EnsureCompatibleCluster(cancellationToken);
            useV2Reads = true;
            return;
        }

        if (options.TableMode == DynamoDBReminderTableMode.V2Only)
        {
            throw new InvalidOperationException("V2Only mode requires the service to be in the Cutover state.");
        }

        var waitForLease = options.TableMode is DynamoDBReminderTableMode.V2 or DynamoDBReminderTableMode.Rollback;
        if (!await AcquireMigrationLease(waitForLease, cancellationToken))
        {
            useV2Reads = state?.Status == MigrationStatus.Cutover;
            LogMigrationLeaseContended(logger, serviceId, migrationOwner);
            return;
        }

        state = await ReadMigrationState();
        if (state?.Status == MigrationStatus.Retired)
        {
            throw new InvalidOperationException("The DynamoDB reminder V1 schema was retired while waiting for the migration lease.");
        }

        if (state?.Status == MigrationStatus.Cutover && options.TableMode == DynamoDBReminderTableMode.V2)
        {
            useV2Reads = true;
            await ReleaseMigrationLease();
            return;
        }

        StartLeaseRenewal();
        try
        {
            if (options.TableMode == DynamoDBReminderTableMode.Rollback)
            {
                await EnsureCompatibleCluster(cancellationToken);
                await ReconcileAndVerify(cancellationToken, preserveMigrationState: true);
                await WriteMigrationState(new(MigrationStatus.RolledBack));
                useV2Reads = false;
                LogMigrationState(logger, serviceId, MigrationStatus.RolledBack.ToString());
                return;
            }

            MembershipVersion? membershipVersion = null;
            if (options.TableMode == DynamoDBReminderTableMode.V2)
            {
                membershipVersion = await EnsureCompatibleCluster(cancellationToken);
            }

            await BackfillAndVerify(cancellationToken);

            if (options.TableMode == DynamoDBReminderTableMode.V2)
            {
                var finalMembershipVersion = await EnsureCompatibleCluster(cancellationToken);
                if (finalMembershipVersion != membershipVersion)
                {
                    throw new InvalidOperationException(
                        "Cluster membership changed during DynamoDB reminder finalization. "
                        + "Migration remains verified but was not cut over; retry after membership stabilizes.");
                }

                await WriteMigrationState(new(MigrationStatus.Cutover));
                useV2Reads = true;
                LogMigrationState(logger, serviceId, MigrationStatus.Cutover.ToString());
            }
            else
            {
                useV2Reads = false;
            }
        }
        finally
        {
            await StopLeaseRenewal();
            await ReleaseMigrationLease();
        }
    }

    private async Task RetireLegacySchema(CancellationToken cancellationToken)
    {
        if (!await AcquireMigrationLease(wait: true, cancellationToken))
        {
            throw new InvalidOperationException("Unable to acquire the DynamoDB reminder migration lease for V1 retirement.");
        }

        StartLeaseRenewal();
        try
        {
            var state = await ReadMigrationState();
            if (state?.Status == MigrationStatus.Retired)
            {
                useV2Reads = true;
                useV2OnlyWrites = true;
                return;
            }

            if (state?.Status != MigrationStatus.Cutover)
            {
                throw new InvalidOperationException($"V1 retirement requires Cutover state, but found '{state?.Status.ToString() ?? "missing"}'.");
            }

            var membershipVersion = await EnsureCompatibleCluster(cancellationToken);
            await ReconcileAndVerify(cancellationToken, preserveMigrationState: true);
            var finalMembershipVersion = await EnsureCompatibleCluster(cancellationToken);
            if (finalMembershipVersion != membershipVersion)
            {
                throw new InvalidOperationException(
                    "Cluster membership changed during DynamoDB reminder V1 retirement. Retry after membership stabilizes.");
            }

            await WriteMigrationState(new(MigrationStatus.Retired));
            useV2Reads = true;
            useV2OnlyWrites = true;
            LogMigrationState(logger, serviceId, MigrationStatus.Retired.ToString());
        }
        finally
        {
            await StopLeaseRenewal();
            await ReleaseMigrationLease();
        }
    }

    private async Task BackfillAndVerify(CancellationToken cancellationToken)
    {
        var state = await ReadMigrationState() ?? new(MigrationStatus.Backfilling);
        state.Status = MigrationStatus.Backfilling;
        await WriteMigrationState(state);
        LogMigrationState(logger, serviceId, state.Status.ToString());

        var checkpoint = state.GetCheckpoint();
        var pageNumber = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (records, nextCheckpoint) = await storage.ScanPageAsync(
                options.TableName,
                new() { [":service"] = new(serviceId) },
                $"{SERVICE_ID_PROPERTY_NAME} = :service",
                static item => new LegacyReminderRecord(item),
                options.MigrationPageSize,
                checkpoint,
                cancellationToken);

            if (testHooks?.AfterLegacyPageRead is { } afterPageRead)
            {
                await afterPageRead();
            }

            var conflict = false;
            foreach (var record in records)
            {
                if (!await CopyLegacyRecord(record))
                {
                    conflict = true;
                    break;
                }
            }

            if (conflict)
            {
                continue;
            }

            checkpoint = nextCheckpoint;
            state.SetCheckpoint(checkpoint);
            await WriteMigrationState(state);
            await RenewMigrationLease();
            LogMigrationPage(logger, serviceId, ++pageNumber, records.Count);
            if (testHooks?.AfterPageCheckpoint is { } afterPageCheckpoint)
            {
                await afterPageCheckpoint();
            }

            if (checkpoint is not { Count: > 0 })
            {
                break;
            }
        }

        await ReconcileAndVerify(cancellationToken, preserveMigrationState: false);
    }

    private async Task<bool> CopyLegacyRecord(LegacyReminderRecord record)
    {
        var entry = Resolve(record.Item);
        var values = new Dictionary<string, AttributeValue> { [":sourceETag"] = Clone(record.Item[ETAG_PROPERTY_NAME]) };
        try
        {
            await storage.WriteTxAsync(
            [
                new() { ConditionCheck = CreateMigrationLeaseFence() },
                new()
                {
                    ConditionCheck = new()
                    {
                        TableName = options.TableName,
                        Key = GetLegacyKey(entry.GrainId, entry.ReminderName),
                        ConditionExpression = $"{ETAG_PROPERTY_NAME} = :sourceETag",
                        ExpressionAttributeValues = values,
                    },
                },
                new()
                {
                    Put = CreateIdentitySafeV2Put(CreateV2ItemFromLegacy(record.Item, entry), entry),
                },
            ]);
            return true;
        }
        catch (TransactionCanceledException exception) when (IsConditionalFailureAt(exception, 0))
        {
            throw new InvalidOperationException("The DynamoDB reminder migration lease was lost while copying a row.", exception);
        }
        catch (TransactionCanceledException exception) when (IsConditionalFailureAt(exception, 1))
        {
            return false;
        }
    }

    private async Task ReconcileAndVerify(CancellationToken cancellationToken, bool preserveMigrationState)
    {
        if (!preserveMigrationState)
        {
            var state = new MigrationState(MigrationStatus.Verifying);
            await WriteMigrationState(state);
            LogMigrationState(logger, serviceId, state.Status.ToString());
        }

        await ForEachLegacyRecord(async sourceRecord =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await CopyLegacyRecord(sourceRecord))
            {
                throw new InvalidOperationException("A legacy reminder changed during final verification. Retry migration.");
            }
        }, cancellationToken);

        await ForEachV2Record(async targetRecord =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = Resolve(targetRecord.Item);
            var sourceRecord = await storage.ReadSingleEntryAsync(
                options.TableName,
                GetLegacyKey(entry.GrainId, entry.ReminderName),
                static item => new LegacyReminderRecord(item));
            if (sourceRecord is null)
            {
                await DeleteOrphan(targetRecord);
            }
            else if (!RecordsEqual(sourceRecord.Item, targetRecord.Item) && !await CopyLegacyRecord(sourceRecord))
            {
                throw new InvalidOperationException("A legacy reminder changed during final reconciliation. Retry migration.");
            }
        }, cancellationToken);

        if (testHooks?.BeforeVerification is { } beforeVerification)
        {
            await beforeVerification();
        }

        var sourceCount = 0;
        var targetCount = 0;
        var verified = true;
        await ForEachLegacyRecord(async sourceRecord =>
        {
            sourceCount++;
            var entry = Resolve(sourceRecord.Item);
            var targetRecord = await storage.ReadSingleEntryAsync(
                v2TableName,
                GetV2Key(entry.GrainId, entry.ReminderName),
                static item => new V2ReminderRecord(item));
            verified &= targetRecord is not null && RecordsEqual(sourceRecord.Item, targetRecord.Item);
        }, cancellationToken);
        await ForEachV2Record(async targetRecord =>
        {
            targetCount++;
            var entry = Resolve(targetRecord.Item);
            var sourceRecord = await storage.ReadSingleEntryAsync(
                options.TableName,
                GetLegacyKey(entry.GrainId, entry.ReminderName),
                static item => new LegacyReminderRecord(item));
            verified &= sourceRecord is not null && RecordsEqual(sourceRecord.Item, targetRecord.Item);
        }, cancellationToken);
        verified &= sourceCount == targetCount;

        if (!verified)
        {
            if (!preserveMigrationState)
            {
                await WriteMigrationState(new(MigrationStatus.VerificationFailed, sourceCount, targetCount));
            }

            LogMigrationVerificationFailed(logger, serviceId, sourceCount, targetCount);
            throw new InvalidOperationException(
                $"DynamoDB reminder migration verification failed for service '{serviceId}': "
                + $"legacy={sourceCount}, v2={targetCount}. Cutover was not performed.");
        }

        if (!preserveMigrationState)
        {
            await WriteMigrationState(new(MigrationStatus.Ready, sourceCount, targetCount));
        }

        LogMigrationVerified(logger, serviceId, sourceCount);
    }

    private async Task DeleteOrphan(V2ReminderRecord record)
    {
        var entry = Resolve(record.Item);
        var etag = Clone(record.Item[ETAG_PROPERTY_NAME]);
        try
        {
            await storage.WriteTxAsync(
            [
                new() { ConditionCheck = CreateMigrationLeaseFence() },
                new()
                {
                    ConditionCheck = new()
                    {
                        TableName = options.TableName,
                        Key = GetLegacyKey(entry.GrainId, entry.ReminderName),
                        ConditionExpression = $"attribute_not_exists({REMINDER_ID_PROPERTY_NAME})",
                    },
                },
                new()
                {
                    Delete = new()
                    {
                        TableName = v2TableName,
                        Key = new()
                        {
                            [V2PartitionKeyName] = record.Item[V2PartitionKeyName],
                            [V2SortKeyName] = record.Item[V2SortKeyName],
                        },
                        ConditionExpression = $"{ETAG_PROPERTY_NAME} = :targetETag",
                        ExpressionAttributeValues = new() { [":targetETag"] = etag },
                    },
                },
            ]);
        }
        catch (TransactionCanceledException exception) when (IsConditionalFailureAt(exception, 0))
        {
            throw new InvalidOperationException("The DynamoDB reminder migration lease was lost while removing an orphan.", exception);
        }
        catch (TransactionCanceledException exception) when (IsConditionalFailure(exception))
        {
        }
    }

    private async Task ForEachLegacyRecord(Func<LegacyReminderRecord, Task> action, CancellationToken cancellationToken)
    {
        Dictionary<string, AttributeValue>? checkpoint = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (records, nextCheckpoint) = await storage.ScanPageAsync(
                options.TableName,
                new() { [":service"] = new(serviceId) },
                $"{SERVICE_ID_PROPERTY_NAME} = :service",
                static item => new LegacyReminderRecord(item),
                options.MigrationPageSize,
                checkpoint,
                cancellationToken);
            foreach (var record in records)
            {
                await action(record);
            }

            checkpoint = nextCheckpoint;
        }
        while (checkpoint is { Count: > 0 });
    }

    private async Task ForEachV2Record(Func<V2ReminderRecord, Task> action, CancellationToken cancellationToken)
    {
        for (var bucket = 0; bucket < V2BucketCount; bucket++)
        {
            var values = new Dictionary<string, AttributeValue>
            {
                [":partition"] = new($"{v2DataPartitionPrefix}{bucket:X2}"),
            };
            Dictionary<string, AttributeValue>? checkpoint = null;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (records, nextCheckpoint) = await storage.QueryPageAsync(
                    v2TableName,
                    values,
                    $"{V2PartitionKeyName} = :partition",
                    static item => new V2ReminderRecord(item),
                    lastEvaluatedKey: checkpoint,
                    cancellationToken: cancellationToken);
                foreach (var record in records)
                {
                    await action(record);
                }

                checkpoint = nextCheckpoint;
            }
            while (checkpoint is { Count: > 0 });
        }
    }

    private static bool RecordsEqual(
        IReadOnlyDictionary<string, AttributeValue> legacy,
        IReadOnlyDictionary<string, AttributeValue> v2)
        => GetAttributeString(legacy[GRAIN_HASH_PROPERTY_NAME]) == GetAttributeString(v2[GRAIN_HASH_PROPERTY_NAME])
            && GetAttributeString(legacy[GRAIN_REFERENCE_PROPERTY_NAME]) == GetAttributeString(v2[GRAIN_REFERENCE_PROPERTY_NAME])
            && GetAttributeString(legacy[REMINDER_NAME_PROPERTY_NAME]) == GetAttributeString(v2[REMINDER_NAME_PROPERTY_NAME])
            && GetAttributeString(legacy[START_TIME_PROPERTY_NAME]) == GetAttributeString(v2[START_TIME_PROPERTY_NAME])
            && GetAttributeString(legacy[PERIOD_PROPERTY_NAME]) == GetAttributeString(v2[PERIOD_PROPERTY_NAME])
            && GetAttributeString(legacy[ETAG_PROPERTY_NAME]) == GetAttributeString(v2[ETAG_PROPERTY_NAME]);

    private async Task<bool> AcquireMigrationLease(bool wait, CancellationToken cancellationToken)
    {
        do
        {
            if (await TryAcquireMigrationLease())
            {
                return true;
            }

            if (!wait)
            {
                return false;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, cancellationToken);
        }
        while (true);
    }

    private async Task<bool> TryAcquireMigrationLease()
    {
        var now = timeProvider.GetUtcNow();
        leaseToken ??= Guid.NewGuid().ToString("N");
        var item = MetadataItem(MigrationLeaseSortKey);
        item[OwnerAttribute] = new(migrationOwner);
        item[LeaseTokenAttribute] = new(leaseToken);
        item[ExpiresAtAttribute] = new() { N = now.Add(MigrationLeaseDuration).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) };
        try
        {
            await storage.PutEntryAsync(
                v2TableName,
                item,
                $"attribute_not_exists({OwnerAttribute}) OR {ExpiresAtAttribute} < :now OR ({OwnerAttribute} = :owner AND {LeaseTokenAttribute} = :token)",
                new()
                {
                    [":now"] = new() { N = now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) },
                    [":owner"] = new(migrationOwner),
                    [":token"] = new(leaseToken),
                });
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    private async Task RenewMigrationLease()
    {
        if (!await TryAcquireMigrationLease())
        {
            throw new InvalidOperationException("The DynamoDB reminder migration lease was lost.");
        }

    }

    private void StartLeaseRenewal()
    {
        leaseRenewalCancellation = new();
        leaseRenewalTask = RunLeaseRenewal(leaseRenewalCancellation.Token);
    }

    private async Task RunLeaseRenewal(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(CompatibilityHeartbeatPeriod, timeProvider);
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await RenewMigrationLease();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task StopLeaseRenewal()
    {
        if (leaseRenewalCancellation is null)
        {
            return;
        }

        await leaseRenewalCancellation.CancelAsync();
        if (leaseRenewalTask is not null)
        {
            await leaseRenewalTask;
        }

        leaseRenewalCancellation.Dispose();
        leaseRenewalCancellation = null;
        leaseRenewalTask = null;
    }

    private async Task ReleaseMigrationLease()
    {
        try
        {
            await storage.DeleteEntryAsync(
                v2TableName,
                MetadataKey(MigrationLeaseSortKey),
                $"{OwnerAttribute} = :owner AND {LeaseTokenAttribute} = :token",
                new()
                {
                    [":owner"] = new(migrationOwner),
                    [":token"] = new(leaseToken),
                });
        }
        catch (ConditionalCheckFailedException)
        {
        }
    }

    private async Task StartCompatibilityHeartbeat()
    {
        await WriteCompatibilityMarker();
        heartbeatCancellation = new();
        heartbeatTask = RunCompatibilityHeartbeat(heartbeatCancellation.Token);
    }

    private async Task RunCompatibilityHeartbeat(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(CompatibilityHeartbeatPeriod, timeProvider);
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await WriteCompatibilityMarker();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private Task WriteCompatibilityMarker()
    {
        var item = MetadataItem($"{NodeSortKeyPrefix}{Encode(migrationOwner)}");
        item[OwnerAttribute] = new(migrationOwner);
        item[ExpiresAtAttribute] = new()
        {
            N = timeProvider.GetUtcNow().Add(CompatibilityMarkerLifetime).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
        };
        return storage.PutEntryAsync(v2TableName, item);
    }

    private async Task<MembershipVersion> EnsureCompatibleCluster(CancellationToken cancellationToken)
    {
        if (membershipService is null || localSiloDetails is null)
        {
            throw new InvalidOperationException(
                "V2 cutover and rollback require Orleans cluster membership services so active incompatible silos can be detected.");
        }

        await membershipService.Refresh(cancellationToken: cancellationToken);
        var snapshot = membershipService.CurrentSnapshot;
        var markers = await storage.QueryAllAsync(
            v2TableName,
            new()
            {
                [":partition"] = new(v2MetadataPartitionKey),
                [":prefix"] = new(NodeSortKeyPrefix),
            },
            $"{V2PartitionKeyName} = :partition AND begins_with({V2SortKeyName}, :prefix)",
            static item => item);
        var now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var compatible = markers
            .Where(item => long.Parse(item[ExpiresAtAttribute].N, CultureInfo.InvariantCulture) >= now)
            .Select(item => item[OwnerAttribute].S)
            .ToHashSet(StringComparer.Ordinal);
        var incompatible = snapshot.Members.Values
            .Where(static member => member.Status is SiloStatus.Created or SiloStatus.Joining or SiloStatus.Active)
            .Select(static member => member.SiloAddress.ToParsableString())
            .Where(address => !compatible.Contains(address))
            .ToArray();
        if (incompatible.Length > 0)
        {
            throw new InvalidOperationException(
                "DynamoDB reminder schema transition was blocked because active silos did not publish V2 compatibility markers: "
                + string.Join(", ", incompatible));
        }

        return snapshot.Version;
    }

    private async Task<MigrationState?> ReadMigrationState()
        => await storage.ReadSingleEntryAsync(v2TableName, MetadataKey(MigrationStateSortKey), MigrationState.FromItem);

    private async Task WriteMigrationState(MigrationState state)
    {
        if (leaseToken is null)
        {
            throw new InvalidOperationException("Migration state cannot be changed without a fencing token.");
        }

        try
        {
            await storage.WriteTxAsync(
            [
                new()
                {
                    ConditionCheck = new()
                    {
                        TableName = v2TableName,
                        Key = MetadataKey(MigrationLeaseSortKey),
                        ConditionExpression = $"{OwnerAttribute} = :owner AND {LeaseTokenAttribute} = :token AND {ExpiresAtAttribute} > :now",
                        ExpressionAttributeValues = new()
                        {
                            [":owner"] = new(migrationOwner),
                            [":token"] = new(leaseToken),
                            [":now"] = new() { N = timeProvider.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) },
                        },
                    },
                },
                new()
                {
                    Put = new()
                    {
                        TableName = v2TableName,
                        Item = state.ToItem(v2MetadataPartitionKey),
                    },
                },
            ]);
        }
        catch (TransactionCanceledException exception) when (IsConditionalFailure(exception))
        {
            throw new InvalidOperationException("The DynamoDB reminder migration lease fencing token is no longer current.", exception);
        }
    }

    private ConditionCheck CreateMigrationLeaseFence()
    {
        if (leaseToken is null)
        {
            throw new InvalidOperationException("Migration writes require a fencing token.");
        }

        return new()
        {
            TableName = v2TableName,
            Key = MetadataKey(MigrationLeaseSortKey),
            ConditionExpression = $"{OwnerAttribute} = :owner AND {LeaseTokenAttribute} = :token AND {ExpiresAtAttribute} > :now",
            ExpressionAttributeValues = new()
            {
                [":owner"] = new(migrationOwner),
                [":token"] = new(leaseToken),
                [":now"] = new() { N = timeProvider.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) },
            },
        };
    }

    private Dictionary<string, AttributeValue> MetadataItem(string sortKey)
        => new()
        {
            [V2PartitionKeyName] = new(v2MetadataPartitionKey),
            [V2SortKeyName] = new(sortKey),
        };

    private Dictionary<string, AttributeValue> MetadataKey(string sortKey) => MetadataItem(sortKey);

    private static AttributeValue Clone(AttributeValue value)
        => new() { S = value.S, N = value.N, B = value.B };

    private Dictionary<string, AttributeValue> CreateV2ItemFromLegacy(
        Dictionary<string, AttributeValue> legacy,
        ReminderEntry entry)
    {
        var result = legacy
            .Where(static pair => pair.Key != REMINDER_ID_PROPERTY_NAME)
            .ToDictionary(static pair => pair.Key, static pair => Clone(pair.Value), StringComparer.Ordinal);
        var hash = entry.GrainId.GetUniformHashCode();
        result[V2PartitionKeyName] = new(GetV2PartitionKey(hash));
        result[V2SortKeyName] = new(GetV2SortKey(hash, entry.GrainId, entry.ReminderName));
        return result;
    }

    private static string GetAttributeString(AttributeValue value)
        => value.S ?? value.N ?? throw new InvalidOperationException("Expected a scalar DynamoDB attribute.");

    private sealed class LegacyReminderRecord(Dictionary<string, AttributeValue> item)
    {
        public Dictionary<string, AttributeValue> Item { get; } = item;

        public string Identity => $"{Item[GRAIN_REFERENCE_PROPERTY_NAME].S}\0{Item[REMINDER_NAME_PROPERTY_NAME].S}";
    }

    private sealed class V2ReminderRecord(Dictionary<string, AttributeValue> item)
    {
        public Dictionary<string, AttributeValue> Item { get; } = item;

        public string Identity => $"{Item[GRAIN_REFERENCE_PROPERTY_NAME].S}\0{Item[REMINDER_NAME_PROPERTY_NAME].S}";
    }

    internal enum MigrationStatus
    {
        Backfilling,
        Verifying,
        Ready,
        Cutover,
        RolledBack,
        VerificationFailed,
        Retired,
    }

    private sealed class MigrationState(MigrationStatus status, int sourceCount = 0, int targetCount = 0)
    {
        public MigrationStatus Status { get; set; } = status;

        public string? CheckpointReminderId { get; set; }

        public string? CheckpointGrainHash { get; set; }

        public int SourceCount { get; set; } = sourceCount;

        public int TargetCount { get; set; } = targetCount;

        public Dictionary<string, AttributeValue>? GetCheckpoint()
            => CheckpointReminderId is null || CheckpointGrainHash is null
                ? null
                : new()
                {
                    [REMINDER_ID_PROPERTY_NAME] = new(CheckpointReminderId),
                    [GRAIN_HASH_PROPERTY_NAME] = new() { N = CheckpointGrainHash },
                };

        public void SetCheckpoint(Dictionary<string, AttributeValue>? checkpoint)
        {
            CheckpointReminderId = checkpoint is { Count: > 0 } ? checkpoint[REMINDER_ID_PROPERTY_NAME].S : null;
            CheckpointGrainHash = checkpoint is { Count: > 0 } ? checkpoint[GRAIN_HASH_PROPERTY_NAME].N : null;
        }

        public Dictionary<string, AttributeValue> ToItem(string partitionKey)
        {
            var result = new Dictionary<string, AttributeValue>
            {
                [V2PartitionKeyName] = new(partitionKey),
                [V2SortKeyName] = new(MigrationStateSortKey),
                [StatusAttribute] = new(Status.ToString()),
                [SourceCountAttribute] = new() { N = SourceCount.ToString(CultureInfo.InvariantCulture) },
                [TargetCountAttribute] = new() { N = TargetCount.ToString(CultureInfo.InvariantCulture) },
            };
            if (CheckpointReminderId is not null && CheckpointGrainHash is not null)
            {
                result[CheckpointReminderIdAttribute] = new(CheckpointReminderId);
                result[CheckpointGrainHashAttribute] = new() { N = CheckpointGrainHash };
            }

            return result;
        }

        public static MigrationState FromItem(Dictionary<string, AttributeValue> item)
            => new(
                Enum.Parse<MigrationStatus>(item[StatusAttribute].S),
                int.Parse(item[SourceCountAttribute].N, CultureInfo.InvariantCulture),
                int.Parse(item[TargetCountAttribute].N, CultureInfo.InvariantCulture))
            {
                CheckpointReminderId = item.TryGetValue(CheckpointReminderIdAttribute, out var reminderId) ? reminderId.S : null,
                CheckpointGrainHash = item.TryGetValue(CheckpointGrainHashAttribute, out var grainHash) ? grainHash.N : null,
            };
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "DynamoDB reminder migration for service {ServiceId} entered {State}.")]
    private static partial void LogMigrationState(ILogger logger, string serviceId, string state);

    [LoggerMessage(Level = LogLevel.Information, Message = "DynamoDB reminder migration for service {ServiceId} completed page {PageNumber} containing {ItemCount} matching items.")]
    private static partial void LogMigrationPage(ILogger logger, string serviceId, int pageNumber, int itemCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "DynamoDB reminder migration for service {ServiceId} verified {ItemCount} reminders.")]
    private static partial void LogMigrationVerified(ILogger logger, string serviceId, int itemCount);

    [LoggerMessage(Level = LogLevel.Error, Message = "DynamoDB reminder migration verification failed for service {ServiceId}: source count {SourceCount}, target count {TargetCount}.")]
    private static partial void LogMigrationVerificationFailed(ILogger logger, string serviceId, int sourceCount, int targetCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "DynamoDB reminder migration lease for service {ServiceId} is held by another owner; {Owner} will continue in the current read mode.")]
    private static partial void LogMigrationLeaseContended(ILogger logger, string serviceId, string owner);
}
