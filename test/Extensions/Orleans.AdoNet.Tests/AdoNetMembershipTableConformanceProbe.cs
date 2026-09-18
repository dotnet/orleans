using System.Data;
using System.Data.Common;
using System.Globalization;

namespace UnitTests.MembershipTests;

internal static class AdoNetMembershipTableConformanceProbe
{
    internal static async ValueTask<bool> IsDeletedAsync(DbConnection connection, string clusterId, CancellationToken cancellationToken)
    {
        await using var ownedConnection = connection;
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 30;
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM OrleansMembershipTable WHERE DeploymentId = @ClusterId) +
                (SELECT COUNT(*) FROM OrleansMembershipVersionTable WHERE DeploymentId = @ClusterId)
            """;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@ClusterId";
        parameter.DbType = DbType.String;
        parameter.Size = 150;
        parameter.Value = clusterId;
        command.Parameters.Add(parameter);
        var count = await command.ExecuteScalarAsync(cancellationToken);
        if (count is null or DBNull)
        {
            throw new InvalidOperationException("The native membership deletion probe returned no row count.");
        }

        return Convert.ToInt64(count, CultureInfo.InvariantCulture) == 0;
    }
}
