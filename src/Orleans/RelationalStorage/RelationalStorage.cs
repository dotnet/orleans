/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading.Tasks;
using System.Linq;
using System.Diagnostics;


namespace Orleans.Runtime.Storage.Relational
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
        /// The factory to provide vendor specific functionality.
        /// </summary>
        /// <remarks>For more about <see href="http://florianreischl.blogspot.fi/2011/08/adonet-connection-pooling-internals-and.html">ConnectionPool</see>
        /// and issues with using this factory. Take these notes into account when considering robustness of Orleans!</remarks>
        private readonly DbProviderFactory factory;


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
            if(string.IsNullOrWhiteSpace("invariantName"))
            {
                throw new ArgumentException("The name of invariant must contain characters", "invariantName");
            }

            if(string.IsNullOrWhiteSpace("connectionString"))
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
        /// <returns>A list of objects as a result of the <see paramref="query"/>.</returns>
        public async Task<IEnumerable<TResult>> ReadAsync<TResult>(string query, Action<IDbCommand> parameterProvider, Func<IDataRecord, int, TResult> selector)
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

            return (await ExecuteAsync(query, parameterProvider, ExecuteReaderAsync, selector, factory, connectionString).ConfigureAwait(continueOnCapturedContext: false)).Item1;
        }


        /// <summary>
        /// Executes a given statement. Especially intended to use with <em>INSERT</em>, <em>UPDATE</em>, <em>DELETE</em> or <em>DDL</em> queries.
        /// </summary>
        /// <param name="query">The query to execute.</param>
        /// <param name="parameterProvider">Adds parameters to the query. Parameter names must match those defined in the query.</param>
        /// <returns>Affected rows count.</returns>
        public async Task<int> ExecuteAsync(string query, Action<IDbCommand> parameterProvider)
        {
            //If the query is something else that is not acceptable (e.g. an empty string), there will an appropriate database exception.
            if(query == null)
            {
                throw new ArgumentNullException("query");
            }

            return (await ExecuteAsync(query, parameterProvider, ExecuteReaderAsync, (unit, id) => unit, factory, connectionString).ConfigureAwait(continueOnCapturedContext: false)).Item2;
        }


        protected RelationalStorage(string invariantName, string connectionString)
        {
            factory = DbProviderFactories.GetFactory(invariantName);
            this.connectionString = connectionString;
            this.invariantName = invariantName;
        }


        private static async Task<Tuple<IEnumerable<TResult>, int>> SelectAsync<TResult>(DbDataReader reader, Func<IDataReader, int, TResult> selector)
        {
            var results = new List<TResult>();
            int resultSetCount = 0;
            while(reader.HasRows)
            {
                while(await reader.ReadAsync().ConfigureAwait(continueOnCapturedContext: false))
                {
                    var obj = selector(reader, resultSetCount);
                    results.Add(obj);
                }

                await reader.NextResultAsync();
                ++resultSetCount;
            }

            return Tuple.Create(results.AsEnumerable(), reader.RecordsAffected);
        }


        private static async Task<Tuple<IEnumerable<TResult>, int>> ExecuteReaderAsync<TResult>(DbCommand command, Func<IDataRecord, int, TResult> selector)
        {
            using(var reader = await command.ExecuteReaderAsync().ConfigureAwait(continueOnCapturedContext: false))
            {
                return await SelectAsync(reader, selector).ConfigureAwait(false);
            }
        }


        private static async Task<Tuple<IEnumerable<TResult>, int>> ExecuteAsync<TResult>(
            string query,
            Action<DbCommand> parameterProvider,
            Func<DbCommand, Func<IDataRecord, int, TResult>, Task<Tuple<IEnumerable<TResult>, int>>> executor,
            Func<IDataRecord, int, TResult> selector,
            DbProviderFactory factory,
            string connectionString)
        {
            using(var connection = factory.CreateConnection())
            {
                connection.ConnectionString = connectionString;
                await connection.OpenAsync().ConfigureAwait(continueOnCapturedContext: false);
                using(var command = connection.CreateCommand())
                {
                    if(parameterProvider != null)
                    {
                        parameterProvider(command);
                    }

                    command.CommandText = query;
                    return await executor(command, selector).ConfigureAwait(continueOnCapturedContext: false);
                }
            }
        }
    }
}
