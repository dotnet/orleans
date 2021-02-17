using System.Data.Common;

namespace Tester.AdoNet
{
    internal static class PackageReferences
    {
        public static readonly DbProviderFactory[] Factories =
        {
            System.Data.SqlClient.SqlClientFactory.Instance,
            MySql.Data.MySqlClient.MySqlClientFactory.Instance,
            //Oracle.ManagedDataAccess.Client.OracleClientFactory.Instance, // no tests currently
            Npgsql.NpgsqlFactory.Instance,
            //Microsoft.Data.Sqlite.SqliteFactory.Instance, // no tests currently
            //Microsoft.Data.SqlClient.SqlClientFactory.Instance, // no tests currently
            //MySqlConnector.MySqlConnectorFactory.Instance, // no tests currently
        };
    }
}