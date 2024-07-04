using Orleans.AdoNet.Storage;

namespace Orleans.AdoNet.Core;

internal static class DbConstantsStore
{
    private static readonly Dictionary<string, DbConstants> InvariantNameToConsts = new()
    {
        {
            AdoNetInvariants.InvariantNameSqlServer,
            new DbConstants(
                startEscapeIndicator: '[',
                endEscapeIndicator: ']',
                unionAllSelectTemplate: " UNION ALL SELECT ",
                isSynchronousAdoNetImplementation: false,
                supportsStreamNatively: true,
                supportsCommandCancellation: true,
                commandInterceptor: NoOpCommandInterceptor.Instance)
        },
        {
            AdoNetInvariants.InvariantNameMySql,
            new DbConstants(
                startEscapeIndicator: '`',
                endEscapeIndicator: '`',
                unionAllSelectTemplate: " UNION ALL SELECT ",
                isSynchronousAdoNetImplementation: true,
                supportsStreamNatively: false,
                supportsCommandCancellation: false,
                commandInterceptor: NoOpCommandInterceptor.Instance)
        },
        {
            AdoNetInvariants.InvariantNamePostgreSql,
            new DbConstants(
                startEscapeIndicator: '"',
                endEscapeIndicator: '"',
                unionAllSelectTemplate: " UNION ALL SELECT ",
                isSynchronousAdoNetImplementation: true, //there are some intermittent PostgreSQL problems too, see more discussion at https://github.com/dotnet/orleans/pull/2949.
                supportsStreamNatively: true,
                supportsCommandCancellation: true, // See https://dev.mysql.com/doc/connector-net/en/connector-net-ref-mysqlclient-mysqlcommandmembers.html.
                commandInterceptor: NoOpCommandInterceptor.Instance)
        },
        {
            AdoNetInvariants.InvariantNameOracleDatabase,
            new DbConstants(
                startEscapeIndicator: '\"',
                endEscapeIndicator: '\"',
                unionAllSelectTemplate: " FROM DUAL UNION ALL SELECT ",
                isSynchronousAdoNetImplementation: true,
                supportsStreamNatively: false,
                supportsCommandCancellation: false, // Is supported but the remarks sound scary: https://docs.oracle.com/cd/E11882_01/win.112/e23174/OracleCommandClass.htm#DAFIEHHG.
                commandInterceptor: OracleCommandInterceptor.Instance)
        },
        {
            AdoNetInvariants.InvariantNameSqlServerDotnetCore,
            new DbConstants(
                startEscapeIndicator: '[',
                endEscapeIndicator: ']',
                unionAllSelectTemplate: " UNION ALL SELECT ",
                isSynchronousAdoNetImplementation: false,
                supportsStreamNatively: true,
                supportsCommandCancellation: true,
                commandInterceptor: NoOpCommandInterceptor.Instance)
        },
        {
            AdoNetInvariants.InvariantNameMySqlConnector,
            new DbConstants(
                startEscapeIndicator: '[',
                endEscapeIndicator: ']',
                unionAllSelectTemplate: " UNION ALL SELECT ",
                isSynchronousAdoNetImplementation: false,
                supportsStreamNatively: true,
                supportsCommandCancellation: true,
                commandInterceptor: NoOpCommandInterceptor.Instance)
        }
    };

    public static DbConstants GetDbConstants(string invariantName) => InvariantNameToConsts[invariantName];

    /// <summary>
    /// If the underlying storage supports cancellation or not.
    /// </summary>
    /// <param name="storage">The storage used.</param>
    /// <returns><em>TRUE</em> if cancellation is supported. <em>FALSE</em> otherwise.</returns>
    public static bool SupportsCommandCancellation(this IRelationalStorage storage) => SupportsCommandCancellation(storage.InvariantName);

    /// <summary>
    /// If the provider supports cancellation or not.
    /// </summary>
    /// <param name="adoNetProvider">The ADO.NET provider invariant string.</param>
    /// <returns><em>TRUE</em> if cancellation is supported. <em>FALSE</em> otherwise.</returns>
    public static bool SupportsCommandCancellation(string adoNetProvider) => GetDbConstants(adoNetProvider).SupportsCommandCancellation;

    /// <summary>
    /// If the underlying storage supports streaming natively.
    /// </summary>
    /// <param name="storage">The storage used.</param>
    /// <returns><em>TRUE</em> if streaming is supported natively. <em>FALSE</em> otherwise.</returns>
    public static bool SupportsStreamNatively(this IRelationalStorage storage) => SupportsStreamNatively(storage.InvariantName);

    /// <summary>
    /// If the provider supports streaming natively.
    /// </summary>
    /// <param name="adoNetProvider">The ADO.NET provider invariant string.</param>
    /// <returns><em>TRUE</em> if streaming is supported natively. <em>FALSE</em> otherwise.</returns>
    public static bool SupportsStreamNatively(string adoNetProvider) => GetDbConstants(adoNetProvider).SupportsStreamNatively;

    /// <summary>
    /// If the underlying ADO.NET implementation is known to be synchronous.
    /// </summary>
    /// <param name="storage">The storage used.</param>
    /// <returns></returns>
    public static bool IsSynchronousAdoNetImplementation(this IRelationalStorage storage) =>

        //Currently the assumption is all but MySQL are asynchronous.
        IsSynchronousAdoNetImplementation(storage.InvariantName);

    /// <summary>
    /// If the provider supports cancellation or not.
    /// </summary>
    /// <param name="adoNetProvider">The ADO.NET provider invariant string.</param>
    /// <returns></returns>
    public static bool IsSynchronousAdoNetImplementation(string adoNetProvider) => GetDbConstants(adoNetProvider).IsSynchronousAdoNetImplementation;

    public static ICommandInterceptor GetDatabaseCommandInterceptor(string invariantName) => GetDbConstants(invariantName).DatabaseCommandInterceptor;
}
