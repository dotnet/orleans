using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.Transactions.AdoNet.Entity;

namespace Orleans.Transactions.AdoNet.Utils;
internal static class ActionToSql
{
    public static string InsertSql(string tableName,List<string> insertList,string sqlDot)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append($"INSERT INTO {tableName} (");
        for(int i=0;i < insertList.Count;i++) {
            sb.Append($"{insertList[i]}");
            if(i != insertList.Count - 1)
            {
                sb.Append(",");
            }
        }
        sb.Append(")VALUES (");
        for (int i = 0; i < insertList.Count; i++)
        {
            sb.Append($"{sqlDot}{insertList[i]}");
            if (i != insertList.Count - 1)
            {
                sb.Append(",");
            }
        }
        sb.Append(");");
        return sb.ToString();
    }

    public static string UpdateSql(string tableName,List<string> selectList,List<string> whereList,string sqlDot)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append($"UPDATE {tableName} SET ");
        for (int i = 0; i < selectList.Count; i++)
        {
            sb.Append($"{selectList[i]}={sqlDot}{selectList[i]}");
            if (i != selectList.Count - 1)
            {
                sb.Append(",");
            }
        }
        sb.Append(" WHERE ");
        for (int i = 0; i < whereList.Count; i++)
        {
            sb.Append($"{whereList[i]}={sqlDot}{whereList[i]}");
            if (i != whereList.Count - 1)
            {
                sb.Append(" AND ");
            }
        }
        sb.Append(";");
        return sb.ToString();
    }

    public static string DeleteSql(string tableName, List<string> whereList, string sqlDot)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append($"DELETE FROM {tableName}");
        sb.Append(" WHERE ");
        for (int i = 0; i < whereList.Count; i++)
        {
            sb.Append($"{whereList[i]}={sqlDot}{whereList[i]}");
            if (i != whereList.Count - 1)
            {
                sb.Append(" AND ");
            }
        }
        sb.Append(";");
        return sb.ToString();
    }

    public static string QuerySimpleSql(string tableName, List<string> selectList, List<string> whereList,List<string> orderList, string sqlDot,string orderBy = "ASC")
    {
        StringBuilder sb = new StringBuilder();
        sb.Append($"SELECT ");
        for (int i = 0; i < selectList.Count; i++)
        {
            sb.Append($"{selectList[i]}");
            if (i != selectList.Count - 1)
            {
                sb.Append(",");
            }
        }
        sb.Append($" FROM {tableName} WHERE ");
        for (int i = 0; i < whereList.Count; i++)
        {
            sb.Append($"{whereList[i]}={sqlDot}{whereList[i]}");
            if (i != whereList.Count - 1)
            {
                sb.Append(" AND ");
            }
        }
        if (orderList != null)
        {
            for (int i = 0; i < orderList.Count; i++)
            {
                if (i == 0)
                {
                    sb.Append(" Order By ");
                }
                sb.Append($"{whereList[i]}={sqlDot}{whereList[i]}");
                if (i != orderList.Count - 1)
                {
                    sb.Append(",");
                }
                if (i == orderList.Count - 1)
                {
                    sb.Append($" {orderBy} ");
                }
            }
        }
        sb.Append(";");
        return sb.ToString();
    }
}
