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
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;

namespace Orleans.SqlUtils.Management
{
    /// <summary>
    /// Contains some relational database management extension methods.
    /// </summary>
    public static class RelationalManagementExtensions
    {
        /// <summary>
        /// Seeks for database provider factory classes from GAC or as indicated by
        /// the configuration file, see at <see href="https://msdn.microsoft.com/en-us/library/dd0w4a2z%28v=vs.110%29.aspx">Obtaining a DbProviderFactory</see>.
        /// </summary>
        /// <returns>Database constants with values from <see cref="DbProviderFactories"/>.</returns>
        /// <remarks>Every call may potentially update data as it is refreshed from <see cref="DbProviderFactories"/>.</remarks>
        public static QueryConstantsBag GetAdoNetFactoryData()
        {
            var queryBag = new QueryConstantsBag();

            //This method seeks for factory classes either from the GAC or as indicated in a config file.            
            var factoryData = DbProviderFactories.GetFactoryClasses();

            //The provided default information will be loaded from here to the factory constants
            //which are further augmented with predefined query templates.
            foreach(DataRow row in factoryData.Rows)
            {
                var invariantName = row[AdoNetInvariants.InvariantNameKey].ToString();
                queryBag.AddOrModifyQueryConstant(invariantName, AdoNetInvariants.InvariantNameKey, invariantName);
                queryBag.AddOrModifyQueryConstant(invariantName, AdoNetInvariants.NameKey, row[AdoNetInvariants.NameKey].ToString());
                queryBag.AddOrModifyQueryConstant(invariantName, AdoNetInvariants.DescriptionKey, row[AdoNetInvariants.DescriptionKey].ToString());
                queryBag.AddOrModifyQueryConstant(invariantName, AdoNetInvariants.AssemblyQualifiedNameKey, row[AdoNetInvariants.AssemblyQualifiedNameKey].ToString());
            }

            return queryBag;
        }


        /// <summary>
        /// Initializes Orleans queries from the database. Orleans uses only these queries and the variables therein, nothing more.
        /// </summary>
        /// <param name="storage">The storage to use.</param>
        /// <returns>Orleans queries have been loaded to silo or client memory.</returns>
        /// <remarks>This is public only to be usable to the statistics providers. Not intended for public use otherwise.</remarks>
        public static async Task<QueryConstantsBag> InitializeOrleansQueriesAsync(this IRelationalStorage storage)
        {
            var queryConstants = new QueryConstantsBag();            
            var query = queryConstants.GetConstant(storage.InvariantName, QueryKeys.OrleansQueriesKey);
            var orleansQueries = await storage.ReadAsync(query, _ => { }, (selector, _) =>
            {
                return Tuple.Create(selector.GetValue<string>("QueryKey"), selector.GetValue<string>("QueryText"));
            }).ConfigureAwait(continueOnCapturedContext: false);

            //The queries need to be added to be used later with a given key.
            foreach(var orleansQuery in orleansQueries)
            {
                queryConstants.AddOrModifyQueryConstant(storage.InvariantName, orleansQuery.Item1, orleansQuery.Item2);
            }

            //Check that all the required keys are loaded and throw an exception giving the keys expected but not loaded.
            var loadedQueriesKeys = queryConstants.GetAllConstants(storage.InvariantName).Keys;
            var missingQueryKeys = QueryKeys.Keys.Except(loadedQueriesKeys);
            if(missingQueryKeys.Any())
            {
                throw new ArgumentException(string.Format("Not all required queries found when loading from the database. Missing are: {0}", string.Join(",", missingQueryKeys)));
            }

            return await Task.FromResult(queryConstants);
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
                case(AdoNetInvariants.InvariantNameSqlServer):
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
    }
}
