using System.Collections.Generic;
using System.Linq;

namespace Orleans.Transactions.AdoNet.Utils;
internal static class ActionToSql
{
    public static string InsertSql(string tableName, IReadOnlyList<string> columns, string parameterPrefix) =>
        $"INSERT INTO {tableName} ({string.Join(",", columns)}) VALUES ({string.Join(",", columns.Select(column => $"{parameterPrefix}{column}"))})";

    public static string UpdateSql(
        string tableName,
        IReadOnlyList<string> columns,
        IReadOnlyList<string> whereColumns,
        string parameterPrefix,
        IReadOnlyDictionary<string, string>? whereParameterNames = null) =>
        $"UPDATE {tableName} SET {JoinAssignments(columns, parameterPrefix, ",")} WHERE {JoinAssignments(whereColumns, parameterPrefix, " AND ", whereParameterNames)}";

    public static string DeleteSql(string tableName, IReadOnlyList<string>? whereColumns, string parameterPrefix)
    {
        var whereClause = whereColumns is { Count: > 0 }
            ? $" WHERE {JoinAssignments(whereColumns, parameterPrefix, " AND ")}"
            : string.Empty;
        return $"DELETE FROM {tableName}{whereClause}";
    }

    public static string QuerySimpleSql(
        string tableName,
        IReadOnlyList<string> selectColumns,
        IReadOnlyList<string>? whereColumns,
        IReadOnlyList<string>? orderColumns,
        string parameterPrefix,
        string orderBy = "ASC")
    {
        var whereClause = whereColumns is { Count: > 0 }
            ? $" WHERE {JoinAssignments(whereColumns, parameterPrefix, " AND ")}"
            : string.Empty;
        var orderClause = orderColumns is { Count: > 0 }
            ? $" ORDER BY {string.Join(",", orderColumns)} {orderBy}"
            : string.Empty;
        return $"SELECT {string.Join(",", selectColumns)} FROM {tableName}{whereClause}{orderClause}";
    }

    private static string JoinAssignments(
        IEnumerable<string> columns,
        string parameterPrefix,
        string separator,
        IReadOnlyDictionary<string, string>? parameterNames = null) =>
        string.Join(
            separator,
            columns.Select(column =>
                $"{column}={parameterPrefix}{(parameterNames?.GetValueOrDefault(column) ?? column)}"));
}
