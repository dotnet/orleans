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

using Orleans.Runtime.Storage.Relational;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;


namespace Orleans.Runtime.Storage.RelationalExtensions
{
    /// <summary>
    /// Convenienience functions to work with objects of type <see cref="IRelationalStorage"/>.
    /// </summary>
    public static class RelationalStorageExtensions
    {
        /// <summary>
        /// Used to format .NET objects suitable to relational database format.
        /// </summary>
        private static SqlFormatProvider sqlFormatProvider = new SqlFormatProvider();

        /// <summary>
        /// This is a template to produce query parameters that are indexed.
        /// </summary>
        private static string indexedParameterTemplate = "@p{0}";


        /// <summary>
        /// Executes a multi-record insert query clause with <em>SELECT UNION ALL</em>.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="storage">The storage to use.</param>
        /// <param name="tableName">The table name to against which to execute the query.</param>
        /// <param name="parameters">The parameters to insert.</param>
        /// <param name="useSqlParams"><em>TRUE</em> if the query should be in parameterized form. <em>FALSE</em> otherwise.</param>
        /// <returns>The rows affected.</returns>
        public static Task<int> ExecuteMultipleInsertIntoAsync<T>(this IRelationalStorage storage, string tableName, IEnumerable<T> parameters, bool useSqlParams = false)
        {
            if(string.IsNullOrWhiteSpace(tableName))
            {
                throw new ArgumentException("The name must be a legal SQL table name", "tableName");
            }

            if(parameters == null)
            {
                throw new ArgumentNullException("parameters");
            }

            //SqlParameters map is needed in case the query needs to be parameterized in order to avoid two
            //reflection passes as first a query needs to be constructed and after that when a database
            //command object has been created, parameters need to be provided to them.
            var sqlParameters = new Dictionary<string, object>();
            const string insertIntoValuesTemplate = "INSERT INTO {0} ({1}) SELECT {2};";
            string columns = string.Empty;
            var values = new List<string>();
            var materializedParameters = parameters.ToList();
            if(materializedParameters.Any())
            {
                //Type and property information are the same for all of the objects.
                //The following assumes the property names will be retrieved in the same
                //order as is the index iteration done.
                var type = materializedParameters.First().GetType();
                var properties = type.GetProperties();
                var startEscapeIndicator = RelationalConstants.GetConstant(storage.InvariantName, RelationalConstants.StartEscapeIndicatorKey);
                var endEscapeIndicator = RelationalConstants.GetConstant(storage.InvariantName, RelationalConstants.StartEscapeIndicatorKey);
                columns = string.Join(",", properties.Select(f => string.Format("{0}{1}{2}", startEscapeIndicator, f.Name, endEscapeIndicator)));
                int parameterCount = 0;
                //This datarows will be used multiple times. It is all right as all the rows
                //are of the same length, so all the values will always be replaced.
                var dataRows = new string[properties.Length];
                foreach(var row in materializedParameters)
                {                    
                    for(int i = 0; i < properties.Length; ++i)
                    {
                        if(useSqlParams)
                        {
                            var parameterName = string.Format(string.Format(indexedParameterTemplate, parameterCount));
                            dataRows[i] = parameterName;
                            sqlParameters.Add(parameterName, properties[i].GetValue(row, null));
                            ++parameterCount;
                        }
                        else
                        {
                            dataRows[i] = string.Format(sqlFormatProvider, "{0}", properties[i].GetValue(row, null));
                        }
                    }

                    values.Add(string.Format("{0}", string.Join(",", dataRows)));
                }
            }

            //If this is an Oracle database, every UNION ALL SELECT needs to have "FROM DUAL" appended.
            if(storage.InvariantName == WellKnownRelationalInvariants.OracleDatabase)
            {
                //Counting starts from 1 as the first SELECT should not select from dual.
                for(int i = 1; i < values.Count; ++i)
                {
                    values[i] = string.Concat(values[i], " FROM DUAL");
                }
            }

            var query = string.Format(insertIntoValuesTemplate, tableName, columns, string.Join(" UNION ALL SELECT ", values));
            return storage.ExecuteAsync(query, command =>
            {
                if(useSqlParams)
                {
                    foreach(var sqlParameter in sqlParameters)
                    {
                        var p = command.CreateParameter();
                        p.ParameterName = sqlParameter.Key;
                        p.Value = sqlParameter.Value ?? DBNull.Value;
                        p.Direction = ParameterDirection.Input;
                        command.Parameters.Add(p);
                    }
                }
            });
        }


