using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

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
    /// Convenience functions to work with objects of type <see cref="IRelationalStorage"/>.
    /// </summary>
    internal static class RelationalStorageExtensions
    {
        /// <summary>
        /// A simplified version of <see cref="IRelationalStorage.ReadAsync{TResult}"/>
        /// </summary>
        /// <param name="storage"></param>
        /// <param name="query"></param>
        /// <param name="selector"></param>
        /// <param name="parameterProvider"></param>
        /// <typeparam name="TResult"></typeparam>
        /// <returns></returns>
        public static Task<IEnumerable<TResult>> ReadAsync<TResult>(this IRelationalStorage storage, string query, Func<IDataRecord, TResult> selector, Action<IDbCommand>? parameterProvider)
        {
            ArgumentNullException.ThrowIfNull(selector);
            return storage.ReadAsync(query, parameterProvider, (record, i, cancellationToken) => Task.FromResult(selector(record)));
        }

        /// <summary>
        /// Uses <see cref="IRelationalStorage"/> with <see cref="DbExtensions.ReflectionParameterProvider{T}(IDbCommand, T, IReadOnlyDictionary{string, string})"/>.
        /// </summary>
        /// <typeparam name="TResult">The type of the result.</typeparam>
        /// <param name="storage">The storage to use.</param>
        /// <param name="query">Executes a given statement. Especially intended to use with <em>SELECT</em> statement, but works with other queries too.</param>
        /// <param name="parameters">Adds parameters to the query. Parameter names must match those defined in the query.</param>
        /// <param name="cancellationToken">The cancellation token. Defaults to <see cref="CancellationToken.None"/>.</param>
        /// <returns>A list of objects as a result of the <see paramref="query"/>.</returns>
        /// <example>This uses reflection to read results and match the parameters.
        /// <code>
        /// //This struct holds the return value in this example.
        /// public struct Information
        /// {
        ///     public string TABLE_CATALOG { get; set; }
        ///     public string TABLE_NAME { get; set; }
        /// }
        ///
        /// //Here reflection (<seealso cref="DbExtensions.ReflectionParameterProvider{T}(IDbCommand, T, IReadOnlyDictionary{string, string})"/>)
        /// is used to match parameter names as well as to read back the results (<seealso cref="DbExtensions.ReflectionSelector{TResult}(IDataRecord)"/>).
        /// var query = "SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = @tname;";
        /// IEnumerable&lt;Information&gt; informationData = await db.ReadAsync&lt;Information&gt;(query, new { tname = 200000 });
        /// </code>
        /// </example>
        public static Task<IEnumerable<TResult>> ReadAsync<TResult>(this IRelationalStorage storage, string query, object? parameters, CancellationToken cancellationToken = default)
        {
            return storage.ReadAsync(query, command =>
            {
                if (parameters != null)
                {
                    command.ReflectionParameterProvider(parameters);
                }
            }, (selector, resultSetCount, token) => Task.FromResult(selector.ReflectionSelector<TResult>()), cancellationToken: cancellationToken);
        }


        /// <summary>
        /// Uses <see cref="IRelationalStorage"/> with <see cref="DbExtensions.ReflectionParameterProvider{T}(System.Data.IDbCommand, T, IReadOnlyDictionary{string, string})">DbExtensions.ReflectionParameterProvider</see>.
        /// </summary>
        /// <typeparam name="TResult">The type of the result.</typeparam>
        /// <param name="storage">The storage to use.</param>
        /// <param name="query">Executes a given statement. Especially intended to use with <em>SELECT</em> statement, but works with other queries too.</param>
        /// <param name="cancellationToken">The cancellation token. Defaults to <see cref="CancellationToken.None"/>.</param>
        /// <returns>A list of objects as a result of the <see paramref="query"/>.</returns>
        public static Task<IEnumerable<TResult>> ReadAsync<TResult>(this IRelationalStorage storage, string query, CancellationToken cancellationToken = default)
        {
            return ReadAsync<TResult>(storage, query, null, cancellationToken);
        }


        /// <summary>
        /// Uses <see cref="IRelationalStorage"/> with <see cref="DbExtensions.ReflectionSelector{TResult}(System.Data.IDataRecord)"/>.
        /// </summary>
        /// <param name="storage">The storage to use.</param>
        /// <param name="query">Executes a given statement. Especially intended to use with <em>INSERT</em>, <em>UPDATE</em>, <em>DELETE</em> or <em>DDL</em> queries.</param>
        /// <param name="parameters">Adds parameters to the query. Parameter names must match those defined in the query.</param>
        /// <param name="cancellationToken">The cancellation token. Defaults to <see cref="CancellationToken.None"/>.</param>
        /// <returns>Affected rows count.</returns>
        /// <example>This uses reflection to provide parameters to an execute
        /// query that reads only affected rows count if available.
        /// <code>
        /// //Here reflection (<seealso cref="DbExtensions.ReflectionParameterProvider{T}(IDbCommand, T, IReadOnlyDictionary{string, string})"/>)
        /// is used to match parameter names as well as to read back the results (<seealso cref="DbExtensions.ReflectionSelector{TResult}(IDataRecord)"/>).
        /// var query = "IF NOT EXISTS(SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = @tname) CREATE TABLE Test(Id INT PRIMARY KEY IDENTITY(1, 1) NOT NULL);"
        /// await db.ExecuteAsync(query, new { tname = "test_table" });
        /// </code>
        /// </example>
        public static Task<int> ExecuteAsync(this IRelationalStorage storage, string query, object? parameters, CancellationToken cancellationToken = default)
        {
            return storage.ExecuteAsync(query, command =>
            {
                if (parameters != null)
                {
                    command.ReflectionParameterProvider(parameters);
                }
            }, cancellationToken: cancellationToken);
        }


        /// <summary>
        /// Uses <see cref="IRelationalStorage"/> with <see cref="DbExtensions.ReflectionSelector{TResult}(System.Data.IDataRecord)"/>.
        /// </summary>
        /// <param name="storage">The storage to use.</param>
        /// <param name="query">Executes a given statement. Especially intended to use with <em>INSERT</em>, <em>UPDATE</em>, <em>DELETE</em> or <em>DDL</em> queries.</param>
        /// <param name="cancellationToken">The cancellation token. Defaults to <see cref="CancellationToken.None"/>.</param>
        /// <returns>Affected rows count.</returns>
        public static Task<int> ExecuteAsync(this IRelationalStorage storage, string query, CancellationToken cancellationToken = default)
        {
            return ExecuteAsync(storage, query, null, cancellationToken);
        }


        /// <summary>
        /// Returns a native implementation of <see cref="DbDataReader.GetStream(int)"/> for those providers
        /// which support it. Otherwise returns a chunked read using <see cref="DbDataReader.GetBytes(int, long, byte[], int, int)"/>.
        /// </summary>
        /// <param name="reader">The reader from which to return the stream.</param>
        /// <param name="ordinal">The ordinal column for which to return the stream.</param>
        /// <param name="storage">The storage that gives the invariant.</param>
        /// <returns></returns>
        public static Stream GetStream(this DbDataReader reader, int ordinal, IRelationalStorage storage)
        {
            if (storage.SupportsStreamNatively())
            {
                return reader.GetStream(ordinal);
            }

            return new OrleansRelationalDownloadStream(reader, ordinal);
        }
    }
}

#nullable restore
