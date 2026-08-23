using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
#if TRANSACTIONS_ADONET
using Orleans.Storage;
#endif

#if CLUSTERING_ADONET
namespace Orleans.Clustering.AdoNet.Storage
#elif PERSISTENCE_ADONET
namespace Orleans.Persistence.AdoNet.Storage
#elif REMINDERS_ADONET
namespace Orleans.Reminders.AdoNet.Storage
#elif STREAMING_ADONET
namespace Orleans.Streaming.AdoNet.Storage
#elif GRAINDIRECTORY_ADONET
namespace Orleans.GrainDirectory.AdoNet.Storage
#elif TRANSACTIONS_ADONET
namespace Orleans.Transactions.AdoNet.Storage
#elif TESTER_SQLUTILS
namespace Orleans.Tests.SqlUtils
#else
// No default namespace intentionally to cause compile errors if something is not defined
#endif
{
    /// <summary>
    /// A general purpose class to work with a given relational database and ADO.NET provider.
    /// </summary>
    [DebuggerDisplay("InvariantName = {InvariantName}, ConnectionString = {ConnectionString}")]
    internal class RelationalStorage : IRelationalStorage
    {
#if TRANSACTIONS_ADONET
        internal const string RollbackExceptionDataKey = "Orleans.RelationalStorage.RollbackException";
#endif

        /// <summary>
        /// The connection string to use.
        /// </summary>
        private readonly string _connectionString;

        /// <summary>
        /// The optional data source used to open connections.
        /// </summary>
        private readonly DbDataSource? _dataSource;

        /// <summary>
        /// The invariant name of the connector for this database.
        /// </summary>
        private readonly string _invariantName;

        /// <summary>
        /// If the ADO.NET provider of this storage supports cancellation or not. This
        /// capability is queried and the result is cached here.
        /// </summary>
        private readonly bool _supportsCommandCancellation;

        /// <summary>
        /// If the underlying ADO.NET implementation is natively asynchronous
        /// (the ADO.NET Db*.XXXAsync classes are overridden) or not.
        /// </summary>
        private readonly bool _isSynchronousAdoNetImplementation;

        /// <summary>
        /// Command interceptor for the given data provider.
        /// </summary>
        private readonly ICommandInterceptor _databaseCommandInterceptor;

        /// <summary>
        /// The invariant name of the connector for this database.
        /// </summary>
        public string InvariantName
        {
            get
            {
                return _invariantName;
            }
        }


        /// <summary>
        /// The connection string used to connect to the database.
        /// </summary>
        public string ConnectionString
        {
            get
            {
                return _connectionString;
            }
        }


        /// <summary>
        /// Creates an instance of a database of type <see cref="IRelationalStorage"/>.
        /// </summary>
        /// <param name="invariantName">The invariant name of the connector for this database.</param>
        /// <param name="connectionString">The connection string this database should use for database operations.</param>
        /// <returns></returns>
        public static IRelationalStorage CreateInstance(string invariantName, string connectionString)
        {
            if (string.IsNullOrWhiteSpace(invariantName))
            {
                throw new ArgumentException("The name of invariant must contain characters", nameof(invariantName));
            }

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ArgumentException("Connection string must contain characters", nameof(connectionString));
            }

            return new RelationalStorage(invariantName, connectionString);
        }

        /// <summary>
        /// Creates an instance of a database of type <see cref="IRelationalStorage"/>.
        /// </summary>
        /// <param name="invariantName">The invariant name of the connector for this database.</param>
        /// <param name="dataSource">The data source used to open database connections.</param>
        /// <returns>A relational storage instance.</returns>
        public static IRelationalStorage CreateInstance(string invariantName, DbDataSource dataSource)
        {
            if (string.IsNullOrWhiteSpace(invariantName))
            {
                throw new ArgumentException("The name of invariant must contain characters", nameof(invariantName));
            }

            ArgumentNullException.ThrowIfNull(dataSource);
            DbConnectionFactory.ValidateDataSource(invariantName, dataSource);
            return new RelationalStorage(invariantName, dataSource);
        }

        /// <summary>
        /// Creates an instance using exactly one configured connection source.
        /// </summary>
        public static IRelationalStorage CreateInstance(string invariantName, string? connectionString, DbDataSource? dataSource)
        {
            if (string.IsNullOrWhiteSpace(connectionString) == (dataSource is null))
            {
                throw new ArgumentException($"Configure exactly one of {nameof(connectionString)} or {nameof(dataSource)}.");
            }

            return dataSource is null
                ? CreateInstance(invariantName, connectionString!)
                : CreateInstance(invariantName, dataSource);
        }


        /// <summary>
        /// Executes a given statement. Especially intended to use with <em>SELECT</em> statement.
        /// </summary>
        /// <typeparam name="TResult">The result type.</typeparam>
        /// <param name="query">Executes a given statement. Especially intended to use with <em>SELECT</em> statement.</param>
        /// <param name="parameterProvider">Adds parameters to the query. Parameter names must match those defined in the query.</param>
        /// <param name="selector">This function transforms the raw <see cref="IDataRecord"/> results to type <see paramref="TResult"/> the <see cref="int"/> parameter being the resultset number.</param>
        /// <param name="commandBehavior">The command behavior that should be used. Defaults to <see cref="CommandBehavior.Default"/>.</param>
        /// <param name="cancellationToken">The cancellation token. Defaults to <see cref="CancellationToken.None"/>.</param>
        /// <returns>A list of objects as a result of the <see paramref="query"/>.</returns>
        /// <example>This sample shows how to make a hand-tuned database call.
        /// <code>
        /// //This struct holds the return value in this example.
        /// public struct Information
        /// {
        ///     public string TABLE_CATALOG { get; set; }
        ///     public string TABLE_NAME { get; set; }
        /// }
        ///
        /// //Here are defined two queries. There can be more than two queries, in which case
        /// //the result sets are differentiated by a count parameter. Here the queries are
        /// //SELECT clauses, but they can be whatever, even mixed ones.
        /// IEnumerable&lt;Information&gt; ret =
        ///     await storage.ReadAsync&lt;Information&gt;("SELECT * FROM INFORMATION_SCHEMA.TABLES; SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = @tp1", command =>
        /// {
        ///     //Parameters are added and created like this.
        ///     //They are database vendor agnostic.
        ///     var tp1 = command.CreateParameter();
        ///     tp1.ParameterName = "tp1";
        ///     tp1.Value = "some test value";
        ///     tp1.DbType = DbType.String;
        ///     tp1.Direction = ParameterDirection.Input;
        ///     command.Parameters.Add(tp1);
        ///
        ///     //The selector is used to select the results within the result set. In this case there are two homogenous
        ///     //result sets, so there is actually no need to check which result set the selector holds and it could
        ///     //marked with by convention by underscore (_).
        /// }, (selector, resultSetCount) =>
        ///    {
        ///        //This function is called once for each row returned, so the final result will be an
        ///        //IEnumerable&lt;Information&gt;.
        ///        return new Information
        ///        {
        ///            TABLE_CATALOG = selector.GetValueOrDefault&lt;string&gt;("TABLE_CATALOG"),
        ///            TABLE_NAME = selector.GetValueOrDefault&lt;string&gt;("TABLE_NAME")
        ///        }
        ///}).ConfigureAwait(continueOnCapturedContext: false);
        /// </code>
        /// </example>
        public async Task<IEnumerable<TResult>> ReadAsync<TResult>(string query, Action<IDbCommand>? parameterProvider, Func<IDataRecord, int, CancellationToken, Task<TResult>> selector, CommandBehavior commandBehavior = CommandBehavior.Default, CancellationToken cancellationToken = default)
        {
            //If the query is something else that is not acceptable (e.g. an empty string), there will an appropriate database exception.
            if (query == null)
            {
                throw new ArgumentNullException(nameof(query));
            }

            if (selector == null)
            {
                throw new ArgumentNullException(nameof(selector));
            }

            return (await ExecuteAsync(query, parameterProvider, ExecuteReaderAsync, selector, commandBehavior, cancellationToken).ConfigureAwait(false)).Item1;
        }

#if TRANSACTIONS_ADONET
        public async Task<(IReadOnlyList<TFirst> First, IReadOnlyList<TSecond> Second)> ReadTransactionAsync<TFirst, TSecond>(
            string firstQuery,
            Action<IDbCommand>? firstParameterProvider,
            Func<IDataRecord, TFirst> firstSelector,
            string secondQuery,
            Action<IDbCommand>? secondParameterProvider,
            Func<IDataRecord, TSecond> secondSelector,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(firstQuery);
            ArgumentNullException.ThrowIfNull(firstSelector);
            ArgumentNullException.ThrowIfNull(secondQuery);
            ArgumentNullException.ThrowIfNull(secondSelector);

            using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
            try
            {
                var first = await ReadInTransactionAsync(
                    connection,
                    transaction,
                    firstQuery,
                    firstParameterProvider,
                    firstSelector,
                    cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
                var second = await ReadInTransactionAsync(
                    connection,
                    transaction,
                    secondQuery,
                    secondParameterProvider,
                    secondSelector,
                    cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
                return (first, second);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
                throw;
            }
        }

        private async Task<IReadOnlyList<TResult>> ReadInTransactionAsync<TResult>(
            DbConnection connection,
            DbTransaction transaction,
            string query,
            Action<IDbCommand>? parameterProvider,
            Func<IDataRecord, TResult> selector,
            CancellationToken cancellationToken)
        {
            using var command = connection.CreateCommand();
            parameterProvider?.Invoke(command);
            command.CommandText = query;
            command.Transaction = transaction;
            _databaseCommandInterceptor.Intercept(command);

            var operation = _isSynchronousAdoNetImplementation
                ? Task.Run(
                    () => ExecuteReaderAsync(
                        command,
                        (record, _, _) => Task.FromResult(selector(record)),
                        CommandBehavior.Default,
                        cancellationToken),
                    cancellationToken)
                : ExecuteReaderAsync(
                    command,
                    (record, _, _) => Task.FromResult(selector(record)),
                    CommandBehavior.Default,
                    cancellationToken);

            var result = await operation.ConfigureAwait(continueOnCapturedContext: false);
            return result.Item1.ToList();
        }
#endif


        /// <summary>
        /// Executes a given statement. Especially intended to use with <em>INSERT</em>, <em>UPDATE</em>, <em>DELETE</em> or <em>DDL</em> queries.
        /// </summary>
        /// <param name="query">The query to execute.</param>
        /// <param name="parameterProvider">Adds parameters to the query. Parameter names must match those defined in the query.</param>
        /// <param name="commandBehavior">The command behavior that should be used. Defaults to <see cref="CommandBehavior.Default"/>.</param>
        /// <param name="cancellationToken">The cancellation token. Defaults to <see cref="CancellationToken.None"/>.</param>
        /// <returns>Affected rows count.</returns>
        /// <example>This sample shows how to make a hand-tuned database call.
        /// <code>
        /// //In contract to reading, execute queries are simpler as they return only
        /// //the affected rows count if it is available.
        /// var query = ""IF NOT EXISTS(SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'Test') CREATE TABLE Test(Id INT PRIMARY KEY IDENTITY(1, 1) NOT NULL);"
        /// int affectedRowsCount = await storage.ExecuteAsync(query, command =>
        /// {
        ///     //There aren't parameters here, but they'd be added like when reading.
        ///     //As the affected rows count is the only thing returned, there isn't
        ///     //facilities to read anything.
        /// }).ConfigureAwait(continueOnCapturedContext: false);
        /// </code>
        /// </example>
        public async Task<int> ExecuteAsync(string query, Action<IDbCommand>? parameterProvider, CommandBehavior commandBehavior = CommandBehavior.Default, CancellationToken cancellationToken = default)
        {
            //If the query is something else that is not acceptable (e.g. an empty string), there will an appropriate database exception.
            if (query == null)
            {
                throw new ArgumentNullException(nameof(query));
            }

            return (await ExecuteAsync(query, parameterProvider, ExecuteReaderAsync, (unit, id, c) => Task.FromResult(unit), commandBehavior, cancellationToken).ConfigureAwait(false)).Item2;
        }

#if TRANSACTIONS_ADONET
        /// <summary>
        /// Executes a given statement. Especially intended to use with <em>INSERT</em>, <em>UPDATE</em>, <em>DELETE</em> or <em>DDL</em> queries with transaction
        /// </summary>
        /// <param name="multipleQuery"></param>
        /// <param name="currentETag">The ETag held by the current activation, used to describe optimistic concurrency conflicts.</param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        public async Task<int> ExecuteTransactionAsync(
            List<Tuple<string, Action<DbCommand>>> multipleQuery,
            string? currentETag,
            CancellationToken cancellationToken = default)
        {
            //If the query is something else that is not acceptable (e.g. an empty string), there will an appropriate database exception.
            if (multipleQuery == null)
            {
                throw new ArgumentNullException(nameof(multipleQuery));
            }

            return await ExecuteTransactionCoreAsync(multipleQuery, currentETag, cancellationToken).ConfigureAwait(false);
        }
#endif

        /// <summary>
        /// Creates an instance of a database of type <see cref="RelationalStorage"/>.
        /// </summary>
        /// <param name="invariantName">The invariant name of the connector for this database.</param>
        /// <param name="connectionString">The connection string this database should use for database operations.</param>
        private RelationalStorage(string invariantName, string connectionString)
        {
            this._connectionString = connectionString;
            this._invariantName = invariantName;
            _supportsCommandCancellation = DbConstantsStore.SupportsCommandCancellation(InvariantName);
            _isSynchronousAdoNetImplementation = DbConstantsStore.IsSynchronousAdoNetImplementation(InvariantName);
            this._databaseCommandInterceptor = DbConstantsStore.GetDatabaseCommandInterceptor(InvariantName);
        }

        private RelationalStorage(string invariantName, DbDataSource dataSource)
        {
            _connectionString = dataSource.ConnectionString;
            _dataSource = dataSource;
            _invariantName = invariantName;
            _supportsCommandCancellation = DbConstantsStore.SupportsCommandCancellation(InvariantName);
            _isSynchronousAdoNetImplementation = DbConstantsStore.IsSynchronousAdoNetImplementation(InvariantName);
            _databaseCommandInterceptor = DbConstantsStore.GetDatabaseCommandInterceptor(InvariantName);
        }

        private static async Task<Tuple<IEnumerable<TResult>, int>> SelectAsync<TResult>(DbDataReader reader, Func<IDataReader, int, CancellationToken, Task<TResult>> selector, CancellationToken cancellationToken)
        {
            var results = new List<TResult>();
            var resultSetCount = 0;

            do
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false))
                {
                    var obj = await selector(reader, resultSetCount, cancellationToken).ConfigureAwait(false);
                    results.Add(obj);
                }

                ++resultSetCount;

            } while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));

            return Tuple.Create(results.AsEnumerable(), reader.RecordsAffected);
        }

        private async Task<Tuple<IEnumerable<TResult>, int>> ExecuteReaderAsync<TResult>(DbCommand command, Func<IDataRecord, int, CancellationToken, Task<TResult>> selector, CommandBehavior commandBehavior, CancellationToken cancellationToken)
        {
            using (var reader = await command.ExecuteReaderAsync(commandBehavior, cancellationToken).ConfigureAwait(continueOnCapturedContext: false))
            {
                CancellationTokenRegistration cancellationRegistration = default;
                try
                {
                    if (cancellationToken.CanBeCanceled && _supportsCommandCancellation)
                    {
                        cancellationRegistration = cancellationToken.Register(CommandCancellation, Tuple.Create(reader, command), useSynchronizationContext: false);
                    }
                    return await SelectAsync(reader, selector, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
                }
                finally
                {
                    cancellationRegistration.Dispose();
                }
            }
        }


        private async Task<Tuple<IEnumerable<TResult>, int>> ExecuteAsync<TResult>(
            string query,
            Action<DbCommand>? parameterProvider,
            Func<DbCommand, Func<IDataRecord, int, CancellationToken, Task<TResult>>, CommandBehavior, CancellationToken, Task<Tuple<IEnumerable<TResult>, int>>> executor,
            Func<IDataRecord, int, CancellationToken, Task<TResult>> selector,
            CommandBehavior commandBehavior,
            CancellationToken cancellationToken)
        {
            using (var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false))
            {
                using (var command = connection.CreateCommand())
                {
                    parameterProvider?.Invoke(command);
                    command.CommandText = query;

                    _databaseCommandInterceptor.Intercept(command);

                    Task<Tuple<IEnumerable<TResult>, int>> ret;
                    if (_isSynchronousAdoNetImplementation)
                    {
                        ret = Task.Run(() => executor(command, selector, commandBehavior, cancellationToken), cancellationToken);
                    }
                    else
                    {
                        ret = executor(command, selector, commandBehavior, cancellationToken);
                    }

                    return await ret.ConfigureAwait(continueOnCapturedContext: false);
                }
            }
        }

        private async ValueTask<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        {
            if (_dataSource is not null)
            {
                return await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            }

            var connection = DbConnectionFactory.CreateConnection(_invariantName, _connectionString);
            try
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
                return connection;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

#if TRANSACTIONS_ADONET
        private async Task<int> ExecuteTransactionCoreAsync(
            List<Tuple<string, Action<DbCommand>>> multipleQuery,
            string? currentETag,
            CancellationToken cancellationToken)
        {
            using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            try
            {
                var affectedRows = 0;
                for (var operationIndex = 0; operationIndex < multipleQuery.Count; operationIndex++)
                {
                    var (query, parameterProvider) = multipleQuery[operationIndex];
                    using var command = connection.CreateCommand();
                    parameterProvider?.Invoke(command);
                    command.CommandText = query;
                    command.Transaction = transaction;

                    _databaseCommandInterceptor.Intercept(command);

                    var operation = _isSynchronousAdoNetImplementation
                        ? Task.Run(command.ExecuteNonQuery, cancellationToken)
                        : command.ExecuteNonQueryAsync(cancellationToken);
                    var currentAffectedRows = await operation.ConfigureAwait(continueOnCapturedContext: false);
                    ValidateAffectedRows(operationIndex, currentAffectedRows, currentETag);

                    affectedRows += currentAffectedRows;
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
                return affectedRows;
            }
            catch (DbException exception) when (IsUniqueConstraintViolation(_invariantName, exception))
            {
                await RollbackPreservingOriginalExceptionAsync(transaction, exception, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
                throw CreateTransactionConflict(
                    $"Relational transaction insert conflicted with an existing record for provider '{_invariantName}'.",
                    currentETag,
                    exception);
            }
            catch (Exception exception)
            {
                await RollbackPreservingOriginalExceptionAsync(transaction, exception, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
                throw;
            }
        }

        internal static async Task RollbackPreservingOriginalExceptionAsync(
            DbTransaction transaction,
            Exception originalException,
            CancellationToken cancellationToken)
        {
            try
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            }
            catch (Exception rollbackException)
            {
                originalException.Data[RollbackExceptionDataKey] = rollbackException;
            }
        }

        internal static InconsistentStateException CreateTransactionConflict(
            string message,
            string? currentETag,
            Exception? innerException = null) =>
            new(
                message,
                storedEtag: "Unknown",
                currentEtag: currentETag ?? "null",
                storageException: innerException);

        internal static void ValidateAffectedRows(int operationIndex, int affectedRows, string? currentETag)
        {
            if (affectedRows != 1)
            {
                throw CreateTransactionConflict(
                    $"Relational transaction operation {operationIndex} expected to affect one row but affected {affectedRows}.",
                    currentETag);
            }
        }

        internal static bool IsUniqueConstraintViolation(string invariantName, DbException exception)
        {
            if (invariantName == AdoNetInvariants.InvariantNamePostgreSql)
            {
                return exception.SqlState == "23505";
            }

            var providerErrorNumber = GetProviderErrorNumber(exception);
            return invariantName switch
            {
                AdoNetInvariants.InvariantNameSqlServer => providerErrorNumber is 2601 or 2627,
                AdoNetInvariants.InvariantNameMySql or AdoNetInvariants.InvariantNameMySqlConnector => providerErrorNumber == 1062,
                AdoNetInvariants.InvariantNameOracleDatabase => providerErrorNumber == 1,
                _ => false,
            };
        }

        private static int? GetProviderErrorNumber(DbException exception)
        {
            var value = exception.GetType()
                .GetProperty("Number", BindingFlags.Instance | BindingFlags.Public)?
                .GetValue(exception);
            return value is null ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
#endif


        private static void CommandCancellation(object? state)
        {
            //The MSDN documentation tells that DbCommand.Cancel() should not be called for SqlCommand if the reader has been closed
            //in order to avoid a race condition that would cause the SQL Server to stream the result set
            //despite the connection already closed. Source: https://msdn.microsoft.com/en-us/library/system.data.sqlclient.sqlcommand.cancel(v=vs.110).aspx.
            //Enforcing this behavior across all providers does not seem to hurt.
            var stateTuple = (Tuple<DbDataReader, DbCommand>)state!;
            if (!stateTuple.Item1.IsClosed)
            {
                stateTuple.Item2.Cancel();
            }
        }
    }
}

#nullable restore
