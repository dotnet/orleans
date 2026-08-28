using System.Data;
using System.Data.Common;
using NSubstitute;
using Orleans.Persistence.AdoNet.Storage;
using UnitTests.StorageTests.Relational.Fakes;

namespace UnitTests.StorageTests.Relational;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Persistence")]
public sealed class DbExtensionsTests
{
    [Fact]
    public void AddParameter_PreservesExplicitDbType()
    {
        using var command = new RecordingDbCommand();

        command.AddParameter("@amount", 42, dbType: DbType.Currency);

        var parameter = Assert.IsType<RecordingDbParameter>(Assert.Single(command.Parameters.Cast<object>()));
        Assert.Equal("@amount", parameter.ParameterName);
        Assert.Equal(42, parameter.Value);
        Assert.Equal(DbType.Currency, parameter.DbType);
    }

    [Fact]
    public void AddParameter_CapturesNameValueTypeAndSize()
    {
        using var command = new RecordingDbCommand();

        command.AddParameter(
            "@customer",
            "customer-42",
            ParameterDirection.InputOutput,
            size: 64,
            dbType: DbType.AnsiString);

        var parameter = AssertParameter(command);
        Assert.Equal("@customer", parameter.ParameterName);
        Assert.Equal("customer-42", parameter.Value);
        Assert.Equal(DbType.AnsiString, parameter.DbType);
        Assert.Equal(64, parameter.Size);
        Assert.Equal(ParameterDirection.InputOutput, parameter.Direction);
    }

    [Fact]
    public void AddParameter_MapsNullToDbNull()
    {
        using var command = new RecordingDbCommand();

        command.AddParameter<string>("@optional", null);

        var parameter = AssertParameter(command);
        Assert.Same(DBNull.Value, parameter.Value);
        Assert.Equal(DbType.String, parameter.DbType);
        Assert.Equal(ParameterDirection.Input, parameter.Direction);
    }

    [Fact]
    public void AddParameter_UsesDefaultsWhenOptionalMetadataIsOmitted()
    {
        using var command = new RecordingDbCommand();
        var value = Guid.Parse("a13da96c-8b25-4df6-8248-94f38975af81");

        command.AddParameter("@id", value);

        var parameter = AssertParameter(command);
        Assert.Equal(Guid.Parse("a13da96c-8b25-4df6-8248-94f38975af81"), parameter.Value);
        Assert.Equal(DbType.Guid, parameter.DbType);
        Assert.Equal(ParameterDirection.Input, parameter.Direction);
        Assert.Equal(0, parameter.Size);
    }

    [Fact]
    public void GetValue_ByNameThrowsDataExceptionWhenFieldIsMissing()
    {
        var record = Substitute.For<IDataRecord>();
        record.GetOrdinal("Missing").Returns(_ => throw new IndexOutOfRangeException("Missing"));

        var exception = Assert.Throws<DataException>(
            () => record.GetValue<int>("Missing"));

        Assert.Equal("Field 'Missing' not found in data record.", exception.Message);
        Assert.IsType<IndexOutOfRangeException>(exception.InnerException);
    }

    [Fact]
    public void GetValue_ReturnsUtcDateTime()
    {
        var source = new DateTime(2026, 8, 27, 14, 15, 16, DateTimeKind.Unspecified);
        using var reader = CreateReader(
            ("Required", typeof(DateTime), source),
            ("Optional", typeof(DateTime), DBNull.Value));

        var required = reader.GetDateTimeValue("Required");
        var optional = reader.GetDateTimeValueOrDefault(
            "Optional",
            new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc));

