using System.Data;
using System.Data.Common;
using Orleans.Persistence.AdoNet.Storage;
using UnitTests.StorageTests.Relational.Fakes;

namespace UnitTests.StorageTests.Relational;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Persistence")]
public sealed class RelationalStorageExtensionsTests
{
    [Fact]
    public async Task ReadAsync_DelegatesSqlParametersAndSelector()
    {
        const string Sql = "SELECT Value FROM Sample WHERE Tenant = @tenant";
        var storage = new ScriptedRelationalStorage().ExpectRead(
            Sql,
            CreateTable(("Value", typeof(int), 42)));

        var results = await storage.ReadAsync(
            Sql,
            record => record.GetInt32(0) * 2,
            command => command.AddParameter(
                "@tenant",
                "north",
                size: 32,
                dbType: DbType.AnsiString));

        Assert.Equal([84], results);
        var call = Assert.Single(storage.Calls);
        Assert.Equal(Sql, call.Query);
        Assert.Equal(CommandBehavior.Default, call.CommandBehavior);
        var parameter = AssertParameter(call);
        Assert.Equal("@tenant", parameter.ParameterName);
        Assert.Equal("north", parameter.Value);
        Assert.Equal(DbType.AnsiString, parameter.DbType);
        Assert.Equal(32, parameter.Size);
        storage.VerifyComplete();
    }

    [Fact]
    public async Task ReadAsync_FlowsAcrossResultSetsInOrder()
    {
        const string Sql = "SELECT 10 AS Value; SELECT 30 AS Value;";
        var storage = new ScriptedRelationalStorage().ExpectRead(
            Sql,
            CreateTable(("Value", typeof(int), 10), ("Value", typeof(int), 20)),
            CreateTable(("Value", typeof(int), 30)));

        var results = await storage.ReadAsync(Sql, record => record.GetInt32(0), parameterProvider: null);

        Assert.Equal([10, 20, 30], results);
        Assert.Single(storage.Calls);
        storage.VerifyComplete();
    }

    [Fact]
    public async Task ReadAsync_UsesReflectionParameterProvider()
    {
        const string Sql = "SELECT Name, Count FROM Sample WHERE Tenant = @Tenant AND Optional = @Optional";
        var storage = new ScriptedRelationalStorage().ExpectRead(
            Sql,
            CreateTable(
                (nameof(ReadProjection.Name), typeof(string), "record-a"),
                (nameof(ReadProjection.Count), typeof(int), 7)));

        var results = await storage.ReadAsync<ReadProjection>(
            Sql,
            new { Tenant = "east", Optional = (string?)null },
            TestContext.Current.CancellationToken);

        var result = Assert.Single(results);
        Assert.Equal("record-a", result.Name);
        Assert.Equal(7, result.Count);
        var parameters = Assert.Single(storage.Calls).Command.Parameters.Cast<RecordingDbParameter>().ToArray();
        Assert.Collection(
            parameters,
            parameter =>
            {
                Assert.Equal("Tenant", parameter.ParameterName);
                Assert.Equal("east", parameter.Value);
                Assert.Equal(DbType.String, parameter.DbType);
            },
            parameter =>
            {
                Assert.Equal("Optional", parameter.ParameterName);
                Assert.Same(DBNull.Value, parameter.Value);
                Assert.Equal(DbType.String, parameter.DbType);
            });
        storage.VerifyComplete();
    }

    [Fact]
    public async Task ReadAsync_ReturnsEmptyCollectionWhenNoRowsExist()
    {
        const string Sql = "SELECT Value FROM EmptySample";
        var storage = new ScriptedRelationalStorage().ExpectRead(Sql);

        var results = await storage.ReadAsync<int>(Sql, TestContext.Current.CancellationToken);

        Assert.Empty(results);
        Assert.Empty(Assert.Single(storage.Calls).Command.Parameters.Cast<object>());
        storage.VerifyComplete();
    }

    [Fact]
    public async Task ReadAsync_RejectsNullSelector()
    {
        const string Sql = "SELECT Value FROM Sample";
        var storage = new ScriptedRelationalStorage();

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => storage.ReadAsync(Sql, (Func<IDataRecord, int>)null!, parameterProvider: null));

