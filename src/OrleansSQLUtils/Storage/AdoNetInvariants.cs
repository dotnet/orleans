namespace Orleans.SqlUtils
{
    /// <summary>
    /// A holder for well known, vendor specific connector class invariant names.
    /// </summary>
    public static class AdoNetInvariants
    {
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
