namespace Orleans.AdoNet.Core;

internal abstract class RelationalOrleansQueries<TQueries>
    where TQueries: DbStoredQueries
{
    /// <summary>
    /// the underlying storage
    /// </summary>
    protected IRelationalStorage Storage { get; }

    /// <summary>
    /// When inserting statistics and generating a batch insert clause, these are the columns in the statistics
    /// table that will be updated with multiple values. The other ones are updated with one value only.
    /// </summary>
    private static readonly string[] InsertStatisticsMultiUpdateColumns = {
            DbStoredQueries.Columns.IsValueDelta,
            DbStoredQueries.Columns.StatValue,
            DbStoredQueries.Columns.Statistic
        };

    /// <summary>
    /// the orleans functional queries
    /// </summary>
    protected TQueries Queries { get; }

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="storage">the underlying relational storage</param>
    /// <param name="queries">Orleans functional queries</param>
    private protected RelationalOrleansQueries(IRelationalStorage storage, TQueries queries)
    {
        Storage = storage;
        Queries = queries;
    }

    /* todo: move this into a factory
    /// <summary>
    /// Creates an instance of a database of type <see cref="RelationalOrleansQueries"/> and Initializes Orleans queries from the database.
    /// Orleans uses only these queries and the variables therein, nothing more.
    /// </summary>
    /// <param name="invariantName">The invariant name of the connector for this database.</param>
    /// <param name="connectionString">The connection string this database should use for database operations.</param>
    internal static async Task<RelationalOrleansQueries> CreateInstance(string invariantName, string connectionString)
    {
        var storage = RelationalStorage.CreateInstance(invariantName, connectionString);

        var queries = await storage.ReadAsync(DbStoredQueries.GetQueriesKey, DbStoredQueries.Converters.GetQueryKeyAndValue, null);

        return new RelationalOrleansQueries(storage, new DbStoredQueries(queries.ToDictionary(q => q.Key, q => q.Value)));
    }
    */

    protected Task<int> ExecuteAsync(string query, Func<IDbCommand, DbStoredQueries.Columns> parameterProvider) => Storage.ExecuteAsync(query, command => parameterProvider(command));

    protected async Task<TAggregate> ReadAsync<TResult, TAggregate>(string query,
        Func<IDataRecord, TResult> selector,
        Func<IDbCommand, DbStoredQueries.Columns> parameterProvider,
        Func<IEnumerable<TResult>, TAggregate> aggregator)
    {
        var ret = await Storage.ReadAsync(query, selector, command => parameterProvider(command));
        return aggregator(ret);
    }
}
