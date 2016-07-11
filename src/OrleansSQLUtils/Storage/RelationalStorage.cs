using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.SqlUtils
{
    /// <summary>
    /// A general purpose class to work with a given relational database and ADO.NET provider.
    /// </summary>    
    [DebuggerDisplay("InvariantName = {InvariantName}, ConnectionString = {ConnectionString}")]
    public class RelationalStorage: IRelationalStorage
    {
        /// <summary>
        /// The connection string to use.
        /// </summary>
        private readonly string connectionString;

        /// <summary>
        /// The invariant name of the connector for this database.
        /// </summary>
        private readonly string invariantName;

        /// <summary>
        /// If the ADO.NET provider of this storage supports cancellation or not. This
        /// capability is queried and the the result is cached here.
        /// </summary>
        private readonly bool supportsCommandCancellation;

        /// <summary>
        /// If the underlying ADO.NET implementation is natively asynchronous
        /// (the ADO.NET Db*.XXXAsync classes are overriden) or not.
        /// </summary>
        private readonly bool isSynchronousAdoNetImplementation;


        /// <summary>
        /// The invariant name of the connector for this database.
        /// </summary>
        public string InvariantName
        {
            get
            {
                return invariantName;
            }
        }


        /// <summary>
        /// The connection string used to connecto the database.
        /// </summary>
        public string ConnectionString
        {
            get
            {
                return connectionString;
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
            if(string.IsNullOrWhiteSpace(invariantName))
            {
                throw new ArgumentException("The name of invariant must contain characters", "invariantName");
            }

            if(string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ArgumentException("Connection string must contain characters", "connectionString");
            }

            return new RelationalStorage(invariantName, connectionString);
        }


        /// <summary>
        /// Executes a given statement. Especially intended to use with <em>SELECT</em> statement.
        /// </summary>
        /// <typeparam name="TResult">The result type.</typeparam>
        /// <param name="query">Executes a given statement. Especially intended to use with <em>SELECT</em> statement.</param>        
        /// <param name="parameterProvider">Adds parameters to the query. Parameter names must match those defined in the query.</param>
        /// <param name="selector">This function transforms the raw <see cref="IDataRecord"/> results to type <see paramref="TResult"/> the <see cref="int"/> parameter being the resultset number.</param>
        /// <param name="cancellationToken">The cancellation token. Defaults to <see cref="CancellationToken.None"/>.</param>
        /// <param name="commandBehavior">The command behavior that should be used. Defaults to <see cref="CommandBehavior.Default"/>.</param>
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
        public Task<IEnumerable<TResult>> ReadAsync<TResult>(string query, Action<IDbCommand> parameterProvider, Func<IDataRecord, int, CancellationToken, Task<TResult>> selector, CancellationToken cancellationToken = default(CancellationToken), CommandBehavior commandBehavior = CommandBehavior.Default)
        {
            //If the query is something else that is not acceptable (e.g. an empty string), there will an appropriate database exception.
            if(query == null)
            {
                throw new ArgumentNullException("query");
            }

            if(selector == null)
            {
                throw new ArgumentNullException("selector");
            }

            //It is certain the result is already ready here and can be collected straight away. Having a truly asynchronous result collection
            //IAsyncEnumerable<TResult> or equivalent method ought to be used. Taking the result here without async-await saves for generating
            //the async state machine.
            var ret = ExecuteAsync(query, parameterProvider, ExecuteReaderAsync, selector, cancellationToken, commandBehavior).GetAwaiter().GetResult().Item1;
            return Task.FromResult(ret);
        }


        /// <summary>
        /// Executes a given statement. Especially intended to use with <em>INSERT</em>, <em>UPDATE</em>, <em>DELETE</em> or <em>DDL</em> queries.
        /// </summary>
        /// <param name="query">The query to execute.</param>
        /// <param name="parameterProvider">Adds parameters to the query. Parameter names must match those defined in the query.</param>
        /// <param name="cancellationToken">The cancellation token. Defaults to <see cref="CancellationToken.None"/>.</param>
        /// <param name="commandBehavior">The command behavior that should be used. Defaults to <see cref="CommandBehavior.Default"/>.</param>
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
        public Task<int> ExecuteAsync(string query, Action<IDbCommand> parameterProvider, CancellationToken cancellationToken = default(CancellationToken), CommandBehavior commandBehavior = CommandBehavior.Default)
        {
            //If the query is something else that is not acceptable (e.g. an empty string), there will an appropriate database exception.
            if(query == null)
            {
                throw new ArgumentNullException("query");
            }

            //It is certain the result is already ready here and can be collected straight away. Having a truly asynchronous result collection
            //IAsyncEnumerable<TResult> or equivalent method ought to be used. Taking the result here without async-await saves for generating
            //the async state machine.
            var ret = ExecuteAsync(query, parameterProvider, ExecuteReaderAsync, (unit, id, c) => Task.FromResult(unit), cancellationToken, commandBehavior).GetAwaiter().GetResult().Item2;
            return Task.FromResult(ret);
        }

        /// <summary>
        /// Creates an instance of a database of type <see cref="RelationalStorage"/>.
        /// </summary>
        /// <param name="invariantName">The invariant name of the connector for this database.</param>
        /// <param name="connectionString">The connection string this database should use for database operations.</param>
        private RelationalStorage(string invariantName, string connectionString)
        {
            this.connectionString = connectionString;
            this.invariantName = invariantName;
            supportsCommandCancellation = DbConstantsStore.SupportsCommandCancellation(InvariantName);
            isSynchronousAdoNetImplementation = DbConstantsStore.IsSynchronousAdoNetImplementation(InvariantName);
        }

        private static async Task<Tuple<IEnumerable<TResult>, int>> SelectAsync<TResult>(DbDataReader reader, Func<IDataReader, int, CancellationToken, Task<TResult>> selector, CancellationToken cancellationToken)
        {
            var results = new List<TResult>();
            int resultSetCount = 0;
            while(reader.HasRows)
            {
                while(await reader.ReadAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false))
                {
                    var obj = await selector(reader, resultSetCount, cancellationToken).ConfigureAwait(false);
                    results.Add(obj);
                }

                await reader.NextResultAsync(cancellationToken);
                ++resultSetCount;
            }

            return Tuple.Create(results.AsEnumerable(), reader.RecordsAffected);
        }


        private async Task<Tuple<IEnumerable<TResult>, int>> ExecuteReaderAsync<TResult>(DbCommand command, Func<IDataRecord, int, CancellationToken, Task<TResult>> selector, CancellationToken cancellationToken, CommandBehavior commandBehavior)
        {
            using(var reader = await command.ExecuteReaderAsync(commandBehavior, cancellationToken).ConfigureAwait(continueOnCapturedContext: false))
            {
                CancellationTokenRegistration cancellationRegistration = default(CancellationTokenRegistration);
                try
                {
                    if(cancellationToken.CanBeCanceled && supportsCommandCancellation)
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
            Action<DbCommand> parameterProvider,
            Func<DbCommand, Func<IDataRecord, int, CancellationToken, Task<TResult>>, CancellationToken, CommandBehavior, Task<Tuple<IEnumerable<TResult>, int>>> executor,
            Func<IDataRecord, int, CancellationToken, Task<TResult>> selector,
            CancellationToken cancellationToken,
            CommandBehavior commandBehavior)
        {
            using (var connection = DbConnectionFactory.CreateConnection(invariantName, connectionString))
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
                using(var command = connection.CreateCommand())
                {                    
                    if(parameterProvider != null)
                    {
                        parameterProvider(command);
                    }
                    command.CommandText = query;

                    Task<Tuple<IEnumerable<TResult>, int>> ret;
                    if(isSynchronousAdoNetImplementation)
                    {
                        ret = Task.Run(() => executor(command, selector, cancellationToken, commandBehavior), cancellationToken);
                    }
                    else
                    {
                        ret = executor(command, selector, cancellationToken, commandBehavior);
                    }

                    return await ret.ConfigureAwait(continueOnCapturedContext: false);
                }
            }
        }


        private static void CommandCancellation(object state)
        {
            //The MSDN documentation tells that DbCommand.Cancel() should not be called for SqlCommand if the reader has been closed
            //in order to avoid a race condition that would cause the SQL Server to stream the result set
            //despite the connection already closed. Source: https://msdn.microsoft.com/en-us/library/system.data.sqlclient.sqlcommand.cancel(v=vs.110).aspx.
            //Enforcing this behavior across all providers does not seem to hurt.
            var stateTuple = (Tuple<DbDataReader, DbCommand>)state;
            if(!stateTuple.Item1.IsClosed)
            {
                stateTuple.Item2.Cancel();
            }
        }
    }
}
