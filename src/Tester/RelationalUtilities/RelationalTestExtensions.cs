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

using System.Linq;
using System.Threading.Tasks;
using Orleans.SqlUtils;


namespace UnitTests.General
{
    public static class RelationalTestExtensions
    {
        /// <summary>
        /// Checks the existence of a database using the given <see paramref="storage"/> storage object.
        /// </summary>
        /// <param name="storage">The storage to use.</param>
        /// <param name="databaseName">The name of the database existence of which to check.</param>
        /// <returns><em>TRUE</em> if the given database exists. <em>FALSE</em> otherwise.</returns>
        public static async Task<bool> ExistsDatabaseAsync(this IRelationalStorage storage, string query, string databaseName)
        {            
            var ret = await storage.ReadAsync(string.Format(query, databaseName), command =>
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
        public static async Task CreateDatabaseAsync(this IRelationalStorage storage, string query, string databaseName)
        {            
            await storage.ExecuteAsync(string.Format(query, databaseName), command => { }).ConfigureAwait(continueOnCapturedContext: false);
        }


        /// <summary>
        /// Drops a database with a given name.
        /// </summary>
        /// <param name="storage">The storage to use.</param>
        /// <param name="databaseName">The name of the database to drop.</param>
        /// <returns>The call will be succesful if the DDL query is successful. Otherwise an exception will be thrown.</returns>
        public static async Task DropDatabaseAsync(this IRelationalStorage storage, string query, string databaseName)
        {            
            await storage.ExecuteAsync(string.Format(query, databaseName), command => { }).ConfigureAwait(continueOnCapturedContext: false);
        }


        /// <summary>
        /// Deletes all data from Orleans database tables.
        /// </summary>
        /// <param name="storage">The storage to use.</param>        
        /// <returns>The call will be succesful if the DDL query is successful. Otherwise an exception will be thrown.</returns>               
        public static async Task DeleteAllDataAsync(this IRelationalStorage storage, string query)
        {            
            await storage.ExecuteAsync(query, command => { }).ConfigureAwait(continueOnCapturedContext: false);
        }
    }
}
