using Orleans.Docs.Snippets.Aspire;

if (args is ["--example", var example, .. var appHostArgs])
{
    Action<string[]> run = example switch
    {
        "adonet-sqlserver" => AppHostExamples.AdoNetAppHost,
        "adonet-postgresql" => AppHostExamples.AdoNetPostgreSqlAppHost,
        "adonet-mysql" => AppHostExamples.AdoNetMySqlAppHost,
        "adonet-oracle" => AppHostExamples.AdoNetOracleAppHost,
        _ => throw new ArgumentException($"Unknown AppHost example '{example}'."),
    };
    run(appHostArgs);
}
else
{
    AppHostExamples.BasicOrleansCluster(args);
}