        /// <summary>
        /// Uses <see cref="IRelationalStorage"/> with <see cref="DbExtensions.ReflectionParameterProvider{T}(IDbCommand, T, IReadOnlyDictionary{string, string})">DbExtensions.ReflectionParameterProvider</see>.
        /// </summary>
        /// <typeparam name="TResult">The type of the result.</typeparam>
        /// <param name="storage">The storage to use.</param>
        /// <param name="query">Executes a given statement. Especially intended to use with <em>SELECT</em> statement, but works with other queries too.</param>
        /// <param name="parameters">Adds parameters to the query. Parameter names must match those defined in the query.</param>
        /// <returns>A list of objects as a result of the <see paramref="query"/>.</returns>
        public static async Task<IEnumerable<TResult>> ReadAsync<TResult>(this IRelationalStorage storage, string query, object parameters)
        {
            return await storage.ReadAsync(query, command =>
            {
                if(parameters != null)
                {
                    command.ReflectionParameterProvider(parameters);
                }
            }, (selector, resultSetCount) => selector.ReflectionSelector<TResult>()).ConfigureAwait(continueOnCapturedContext: false);
        }


        /// <summary>
        /// Uses <see cref="IRelationalStorage"/> with <see cref="DbExtensions.ReflectionParameterProvider{T}(System.Data.IDbCommand, T, IReadOnlyDictionary{string, string})">DbExtensions.ReflectionParameterProvider</see>.
        /// </summary>
        /// <typeparam name="TResult">The type of the result.</typeparam>
        /// <param name="storage">The storage to use.</param>
        /// <param name="query">Executes a given statement. Especially intended to use with <em>SELECT</em> statement, but works with other queries too.</param>        
        /// <returns>A list of objects as a result of the <see paramref="query"/>.</returns>
        public static async Task<IEnumerable<TResult>> ReadAsync<TResult>(this IRelationalStorage storage, string query)
        {
            return await ReadAsync<TResult>(storage, query, null).ConfigureAwait(continueOnCapturedContext: false);
        }


        /// <summary>
        /// Uses <see cref="IRelationalStorage"/> with <see cref="DbExtensions.ReflectionSelector{TResult}(System.Data.IDataRecord)"/>.
        /// </summary>
        /// <param name="storage">The storage to use.</param>
        /// <param name="query">Executes a given statement. Especially intended to use with <em>INSERT</em>, <em>UPDATE</em>, <em>DELETE</em> or <em>DDL</em> queries.</param>
        /// <param name="parameters">Adds parameters to the query. Parameter names must match those defined in the query.</param>
        /// <returns>Affected rows count.</returns>
        public static async Task<int> ExecuteAsync(this IRelationalStorage storage, string query, object parameters)
        {
            return await storage.ExecuteAsync(query, command =>
            {
                if(parameters != null)
                {
                    command.ReflectionParameterProvider(parameters);
                }
            }).ConfigureAwait(continueOnCapturedContext: false);
        }


        /// <summary>
        /// Uses <see cref="IRelationalStorage"/> with <see cref="DbExtensions.ReflectionSelector{TResult}(System.Data.IDataRecord)"/>.
        /// </summary>
        /// <param name="storage">The storage to use.</param>
        /// <param name="query">Executes a given statement. Especially intended to use with <em>INSERT</em>, <em>UPDATE</em>, <em>DELETE</em> or <em>DDL</em> queries.</param>        
        /// <returns>Affected rows count.</returns>
        public static async Task<int> ExecuteAsync(this IRelationalStorage storage, string query)
        {
            return await ExecuteAsync(storage, query, null).ConfigureAwait(continueOnCapturedContext: false);
        }        
    }
}