        Assert.Equal(source, required);
        Assert.Equal(DateTimeKind.Utc, required.Kind);
        Assert.Equal(new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc), optional);
    }

    [Fact]
    public void GetValue_ConvertsIntAndLongForOracleCompatibility()
    {
        using var intReader = CreateReader(("Value", typeof(int), 1_234_567));
        using var longReader = CreateReader(("Value", typeof(long), 9_876_543_210L));

        var fromInt = ((IDataRecord)intReader).GetInt64("Value");
        var fromLong = ((IDataRecord)longReader).GetInt64("Value");

        Assert.Equal(1_234_567L, fromInt);
        Assert.Equal(9_876_543_210L, fromLong);
    }

    [Fact]
    public void GetValueOrDefault_ReturnsConfiguredDefaultsForDbNull()
    {
        using var reader = CreateReader(
            ("Name", typeof(string), DBNull.Value),
            ("Count", typeof(int), DBNull.Value));

        var name = reader.GetValueOrDefault("Name", "fallback");
        var count = reader.GetValueOrDefault(1, 17);
        var nullableCount = reader.GetNullableInt32("Count");

        Assert.Equal("fallback", name);
        Assert.Equal(17, count);
        Assert.Null(nullableCount);
    }

    [Fact]
    public async Task GetValueAsync_ReturnsTypedValue()
    {
        using var reader = CreateReader(("MessageId", typeof(long), 4_294_967_300L));

        var byName = await reader.GetValueAsync<long>(
            "MessageId",
            TestContext.Current.CancellationToken);
        var byOrdinal = await reader.GetValueOrDefaultAsync(0, -1L);

        Assert.Equal(4_294_967_300L, byName);
        Assert.Equal(byName, byOrdinal);
    }

    [Fact]
    public async Task GetValueAsync_PropagatesCancellation()
    {
        using var reader = Substitute.For<DbDataReader>();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cancellation.Cancel();
        reader.GetOrdinal("MessageId").Returns(3);
        reader.GetFieldValueAsync<long>(3, cancellation.Token)
            .Returns(Task.FromCanceled<long>(cancellation.Token));

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reader.GetValueAsync<long>("MessageId", cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        reader.Received(1).GetOrdinal("MessageId");
        _ = reader.Received(1).GetFieldValueAsync<long>(3, cancellation.Token);
    }

    [Fact]
    public void ReflectionParameterProvider_MapsMembersAndDbNull()
    {
        using var command = new RecordingDbCommand();
        var values = new ParameterClass
        {
            Tenant = "north",
            Count = 23,
            Optional = null,
        };

        command.ReflectionParameterProvider(
            values,
            new Dictionary<string, string> { [nameof(ParameterClass.Tenant)] = "@tenant_id" });

        var parameters = command.Parameters.Cast<RecordingDbParameter>().ToArray();
        Assert.Collection(
            parameters,
            parameter =>
            {
                Assert.Equal("@tenant_id", parameter.ParameterName);
                Assert.Equal("north", parameter.Value);
                Assert.Equal(DbType.String, parameter.DbType);
            },
            parameter =>
            {
                Assert.Equal(nameof(ParameterClass.Count), parameter.ParameterName);
                Assert.Equal(23, parameter.Value);
                Assert.Equal(DbType.Int32, parameter.DbType);
            },
            parameter =>
            {
                Assert.Equal(nameof(ParameterClass.Optional), parameter.ParameterName);
                Assert.Same(DBNull.Value, parameter.Value);
                Assert.Equal(DbType.String, parameter.DbType);
            });
        Assert.All(parameters, parameter => Assert.Equal(ParameterDirection.Input, parameter.Direction));
    }

    [Fact]
    public void ReflectionParameterProvider_HandlesStructAndClassInputs()
    {
        using var structCommand = new RecordingDbCommand();
        using var classCommand = new RecordingDbCommand();

        structCommand.ReflectionParameterProvider(new ParameterStruct { Sequence = 91L });
        classCommand.ReflectionParameterProvider(new ParameterClass { Tenant = "west", Count = 8 });

        var structParameter = AssertParameter(structCommand);
        Assert.Equal(nameof(ParameterStruct.Sequence), structParameter.ParameterName);
        Assert.Equal(91L, structParameter.Value);
        Assert.Equal(DbType.Int64, structParameter.DbType);
        Assert.Equal(3, classCommand.Parameters.Count);
        Assert.Equal(
            ["west", 8, DBNull.Value],
            classCommand.Parameters.Cast<RecordingDbParameter>().Select(parameter => parameter.Value));
    }

    [Fact]
    public void ReflectionSelector_MapsRecordToClassAndStruct()
    {
        using var classReader = CreateReader(
            (nameof(ClassProjection.Name), typeof(string), "alpha"),
            (nameof(ClassProjection.Count), typeof(int), 12));
        using var structReader = CreateReader(
            (nameof(StructProjection.Id), typeof(Guid), Guid.Parse("035704b9-e17f-421d-a7b5-038527ec74a3")),
            (nameof(StructProjection.Optional), typeof(string), DBNull.Value));

        var classResult = classReader.ReflectionSelector<ClassProjection>();
        var structResult = structReader.ReflectionSelector<StructProjection>();

        Assert.Equal("alpha", classResult.Name);
        Assert.Equal(12, classResult.Count);
        Assert.Equal(Guid.Parse("035704b9-e17f-421d-a7b5-038527ec74a3"), structResult.Id);
        Assert.Null(structResult.Optional);
    }

    [Fact]
    public void ReflectionSelector_ThrowsForInvalidMapping()
    {
        using var reader = CreateReader((nameof(ClassProjection.Count), typeof(string), "not-an-int"));

        var exception = Assert.Throws<ArgumentException>(
            () => reader.ReflectionSelector<CountOnlyProjection>());

        Assert.Contains(typeof(string).FullName!, exception.Message, StringComparison.Ordinal);
        Assert.Contains(typeof(int).FullName!, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetValueOrDefault_ByName_ReturnsTypedValue()
    {
        using var reader = CreateReader(
            ("Decoy", typeof(int), -17),
            ("Attempts", typeof(int), 42));

        var actual = reader.GetValueOrDefault("Attempts", -1);

        Assert.Equal(42, actual);
        Assert.Equal(1, reader.GetOrdinal("Attempts"));
    }

    [Fact]
    public void GetValueOrDefault_ByName_ThrowsDataExceptionWhenFieldIsMissing()
    {
        const string providerMessage = "Provider could not find column 'RetryCount'.";
        var record = Substitute.For<IDataRecord>();
        record.GetOrdinal("RetryCount").Returns(_ => throw new IndexOutOfRangeException(providerMessage));

        var exception = Assert.Throws<DataException>(
            () => record.GetValueOrDefault("RetryCount", -1));

        Assert.Equal("Field 'RetryCount' not found in data record.", exception.Message);
        var innerException = Assert.IsType<IndexOutOfRangeException>(exception.InnerException);
        Assert.Equal(providerMessage, innerException.Message);
        record.Received(1).GetOrdinal("RetryCount");
    }

    [Fact]
    public void GetDateTimeValueOrDefault_ThrowsDataExceptionWhenFieldIsMissing()
    {
        const string providerMessage = "Provider could not find column 'CreatedUtc'.";
        var record = Substitute.For<IDataRecord>();
        record.GetOrdinal("CreatedUtc").Returns(_ => throw new IndexOutOfRangeException(providerMessage));

        var exception = Assert.Throws<DataException>(
            () => record.GetDateTimeValueOrDefault("CreatedUtc"));

        Assert.Equal("Field 'CreatedUtc' not found in data record.", exception.Message);
        var innerException = Assert.IsType<IndexOutOfRangeException>(exception.InnerException);
        Assert.Equal(providerMessage, innerException.Message);
        record.Received(1).GetOrdinal("CreatedUtc");
    }

    [Fact]
    public async Task GetValueOrDefaultAsync_ByName_ReturnsTypedValue()
    {
        var expected = Guid.Parse("a1937343-6067-444a-b4ec-9c92364e0ed4");
        using var reader = CreateReader(
            ("Decoy", typeof(Guid), Guid.Parse("d4580117-318d-4052-83d0-2f6dc427f62f")),
            ("CorrelationId", typeof(Guid), expected));

        var actual = await reader.GetValueOrDefaultAsync("CorrelationId", Guid.Empty);

        Assert.Equal(expected, actual);
        Assert.Equal(1, reader.GetOrdinal("CorrelationId"));
    }

    [Fact]
    public async Task GetValueOrDefaultAsync_ByName_ReturnsDefaultForDbNull()
    {
        using var reader = CreateReader(
            ("Decoy", typeof(string), "not-selected"),
            ("DisplayName", typeof(string), DBNull.Value));

        var actual = await reader.GetValueOrDefaultAsync("DisplayName", "anonymous");

        Assert.Equal("anonymous", actual);
        Assert.True(reader.IsDBNull(reader.GetOrdinal("DisplayName")));
    }

    [Fact]
    public async Task GetValueOrDefaultAsync_ByName_ThrowsDataExceptionWhenFieldIsMissing()
    {
        const string providerMessage = "Provider could not find column 'SequenceNumber'.";
        using var reader = Substitute.For<DbDataReader>();
        reader.GetOrdinal("SequenceNumber").Returns(_ => throw new IndexOutOfRangeException(providerMessage));

        var exception = await Assert.ThrowsAsync<DataException>(
            () => reader.GetValueOrDefaultAsync("SequenceNumber", -1L));

        Assert.Equal("Field 'SequenceNumber' not found in data record.", exception.Message);
        var innerException = Assert.IsType<IndexOutOfRangeException>(exception.InnerException);
        Assert.Equal(providerMessage, innerException.Message);
        reader.Received(1).GetOrdinal("SequenceNumber");
    }

    [Fact]
    public void GetValueOrDefault_ByOrdinal_ReturnsTypedValue()
    {
        using var reader = CreateReader(
            ("Decoy", typeof(string), "not-selected"),
            ("Region", typeof(string), "northwest"));

        var actual = reader.GetValueOrDefault(1, "unknown");

        Assert.Equal("northwest", actual);
        Assert.Equal("Region", reader.GetName(1));
    }

    [Fact]
    public void GetDateTimeValue_ThrowsDataExceptionWhenFieldIsMissing()
    {
        const string providerMessage = "Provider could not find column 'ModifiedUtc'.";
        var record = Substitute.For<IDataRecord>();
        record.GetOrdinal("ModifiedUtc").Returns(_ => throw new IndexOutOfRangeException(providerMessage));

        var exception = Assert.Throws<DataException>(
            () => record.GetDateTimeValue("ModifiedUtc"));

        Assert.Equal("Field 'ModifiedUtc' not found in data record.", exception.Message);
        var innerException = Assert.IsType<IndexOutOfRangeException>(exception.InnerException);
        Assert.Equal(providerMessage, innerException.Message);
        record.Received(1).GetOrdinal("ModifiedUtc");
    }

    [Fact]
    public void GetInt32_ThrowsDataExceptionWhenFieldIsMissing()
    {
        const string providerMessage = "Provider could not find column 'AttemptCount'.";
        var record = Substitute.For<IDataRecord>();
        record.GetOrdinal("AttemptCount").Returns(_ => throw new IndexOutOfRangeException(providerMessage));

        var exception = Assert.Throws<DataException>(
            () => record.GetInt32("AttemptCount"));

        Assert.Equal("Field 'AttemptCount' not found in data record.", exception.Message);
        var innerException = Assert.IsType<IndexOutOfRangeException>(exception.InnerException);
        Assert.Equal(providerMessage, innerException.Message);
        record.Received(1).GetOrdinal("AttemptCount");
    }

    [Fact]
    public void GetInt64_ThrowsDataExceptionWhenFieldIsMissing()
    {
        const string providerMessage = "Provider could not find column 'MessageId'.";
        var record = Substitute.For<IDataRecord>();
        record.GetOrdinal("MessageId").Returns(_ => throw new IndexOutOfRangeException(providerMessage));

        var exception = Assert.Throws<DataException>(
            () => record.GetInt64("MessageId"));

        Assert.Equal("Field 'MessageId' not found in data record.", exception.Message);
        var innerException = Assert.IsType<IndexOutOfRangeException>(exception.InnerException);
        Assert.Equal(providerMessage, innerException.Message);
        record.Received(1).GetOrdinal("MessageId");
    }

    [Fact]
    public void GetNullableInt32_ConvertsNonNullValue()
    {
        using var reader = CreateReader(
            ("Decoy", typeof(short), (short)-9),
            ("Priority", typeof(short), (short)32123));

        var actual = reader.GetNullableInt32("Priority");

        Assert.Equal(32123, actual);
        Assert.Equal(typeof(short), reader.GetFieldType(reader.GetOrdinal("Priority")));
    }

    [Fact]
    public void GetNullableInt32_ThrowsDataExceptionWhenFieldIsMissing()
    {
        const string providerMessage = "Provider could not find column 'Priority'.";
        var record = Substitute.For<IDataRecord>();
        record.GetOrdinal("Priority").Returns(_ => throw new IndexOutOfRangeException(providerMessage));

        var exception = Assert.Throws<DataException>(
            () => record.GetNullableInt32("Priority"));

        Assert.Equal("Field 'Priority' not found in data record.", exception.Message);
        var innerException = Assert.IsType<IndexOutOfRangeException>(exception.InnerException);
        Assert.Equal(providerMessage, innerException.Message);
        record.Received(1).GetOrdinal("Priority");
    }

    [Fact]
    public async Task GetValueAsync_ThrowsDataExceptionWhenFieldIsMissing()
    {
        const string providerMessage = "Provider could not find column 'Payload'.";
        using var reader = Substitute.For<DbDataReader>();
        reader.GetOrdinal("Payload").Returns(_ => throw new IndexOutOfRangeException(providerMessage));

        var exception = await Assert.ThrowsAsync<DataException>(
            () => reader.GetValueAsync<byte[]>("Payload", TestContext.Current.CancellationToken));

        Assert.Equal("Field 'Payload' not found in data record.", exception.Message);
        var innerException = Assert.IsType<IndexOutOfRangeException>(exception.InnerException);
        Assert.Equal(providerMessage, innerException.Message);
        reader.Received(1).GetOrdinal("Payload");
    }

    private static RecordingDbParameter AssertParameter(RecordingDbCommand command) =>
        Assert.IsType<RecordingDbParameter>(Assert.Single(command.Parameters.Cast<object>()));

    private static DataTableReader CreateReader(
        params (string Name, Type Type, object Value)[] columns)
    {
        var table = new DataTable();
        foreach (var column in columns)
        {
            table.Columns.Add(column.Name, column.Type);
        }

        table.Rows.Add(columns.Select(column => column.Value).ToArray());
        var reader = table.CreateDataReader();
        Assert.True(reader.Read());
        return reader;
    }

    private sealed class ParameterClass
    {
        public string Tenant { get; init; } = string.Empty;

        public int Count { get; init; }

        public string? Optional { get; init; }
    }

    private struct ParameterStruct
    {
        public long Sequence { get; init; }
    }

    private sealed class ClassProjection
    {
        public string Name { get; set; } = string.Empty;

        public int Count { get; set; }
    }

    private struct StructProjection
    {
        public Guid Id { get; set; }

        public string? Optional { get; set; }
    }

    private sealed class CountOnlyProjection
    {
        public int Count { get; set; }
    }
}
