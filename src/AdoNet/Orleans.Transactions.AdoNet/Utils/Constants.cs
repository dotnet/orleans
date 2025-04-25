namespace Orleans.Transactions.AdoNet.Utils;

 internal static class Constants
{
    public static string AddKeySql = "addKeySql";
    public static string UpdateKeySql = "updateKeySql";
    public static string DelKeySql = "delKeySql";
    public static string AddStateSql = "addStateSql";
    public static string UpdateStateSql = "updateStateSql";
    public static string DelStateSql = "delStateSql";

    // sqlserver,mysql,postgre
    public static string SqlParameterDot = "@";
    // oracle
    public static string OracleParameterDot = ":";
}
