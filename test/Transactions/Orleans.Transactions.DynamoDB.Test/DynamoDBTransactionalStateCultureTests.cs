using System.Globalization;
using Amazon.DynamoDBv2.Model;
using Orleans.Transactions.DynamoDB.TransactionalState;
using Xunit;

namespace Orleans.Transactions.DynamoDB.Tests;

public class DynamoDBTransactionalStateCultureTests
{
    [Fact]
    public void KeyEntity_ToStorageFormat_UsesInvariantNumbersAndLegacyValuesRoundTripUnderCustomCulture()
    {
        RunWithCustomCulture(() =>
        {
            const long committedSequenceId = -1_234_567_890_123_456_789;
            const long timestampSeconds = -123_456_789;
            const long etag = -9_876_543_210;
            byte[] metadata = [1, 2, 3, 4];

            var entity = new KeyEntity("partition")
            {
                CommittedSequenceId = committedSequenceId,
                Metadata = metadata,
                Timestamp = DateTimeOffset.FromUnixTimeSeconds(timestampSeconds),
                ETag = etag
            };

            var stored = entity.ToStorageFormat();

            Assert.Equal("-1234567890123456789", stored[nameof(KeyEntity.CommittedSequenceId)].N);
            Assert.Equal("-123456789", stored[DynamoDBTransactionalStateConstants.TIMESTAMP_PROPERTY_NAME].N);
            Assert.Equal("-9876543210", stored[DynamoDBTransactionalStateConstants.ETAG_PROPERTY_NAME].N);

            var legacy = new Dictionary<string, AttributeValue>
            {
                [DynamoDBTransactionalStateConstants.PARTITION_KEY_PROPERTY_NAME] = new() { S = "legacy-partition" },
                [DynamoDBTransactionalStateConstants.ROW_KEY_PROPERTY_NAME] = new() { S = KeyEntity.RK },
                [nameof(KeyEntity.CommittedSequenceId)] = new() { N = "-1234567890123456789" },
                [nameof(KeyEntity.Metadata)] = new() { B = new MemoryStream(metadata) },
                [DynamoDBTransactionalStateConstants.TIMESTAMP_PROPERTY_NAME] = new() { N = "-123456789" },
                [DynamoDBTransactionalStateConstants.ETAG_PROPERTY_NAME] = new() { N = "-9876543210" }
            };

            var roundTripped = new KeyEntity(legacy);
            var reserialized = roundTripped.ToStorageFormat();

            Assert.Equal("legacy-partition", roundTripped.PartitionKey);
            Assert.Equal(KeyEntity.RK, roundTripped.RowKey);
            Assert.Equal(committedSequenceId, roundTripped.CommittedSequenceId);
            Assert.Equal(metadata, roundTripped.Metadata);
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(timestampSeconds), roundTripped.Timestamp);
            Assert.Equal(etag, roundTripped.ETag);
            Assert.Equal(legacy[nameof(KeyEntity.CommittedSequenceId)].N, reserialized[nameof(KeyEntity.CommittedSequenceId)].N);
            Assert.Equal(legacy[DynamoDBTransactionalStateConstants.TIMESTAMP_PROPERTY_NAME].N, reserialized[DynamoDBTransactionalStateConstants.TIMESTAMP_PROPERTY_NAME].N);
            Assert.Equal(legacy[DynamoDBTransactionalStateConstants.ETAG_PROPERTY_NAME].N, reserialized[DynamoDBTransactionalStateConstants.ETAG_PROPERTY_NAME].N);
            Assert.Equal(metadata, reserialized[nameof(KeyEntity.Metadata)].B.ToArray());
        });
    }

    [Fact]
    public void StateEntity_RowKeyAndSequenceId_UseInvariantLowercaseFixedWidthHexUnderCustomCulture()
    {
        RunWithCustomCulture(() =>
        {
            const long sequenceId = 0x0123456789ABCDEF;

            var rowKey = StateEntity.MakeRowKey(sequenceId);
            var entity = new StateEntity { RowKey = rowKey };

            Assert.Equal("state_0123456789abcdef", rowKey);
            Assert.Equal(16, rowKey[StateEntity.ROW_KEY_PREFIX.Length..].Length);
            Assert.Equal(sequenceId, entity.SequenceId);
        });
    }

    [Fact]
    public void StateEntity_ToStorageFormat_UsesInvariantTimestampAndETagAndLegacyValuesRoundTripUnderCustomCulture()
    {
        RunWithCustomCulture(() =>
        {
            var timestamp = new DateTime(2024, 2, 29, 12, 34, 56, DateTimeKind.Utc).AddTicks(7_891_234);
            const long etag = -9_876_543_210;
            byte[] transactionManager = [5, 6, 7];
            byte[] state = [8, 9, 10, 11];
            var rowKey = StateEntity.MakeRowKey(0x0123456789ABCDEF);

            var entity = new StateEntity
            {
                PartitionKey = "partition",
                RowKey = rowKey,
                TransactionId = "transaction",
                TransactionTimestamp = timestamp,
                TransactionManager = transactionManager,
                State = state,
                ETag = etag
            };

            var stored = entity.ToStorageFormat();

            Assert.Equal("2024-02-29T12:34:56.7891234Z", stored[StateEntity.TRANSACTION_TIMESTAMP_PROPERTY_NAME].S);
            Assert.Equal("-9876543210", stored[DynamoDBTransactionalStateConstants.ETAG_PROPERTY_NAME].N);

            var legacy = new Dictionary<string, AttributeValue>
            {
                [DynamoDBTransactionalStateConstants.PARTITION_KEY_PROPERTY_NAME] = new() { S = "legacy-partition" },
                [DynamoDBTransactionalStateConstants.ROW_KEY_PROPERTY_NAME] = new() { S = rowKey },
                [StateEntity.TRANSACTION_ID_PROPERTY_NAME] = new() { S = "legacy-transaction" },
                [StateEntity.TRANSACTION_TIMESTAMP_PROPERTY_NAME] = new() { S = "2024-02-29T12:34:56.7891234Z" },
                [StateEntity.TRANSACTION_MANAGER_PROPERTY_NAME] = new() { B = new MemoryStream(transactionManager) },
                [DynamoDBTransactionalStateConstants.BINARY_STATE_PROPERTY_NAME] = new() { B = new MemoryStream(state) },
                [DynamoDBTransactionalStateConstants.ETAG_PROPERTY_NAME] = new() { N = "-9876543210" }
            };

            var roundTripped = new StateEntity(legacy);
            var reserialized = roundTripped.ToStorageFormat();

            Assert.Equal("legacy-partition", roundTripped.PartitionKey);
            Assert.Equal(rowKey, roundTripped.RowKey);
            Assert.Equal(0x0123456789ABCDEF, roundTripped.SequenceId);
            Assert.Equal("legacy-transaction", roundTripped.TransactionId);
            Assert.Equal(timestamp.Ticks, roundTripped.TransactionTimestamp.Ticks);
            Assert.Equal(DateTimeKind.Utc, roundTripped.TransactionTimestamp.Kind);
            Assert.Equal(transactionManager, roundTripped.TransactionManager);
            Assert.Equal(state, roundTripped.State);
            Assert.Equal(etag, roundTripped.ETag);
            Assert.Equal(legacy[StateEntity.TRANSACTION_TIMESTAMP_PROPERTY_NAME].S, reserialized[StateEntity.TRANSACTION_TIMESTAMP_PROPERTY_NAME].S);
            Assert.Equal(legacy[DynamoDBTransactionalStateConstants.ETAG_PROPERTY_NAME].N, reserialized[DynamoDBTransactionalStateConstants.ETAG_PROPERTY_NAME].N);
            Assert.Equal(transactionManager, reserialized[StateEntity.TRANSACTION_MANAGER_PROPERTY_NAME].B.ToArray());
            Assert.Equal(state, reserialized[DynamoDBTransactionalStateConstants.BINARY_STATE_PROPERTY_NAME].B.ToArray());
        });
    }

    private static void RunWithCustomCulture(Action action)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUICulture = CultureInfo.CurrentUICulture;
        var culture = (CultureInfo)CultureInfo.GetCultureInfo("fr-FR").Clone();
        culture.NumberFormat.NegativeSign = "NEG";
        culture.NumberFormat.NumberGroupSeparator = "_";
        culture.NumberFormat.NumberGroupSizes = [2];

        try
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            action();
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUICulture;
        }
    }
}
