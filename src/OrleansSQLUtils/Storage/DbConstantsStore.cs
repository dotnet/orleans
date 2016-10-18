using System;
using System.Collections.Generic;
using System.Data.Common;

namespace Orleans.SqlUtils
{
    internal static class DbConstantsStore
    {
        private static readonly Dictionary<string, DbConstants> invariantNameToConsts =
            new Dictionary<string, DbConstants>
            {
                {
                    AdoNetInvariants.InvariantNameSqlServer,
                    new DbConstants(startEscapeIndicator: '[',
                                    endEscapeIndicator: ']',
                                    unionAllSelectTemplate: " UNION ALL SELECT ")
                },
                {AdoNetInvariants.InvariantNameMySql, new DbConstants(
                                    startEscapeIndicator: '`',
                                    endEscapeIndicator: '`',
                                    unionAllSelectTemplate: " UNION ALL SELECT ")
                },
                {AdoNetInvariants.InvariantNamePostgreSql, new DbConstants(
                                    startEscapeIndicator: '"',
                                    endEscapeIndicator: '"',
                                    unionAllSelectTemplate: " UNION ALL SELECT ")
                },
                {AdoNetInvariants.InvariantNameOracleDatabase, new DbConstants(
                                    startEscapeIndicator: '\"',
                                    endEscapeIndicator: '\"',
                                    unionAllSelectTemplate: " UNION ALL SELECT FROM DUAL ")},
            };

        public static DbConstants GetDbConstants(string invariantName)
        {
            return invariantNameToConsts[invariantName];
        }

        /// <summary>
        /// If the underlying <see cref="DbCommand"/> the storage supports cancellation or not.
        /// </summary>
        /// <param name="storage">The storage used.</param>
        /// <returns><em>TRUE</em> if cancellation is supported. <em>FALSE</em> otherwise.</returns>
        public static bool SupportsCommandCancellation(this IRelationalStorage storage)
        {
            //Currently the assumption is all but MySQL support DbCommand cancellation.
            //For MySQL, see at https://dev.mysql.com/doc/connector-net/en/connector-net-ref-mysqlclient-mysqlcommandmembers.html.
            return SupportsCommandCancellation(storage.InvariantName);
        }


        /// <summary>
        /// If the <see cref="DbCommand"/> that would be used supports cancellation or not.
        /// </summary>
        /// <param name="adoNetProvider">The ADO.NET provider invariant string.</param>
        /// <returns><em>TRUE</em> if cancellation is supported. <em>FALSE</em> otherwise.</returns>
        public static bool SupportsCommandCancellation(string adoNetProvider)
        {
            //Currently the assumption is all but MySQL support DbCommand cancellation.
            //For MySQL, see at https://dev.mysql.com/doc/connector-net/en/connector-net-ref-mysqlclient-mysqlcommandmembers.html.
            return !adoNetProvider.Equals(AdoNetInvariants.InvariantNameMySql, StringComparison.OrdinalIgnoreCase);
        }


        /// <summary>
        /// If the underlying <see cref="DbCommand"/> the storage supports streaming natively.
        /// </summary>
        /// <param name="storage">The storage used.</param>
        /// <returns><em>TRUE</em> if streaming is supported natively. <em>FALSE</em> otherwise.</returns>
        public static bool SupportsStreamNatively(this IRelationalStorage storage)
        {
            //Currently the assumption is all but MySQL support streaming natively.            
            return SupportsStreamNatively(storage.InvariantName);
        }


        /// <summary>
        /// If the underlying <see cref="DbCommand"/> the storage supports streaming natively.
        /// </summary>
        /// <param name="adoNetProvider">The ADO.NET provider invariant string.</param>
        /// <returns><em>TRUE</em> if streaming is supported natively. <em>FALSE</em> otherwise.</returns>
        public static bool SupportsStreamNatively(string adoNetProvider)
        {
            //Currently the assumption is all but MySQL support streaming natively.            
            return !adoNetProvider.Equals(AdoNetInvariants.InvariantNameMySql, StringComparison.OrdinalIgnoreCase);
        }


        /// <summary>
        /// If the underlying ADO.NET implementation is known to be synchronous.
        /// </summary>
        /// <param name="storage">The storage used.</param>
        /// <returns></returns>
        public static bool IsSynchronousAdoNetImplementation(this IRelationalStorage storage)
        {
            //Currently the assumption is all but MySQL are asynchronous.            
            return IsSynchronousAdoNetImplementation(storage.InvariantName);
        }


        /// <summary>
        /// If the <see cref="DbCommand"/> that would be used supports cancellation or not.
        /// </summary>
        /// <param name="adoNetProvider">The ADO.NET provider invariant string.</param>
        /// <returns></returns>
        public static bool IsSynchronousAdoNetImplementation(string adoNetProvider)
        {
            //Currently the assumption is all but MySQL support DbCommand cancellation.            
            return adoNetProvider.Equals(AdoNetInvariants.InvariantNameMySql, StringComparison.OrdinalIgnoreCase);
        }        
    }


    internal class DbConstants
    {
        /// <summary>
        /// A query template for union all select
        /// </summary>
        public readonly string UnionAllSelectTemplate;

        /// <summary>
        /// The character that indicates a start escape key for columns and tables that are reserved words.
        /// </summary>
        public readonly char StartEscapeIndicator;

        /// <summary>
        /// The character that indicates an end escape key for columns and tables that are reserved words.
        /// </summary>
        public readonly char EndEscapeIndicator;

        public DbConstants(char startEscapeIndicator, char endEscapeIndicator, string unionAllSelectTemplate)
        {
            StartEscapeIndicator = startEscapeIndicator;
            EndEscapeIndicator = endEscapeIndicator;
            UnionAllSelectTemplate = unionAllSelectTemplate;
        }
    }
}
