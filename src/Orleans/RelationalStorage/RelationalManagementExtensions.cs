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
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;
using System.Transactions;


namespace Orleans.Runtime.Storage.Management
{
    /// <summary>
    /// Contains some relational database management extension methods.
    /// </summary>
    public static class RelationalManagementExtensions
    {
        /// <summary>
        /// Creates a transaction scope in which the storage operates.
        /// </summary>
        /// <param name="storage">The storage object.</param>
        /// <returns>Returns a default transaction scope for the given storage.</returns>
        /// <remarks>Does not set <c>System.Transactions.TransactionScopeAsyncFlowOption.Enabled">TransactionScopeAsyncFlowOption</c>as it is .NET 4.5.1.
        /// This is required to support transaction scopes in async-await type of flows.</remarks>
        public static TransactionScope CreateTransactionScope(this IRelationalStorage storage)
        {
            //By default transaction scope is set to serializable and a timeout for one minute.
            //The timeout is regardless of what has been set on the command object itself and
            //the query would be rolled back in the end. These defaults are more usable and
            //can be customized per database.            
            var transactionOptions = new TransactionOptions { IsolationLevel = IsolationLevel.ReadCommitted, Timeout = TransactionManager.MaximumTimeout };
            return new TransactionScope(TransactionScopeOption.Required, transactionOptions);
        }


        /// <summary>
        /// Creates a new instance of the storage based on the old connection string by changing the database name.
        /// </summary>
        /// <param name="storage">The old storage instance connectionstring of which to base the new one.</param>
        /// <param name="newDatabaseName">Connection string instance name of the database.</param>
        /// <returns>A new <see cref="IRelationalStorage"/> instance with having the same connection string as <paramref name="storage"/>but with with a new databaseName.</returns>
        public static IRelationalStorage CreateNewStorageInstance(this IRelationalStorage storage, string newDatabaseName)
        {
            string databaseKey = string.Empty;
            switch(storage.InvariantName)
            {
                case(WellKnownRelationalInvariants.SqlServer):
                {
                    databaseKey = "Database";
                    break;
                }
                default:
                {
                    databaseKey = "Database";
                    break;
                }
            }

            var csb = new DbConnectionStringBuilder();
            csb.ConnectionString = storage.ConnectionString;
            csb[databaseKey] = newDatabaseName;

            return RelationalStorage.CreateInstance(storage.InvariantName, csb.ConnectionString);
        }


        /// <summary>
        /// Checks the existence of a database using the given <see paramref="storage"/> storage object.
        /// </summary>
        /// <param name="storage">The storage to use.</param>
        /// <param name="databaseName">The name of the database existence of which to check.</param>
        /// <returns><em>TRUE</em> if the given database exists. <em>FALSE</em> otherwise.</returns>
        public static async Task<bool> ExistsDatabaseAsync(this IRelationalStorage storage, string databaseName)
        {
            var existsTemplate = RelationalConstants.GetConstant(storage.InvariantName, RelationalConstants.ExistsDatabaseKey);
            var ret = await storage.ReadAsync(string.Format(existsTemplate, databaseName), command =>
            {
                var p = command.CreateParameter();
                p.ParameterName = "databaseName";
                p.Value = databaseName;
                command.Parameters.Add(p);
            }, (selector, resultSetCount) => { return selector.GetBoolean(0); }).ConfigureAwait(continueOnCapturedContext: false);

            return ret.First();
        }


        /// <summary>
        /// Creates a database with a given name.
        /// </summary>
        /// <param name="storage">The storage to use.</param>
        /// <param name="databaseName">The name of the database to create.</param>
        /// <returns>The call will be succesful if the DDL query is succseful. Otherwisen an exception will be thrown.</returns>
        public static async Task CreateDatabaseAsync(this IRelationalStorage storage, string databaseName)
        {
            var creationTemplate = RelationalConstants.GetConstant(storage.InvariantName, RelationalConstants.CreateDatabaseKey);
            await storage.ExecuteAsync(string.Format(creationTemplate, databaseName), command => { }).ConfigureAwait(continueOnCapturedContext: false);
        }


        /// <summary>
        /// Drops a database with a given name.
        /// </summary>
        /// <param name="storage">The storage to use.</param>
        /// <param name="databaseName">The name of the database to drop.</param>
        /// <returns>The call will be succesful if the DDL query is successful. Otherwise an exception will be thrown.</returns>
        public static async Task DropDatabaseAsync(this IRelationalStorage storage, string databaseName)
        {
            var dropTemplate = RelationalConstants.GetConstant(storage.InvariantName, RelationalConstants.DropDatabaseKey);
            await storage.ExecuteAsync(string.Format(dropTemplate, databaseName), command => { }).ConfigureAwait(continueOnCapturedContext: false);
        }


        /// <summary>
        /// Deletes all data from Orleans database tables.
        /// </summary>
        /// <param name="storage">The storage to use.</param>        
        /// <returns>The call will be succesful if the DDL query is successful. Otherwise an exception will be thrown.</returns>               
        public static async Task DeleteAllDataAsync(this IRelationalStorage storage)
        {
            var deleteAllTemplate = RelationalConstants.GetConstant(storage.InvariantName, RelationalConstants.DeleteAllDataKey);
            await storage.ExecuteAsync(deleteAllTemplate, command => { }).ConfigureAwait(continueOnCapturedContext: false);
        }
    }
}
