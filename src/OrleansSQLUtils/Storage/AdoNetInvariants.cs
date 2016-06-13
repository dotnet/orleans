using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Orleans.SqlUtils
{
    /// <summary>
    /// A holder for well known, vendor specific connector class invariant names.
    /// </summary>
    public static class AdoNetInvariants
    {
        /// <summary>
        /// A list of the supported invariants.
        /// </summary>
        public static ICollection<string> Invariants { get; } = new Collection<string>(new List<string>(new[]
        {
            InvariantNameMySql,
            InvariantNameOracleDatabase,
            InvariantNamePostgreSql,
            InvariantNameSqlLite,
            InvariantNameSqlServer
        }));

        /// <summary>
        /// Microsoft SQL Server invariant name string.
        /// </summary>
        public const string InvariantNameSqlServer = "System.Data.SqlClient";

        /// <summary>
        /// Oracle Database server invariant name string.
        /// </summary>
        public const string InvariantNameOracleDatabase = "Oracle.DataAccess.Client";

        /// <summary>
        /// SQLite invariant name string.
        /// </summary>
        public const string InvariantNameSqlLite = "System.Data.SQLite";

        /// <summary>
        /// MySql invariant name string.
        /// </summary>
        public const string InvariantNameMySql = "MySql.Data.MySqlClient";

        /// <summary>
        /// PostgreSQL invariant name string.
        /// </summary>
        public const string InvariantNamePostgreSql = "Npgsql";
    }
}