        Assert.Equal("selector", exception.ParamName);
        Assert.Empty(storage.Calls);
    }

    [Fact]
    public async Task ReadAsync_PropagatesStorageException()
    {
        const string Sql = "SELECT Value FROM BrokenSample";
        var expected = new DataException("scripted read failure");
        var storage = new ScriptedRelationalStorage().ExpectReadException(Sql, expected);

        var actual = await Assert.ThrowsAsync<DataException>(
            () => storage.ReadAsync<int>(Sql, TestContext.Current.CancellationToken));

        Assert.Same(expected, actual);
        Assert.Single(storage.Calls);
        storage.VerifyComplete();
    }

    [Fact]
    public async Task ReadAsync_PropagatesCancellation()
    {
        const string Sql = "SELECT Value FROM SlowSample";
        var storage = new ScriptedRelationalStorage().ExpectRead(Sql);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => storage.ReadAsync<int>(Sql, cancellation.Token));

        var call = Assert.Single(storage.Calls);
        Assert.Equal(cancellation.Token, call.CancellationToken);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        storage.VerifyComplete();
    }

    [Fact]
    public async Task ExecuteAsync_DelegatesSqlParametersAndReturnsAffectedRows()
    {
        const string Sql = "UPDATE Sample SET Value = @Value WHERE Id = @Id";
        var storage = new ScriptedRelationalStorage().ExpectExecute(Sql, 3);

        var affectedRows = await storage.ExecuteAsync(
            Sql,
            new { Value = "updated", Id = 17 },
            TestContext.Current.CancellationToken);

        Assert.Equal(3, affectedRows);
        var call = Assert.Single(storage.Calls);
        Assert.Equal(Sql, call.Query);
        Assert.Equal(CommandBehavior.Default, call.CommandBehavior);
        Assert.Equal(TestContext.Current.CancellationToken, call.CancellationToken);
        Assert.Equal(
            ["updated", 17],
            call.Command.Parameters.Cast<RecordingDbParameter>().Select(parameter => parameter.Value));
        storage.VerifyComplete();
    }

    [Fact]
    public async Task ExecuteAsync_UsesReflectionParameterProvider()
    {
        const string Sql = "DELETE FROM Sample WHERE Tenant = @Tenant AND Sequence = @Sequence";
        var storage = new ScriptedRelationalStorage().ExpectExecute(Sql, 1);

        var affectedRows = await storage.ExecuteAsync(
            Sql,
            new ExecuteParameters { Tenant = "south", Sequence = 99L },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, affectedRows);
        var parameters = Assert.Single(storage.Calls).Command.Parameters.Cast<RecordingDbParameter>().ToArray();
        Assert.Collection(
            parameters,
            parameter =>
            {
                Assert.Equal(nameof(ExecuteParameters.Tenant), parameter.ParameterName);
                Assert.Equal("south", parameter.Value);
                Assert.Equal(DbType.String, parameter.DbType);
            },
            parameter =>
            {
                Assert.Equal(nameof(ExecuteParameters.Sequence), parameter.ParameterName);
                Assert.Equal(99L, parameter.Value);
                Assert.Equal(DbType.Int64, parameter.DbType);
            });
        storage.VerifyComplete();
    }

    [Fact]
    public async Task ExecuteAsync_MapsNullParametersToDbNull()
    {
        const string Sql = "UPDATE Sample SET Optional = @Optional";
        var storage = new ScriptedRelationalStorage().ExpectExecute(Sql, 5);

        var affectedRows = await storage.ExecuteAsync(
            Sql,
            new { Optional = (byte[]?)null },
            TestContext.Current.CancellationToken);

        Assert.Equal(5, affectedRows);
        var parameter = AssertParameter(Assert.Single(storage.Calls));
        Assert.Equal("Optional", parameter.ParameterName);
        Assert.Same(DBNull.Value, parameter.Value);
        Assert.Equal(DbType.Binary, parameter.DbType);
        storage.VerifyComplete();
    }

    [Fact]
    public async Task ExecuteAsync_PropagatesStorageException()
    {
        const string Sql = "DELETE FROM BrokenSample";
        var expected = new InvalidOperationException("scripted execute failure");
        var storage = new ScriptedRelationalStorage().ExpectExecuteException(Sql, expected);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.ExecuteAsync(Sql, TestContext.Current.CancellationToken));

        Assert.Same(expected, actual);
        Assert.Single(storage.Calls);
        storage.VerifyComplete();
    }

    [Fact]
    public async Task ExecuteAsync_PropagatesCancellation()
    {
        const string Sql = "DELETE FROM SlowSample";
        var storage = new ScriptedRelationalStorage().ExpectExecute(Sql, 2);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => storage.ExecuteAsync(Sql, cancellation.Token));

        var call = Assert.Single(storage.Calls);
        Assert.Equal(cancellation.Token, call.CancellationToken);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        storage.VerifyComplete();
    }

    [Fact]
    public async Task GetStream_UsesNativeProviderStream()
    {
        var payload = new byte[] { 1, 3, 5, 7, 9 };
        using var reader = CreateTable(("Payload", typeof(byte[]), payload)).CreateDataReader();
        Assert.True(reader.Read());
        var storage = new ScriptedRelationalStorage(AdoNetInvariants.InvariantNameSqlServer);

        await using var stream = reader.GetStream(0, storage);

        Assert.IsNotType<OrleansRelationalDownloadStream>(stream);
        Assert.Equal(payload, await ReadAllBytesAsync(stream));
    }

    [Fact]
    public async Task GetStream_FallsBackToChunkedProviderStream()
    {
        var payload = Enumerable.Range(0, 4_500).Select(value => (byte)(value % 251)).ToArray();
        using var reader = CreateTable(("Payload", typeof(byte[]), payload)).CreateDataReader();
        Assert.True(reader.Read());
        var storage = new ScriptedRelationalStorage(AdoNetInvariants.InvariantNameSqlLite);

        await using var stream = reader.GetStream(0, storage);

        Assert.IsType<OrleansRelationalDownloadStream>(stream);
        Assert.Equal(payload, await ReadAllBytesAsync(stream));
    }

    private static RecordingDbParameter AssertParameter(RecordedStorageCall call) =>
        Assert.IsType<RecordingDbParameter>(Assert.Single(call.Command.Parameters.Cast<object>()));

    private static DataTable CreateTable(
        params (string Name, Type Type, object Value)[] values)
    {
        var table = new DataTable();
        foreach (var group in values.GroupBy(value => value.Name))
        {
            table.Columns.Add(group.Key, group.First().Type);
        }

        foreach (var row in values.Chunk(table.Columns.Count))
        {
            table.Rows.Add(row.Select(value => value.Value).ToArray());
        }

        return table;
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream)
    {
        using var destination = new MemoryStream();
        await stream.CopyToAsync(destination, TestContext.Current.CancellationToken);
        return destination.ToArray();
    }

    private sealed class ReadProjection
    {
        public string Name { get; set; } = string.Empty;

        public int Count { get; set; }
    }

    private sealed class ExecuteParameters
    {
        public string Tenant { get; init; } = string.Empty;

        public long Sequence { get; init; }
    }
}
