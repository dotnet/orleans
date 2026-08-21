// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Generic;
using Orleans.Transactions.AdoNet.Utils;
using Xunit;

namespace Orleans.Transactions.AdoNet.Tests;

/// <summary>
/// Unit tests for <see cref="ActionToSql"/> — pure static string-builder logic,
/// zero external dependencies, no database.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Transactions")]
public sealed class ActionToSqlTests
{
    // -----------------------------------------------------------------------
    // InsertSql
    // -----------------------------------------------------------------------

    [Fact]
    public void InsertSql_SingleColumn_SqlServer()
    {
        var result = ActionToSql.InsertSql("T", new List<string> { "Col" }, "@");

        Assert.Equal("INSERT INTO T (Col) VALUES (@Col)", result);
    }

    [Fact]
    public void InsertSql_MultipleColumns_SqlServer()
    {
        var result = ActionToSql.InsertSql("T", new List<string> { "A", "B", "C" }, "@");

        Assert.Equal("INSERT INTO T (A,B,C) VALUES (@A,@B,@C)", result);
        // secondary: columns appear in declared order
        Assert.True(result.IndexOf("@A") < result.IndexOf("@B"));
        Assert.True(result.IndexOf("@B") < result.IndexOf("@C"));
    }

    [Fact]
    public void InsertSql_MultipleColumns_Oracle()
    {
        var result = ActionToSql.InsertSql("T", new List<string> { "A", "B", "C" }, ":");

        // Oracle uses ':' prefix, not '@'
        Assert.Equal("INSERT INTO T (A,B,C) VALUES (:A,:B,:C)", result);
        Assert.DoesNotContain("@", result);
    }

    [Fact]
    public void InsertSql_EmptyList_ProducesEmptyParens()
    {
        // Documents current behavior: empty column list produces empty parens.
        var result = ActionToSql.InsertSql("T", new List<string>(), "@");

        Assert.Equal("INSERT INTO T () VALUES ()", result);
        Assert.StartsWith("INSERT INTO T", result);
    }

    // -----------------------------------------------------------------------
    // UpdateSql
    // -----------------------------------------------------------------------

    [Fact]
    public void UpdateSql_SingleSetSingleWhere_SqlServer()
    {
        var result = ActionToSql.UpdateSql("T", new List<string> { "A" }, new List<string> { "B" }, "@");

        Assert.Equal("UPDATE T SET A=@A WHERE B=@B", result);
    }

    [Fact]
    public void UpdateSql_MultipleSetMultipleWhere_SqlServer()
    {
        var result = ActionToSql.UpdateSql("T", new List<string> { "A", "B" }, new List<string> { "C", "D" }, "@");

        Assert.Equal("UPDATE T SET A=@A,B=@B WHERE C=@C AND D=@D", result);
        // SET columns are comma-separated (not AND-separated)
        Assert.Contains(",", result.Substring(0, result.IndexOf("WHERE")));
        // WHERE columns are AND-separated
        Assert.Contains("AND", result.Substring(result.IndexOf("WHERE")));
    }

    [Fact]
    public void UpdateSql_Oracle()
    {
        var result = ActionToSql.UpdateSql("T", new List<string> { "A" }, new List<string> { "B" }, ":");

        Assert.Equal("UPDATE T SET A=:A WHERE B=:B", result);
        Assert.DoesNotContain("@", result);
    }

    // -----------------------------------------------------------------------
    // DeleteSql
    // -----------------------------------------------------------------------

    [Fact]
    public void DeleteSql_WithWhere_SqlServer()
    {
        var result = ActionToSql.DeleteSql("T", new List<string> { "A", "B" }, "@");

        Assert.Equal("DELETE FROM T WHERE A=@A AND B=@B", result);
        Assert.StartsWith("DELETE FROM T", result);
    }

    [Fact]
    public void DeleteSql_NullWhere_NoWhereClause()
    {
        // Passing null whereList omits the WHERE clause entirely.
        var result = ActionToSql.DeleteSql("T", null!, "@");

        Assert.Equal("DELETE FROM T", result);
        Assert.DoesNotContain("WHERE", result);
    }

    [Fact]
    public void DeleteSql_Oracle()
    {
        var result = ActionToSql.DeleteSql("T", new List<string> { "A" }, ":");

        Assert.Equal("DELETE FROM T WHERE A=:A", result);
        Assert.DoesNotContain("@", result);
    }

    // -----------------------------------------------------------------------
    // QuerySimpleSql
    // -----------------------------------------------------------------------

    [Fact]
    public void QuerySimpleSql_NullOrderList_NoOrderByClause()
    {
        var result = ActionToSql.QuerySimpleSql("T", new List<string> { "S" }, new List<string> { "W" }, null, "@");

        Assert.Contains("SELECT S", result);
        Assert.Contains("FROM T", result);
        Assert.Contains("W=@W", result);
        // No order-by when orderList is null
        Assert.DoesNotContain("ORDER BY", result);
    }

    [Fact]
    public void QuerySimpleSql_WithOrderList_UsesOrderColumns()
    {
        var result = ActionToSql.QuerySimpleSql(
            "T",
            new List<string> { "S" },
            new List<string> { "W" },
            new List<string> { "O" },
            "@");

        Assert.Equal("SELECT S FROM T WHERE W=@W ORDER BY O ASC", result);
    }

    [Fact]
    public void QuerySimpleSql_WithMultipleOrderColumns_UsesEachOrderColumn()
    {
        var result = ActionToSql.QuerySimpleSql(
            "T",
            new List<string> { "S" },
            new List<string> { "W1", "W2" },
            new List<string> { "O1", "O2" },
            "@");

        Assert.EndsWith("ORDER BY O1,O2 ASC", result);
    }

    [Fact]
    public void QuerySimpleSql_NullWhereList_OmitsWhereClause()
    {
        var result = ActionToSql.QuerySimpleSql("T", new List<string> { "S" }, null, null, "@");

        Assert.Equal("SELECT S FROM T", result);
    }

    [Fact]
    public void QuerySimpleSql_OrderByDesc()
    {
        var result = ActionToSql.QuerySimpleSql(
            "T",
            new List<string> { "S" },
            new List<string> { "W" },
            new List<string> { "W" },
            "@",
            orderBy: "DESC");

        Assert.EndsWith(" ORDER BY W DESC", result);
    }

    [Fact]
    public void QuerySimpleSql_MultipleSelectColumns()
    {
        var result = ActionToSql.QuerySimpleSql("T", new List<string> { "A", "B", "C" }, null, null, "@");

        // All columns appear in SELECT clause in order
        Assert.Equal("SELECT A,B,C FROM T", result);
        // Columns are comma-separated
        Assert.Contains("A,B,C", result);
    }

    [Fact]
    public void QuerySimpleSql_Oracle()
    {
        var result = ActionToSql.QuerySimpleSql(
            "T",
            new List<string> { "S" },
            new List<string> { "W" },
            null,
            ":");

        // Oracle parameter prefix ':' used in WHERE
        Assert.Contains("W=:W", result);
        Assert.DoesNotContain("@", result);
    }
}
