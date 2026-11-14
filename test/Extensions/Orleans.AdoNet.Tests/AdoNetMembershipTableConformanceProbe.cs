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
            SELECT CASE WHEN
                EXISTS (SELECT 1 FROM OrleansMembershipTable WHERE DeploymentId = @ClusterId) OR
                EXISTS (SELECT 1 FROM OrleansMembershipVersionTable WHERE DeploymentId = @ClusterId)
            THEN 1 ELSE 0 END
            """;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@ClusterId";
        parameter.DbType = DbType.String;
        parameter.Size = 150;
        parameter.Value = clusterId;
        command.Parameters.Add(parameter);
        var populated = await command.ExecuteScalarAsync(cancellationToken);
        if (populated is null or DBNull)
        {
            throw new InvalidOperationException("The native membership deletion probe returned no existence result.");
        }

        return Convert.ToInt64(populated, CultureInfo.InvariantCulture) == 0;
    }
}
