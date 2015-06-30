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
using System.Linq;


namespace Orleans.Runtime.Storage.Relational
{
    /// <summary>
    /// Utility functions to work with relational storage.
    /// </summary>
    public static class RelationalStorageUtilities
    {
        /// <summary>
        /// Removes <em>GO</em> batch separators from the script and returns a series of scripts.
        /// </summary>
        /// <param name="sqlScript">The script from which to remove the separators.</param>
        /// <returns>Scripts without separators.</returns>
        public static IEnumerable<string> RemoveBatchSeparators(string sqlScript)
        {
            return sqlScript.Split(new[] { "GO" }, StringSplitOptions.RemoveEmptyEntries);
        }


        /// <summary>
        /// Creates and instance of SQL Server storage.
        /// </summary>
        /// <returns>A <see cref="IRelationalStorage"/> with <see cref="IRelationalStorage.InvariantName"/> of <see cref="WellKnownRelationalInvariants.SqlServer"/>.</returns>
        public static IRelationalStorage CreateDefaultSqlServerStorageInstance()
        {
            var sqlServer = RelationalConstants.GetRelationalConstants().First(i => i.InvariantName == WellKnownRelationalInvariants.SqlServer);
            return CreateGenericStorageInstance(sqlServer.InvariantName, sqlServer.DefaultConnectionString);
        }


        /// <summary>
        /// Creates an instance of a database of type <see cref="IRelationalStorage"/>.
        /// </summary>
        /// <param name="invariantName">The invariant name of the connector for this database.</param>
        /// <param name="connectionString">The connection string this database should use for database operations.</param>
        /// <returns></returns>
        public static IRelationalStorage CreateGenericStorageInstance(string invariantName, string connectionString)
        {
            return RelationalStorage.CreateInstance(invariantName, connectionString);            
        }
    }
}
