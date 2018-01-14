using System.Collections.Generic;

#if CLUSTERING_ADONET
namespace Orleans.Clustering.AdoNet.Storage
#elif PERSISTENCE_ADONET
namespace Orleans.Persistence.AdoNet.Storage
#elif REMINDERS_ADONET
namespace Orleans.Reminders.AdoNet.Storage
#elif STATISTICS_ADONET
namespace Orleans.Statistics.AdoNet.Storage
#elif TESTER_SQLUTILS
namespace Orleans.Tests.SqlUtils
#else
// No default namespace intentionally to cause compile errors if something is not defined
#endif
{
    internal static class DbConstantsStore
    {
        private static readonly Dictionary<string, DbConstants> invariantNameToConsts =
            new Dictionary<string, DbConstants>
            {
                {
                    AdoNetInvariants.InvariantNameSqlServer,
                    new DbConstants(startEscapeIndicator: '[',
                                    endEscapeIndicator: ']',
                                    unionAllSelectTemplate: " UNION ALL SELECT ",
                                    isSynchronousAdoNetImplementation: false,
                                    supportsStreamNatively: true,
                                    supportsCommandCancellation: true,
                                    commandInterceptor: NoOpCommandInterceptor.Instance)
                },
                {AdoNetInvariants.InvariantNameMySql, new DbConstants(
                                    startEscapeIndicator: '`',
                                    endEscapeIndicator: '`',
                                    unionAllSelectTemplate: " UNION ALL SELECT ",
                                    isSynchronousAdoNetImplementation: true,
                                    supportsStreamNatively: false,
                                    supportsCommandCancellation: false,
                                    commandInterceptor: NoOpCommandInterceptor.Instance)
                },
                {AdoNetInvariants.InvariantNamePostgreSql, new DbConstants(
                                    startEscapeIndicator: '"',
                                    endEscapeIndicator: '"',
                                    unionAllSelectTemplate: " UNION ALL SELECT ",
                                    isSynchronousAdoNetImplementation: true, //there are some intermittent PostgreSQL problems too, see more discussion at https://github.com/dotnet/orleans/pull/2949.
                                    supportsStreamNatively: true,
                                    supportsCommandCancellation: true, // See https://dev.mysql.com/doc/connector-net/en/connector-net-ref-mysqlclient-mysqlcommandmembers.html.
                                    commandInterceptor: NoOpCommandInterceptor.Instance) 
                                    
                },
                {AdoNetInvariants.InvariantNameOracleDatabase, new DbConstants(
                                    startEscapeIndicator: '\"',
                                    endEscapeIndicator: '\"',
                                    unionAllSelectTemplate: " FROM DUAL UNION ALL SELECT ",
                                    isSynchronousAdoNetImplementation: true,
                                    supportsStreamNatively: false,
                                    supportsCommandCancellation: false, // Is supported but the remarks sound scary: https://docs.oracle.com/cd/E11882_01/win.112/e23174/OracleCommandClass.htm#DAFIEHHG.
                                    commandInterceptor: OracleCommandInterceptor.Instance) 
                    
                }, 
            };

        public static DbConstants GetDbConstants(string invariantName)
        {
            return invariantNameToConsts[invariantName];
        }

        /// <summary>
        /// If the underlying <see cref="DbCommand"/> the storage supports cancellation or not.
        /// </summary>
        /// <param name="storage">The storage used.</param>
        /// <returns><em>TRUE</em> if cancellation is supported. <em>FALSE</em> otherwise.</returns>
        public static bool SupportsCommandCancellation(this IRelationalStorage storage)
        {
            return SupportsCommandCancellation(storage.InvariantName);
        }


        /// <summary>
        /// If the <see cref="DbCommand"/> that would be used supports cancellation or not.
        /// </summary>
        /// <param name="adoNetProvider">The ADO.NET provider invariant string.</param>
        /// <returns><em>TRUE</em> if cancellation is supported. <em>FALSE</em> otherwise.</returns>
        public static bool SupportsCommandCancellation(string adoNetProvider)
        {
            return GetDbConstants(adoNetProvider).SupportsCommandCancellation;
        }


        /// <summary>
        /// If the underlying <see cref="DbCommand"/> the storage supports streaming natively.
        /// </summary>
        /// <param name="storage">The storage used.</param>
        /// <returns><em>TRUE</em> if streaming is supported natively. <em>FALSE</em> otherwise.</returns>
        public static bool SupportsStreamNatively(this IRelationalStorage storage)
        {
            return SupportsStreamNatively(storage.InvariantName);
        }


        /// <summary>
        /// If the underlying <see cref="DbCommand"/> the storage supports streaming natively.
        /// </summary>
        /// <param name="adoNetProvider">The ADO.NET provider invariant string.</param>
        /// <returns><em>TRUE</em> if streaming is supported natively. <em>FALSE</em> otherwise.</returns>
        public static bool SupportsStreamNatively(string adoNetProvider)
        {
            return GetDbConstants(adoNetProvider).SupportsStreamNatively;
        }


        /// <summary>
        /// If the underlying ADO.NET implementation is known to be synchronous.
        /// </summary>
        /// <param name="storage">The storage used.</param>
        /// <returns></returns>
        public static bool IsSynchronousAdoNetImplementation(this IRelationalStorage storage)
        {
            //Currently the assumption is all but MySQL are asynchronous.            
            return IsSynchronousAdoNetImplementation(storage.InvariantName);
        }


        /// <summary>
        /// If the <see cref="DbCommand"/> that would be used supports cancellation or not.
        /// </summary>
        /// <param name="adoNetProvider">The ADO.NET provider invariant string.</param>
        /// <returns></returns>
        public static bool IsSynchronousAdoNetImplementation(string adoNetProvider)
        {
            return GetDbConstants(adoNetProvider).IsSynchronousAdoNetImplementation;
        }

        public static ICommandInterceptor GetDatabaseCommandInterceptor(string invariantName)
        {
            return GetDbConstants(invariantName).DatabaseCommandInterceptor;
        }
    }

    internal class DbConstants
    {
        /// <summary>
        /// A query template for union all select
        /// </summary>
        public readonly string UnionAllSelectTemplate;

        /// <summary>
        /// Indicates whether the ADO.net provider does only support synchronous operations.
        /// </summary>
        public readonly bool IsSynchronousAdoNetImplementation;

        /// <summary>
        /// Indicates whether the ADO.net provider does streaming operations natively.
        /// </summary>
        public readonly bool SupportsStreamNatively;

        /// <summary>
        /// Indicates whether the ADO.net provider supports cancellation of commands.
        /// </summary>
        public readonly bool SupportsCommandCancellation;

        /// <summary>
        /// The character that indicates a start escape key for columns and tables that are reserved words.
        /// </summary>
        public readonly char StartEscapeIndicator;

        /// <summary>
        /// The character that indicates an end escape key for columns and tables that are reserved words.
        /// </summary>
        public readonly char EndEscapeIndicator;

        public readonly ICommandInterceptor DatabaseCommandInterceptor;


        public DbConstants(char startEscapeIndicator, char endEscapeIndicator, string unionAllSelectTemplate,
                           bool isSynchronousAdoNetImplementation, bool supportsStreamNatively, bool supportsCommandCancellation, ICommandInterceptor commandInterceptor)
        {
            StartEscapeIndicator = startEscapeIndicator;
            EndEscapeIndicator = endEscapeIndicator;
            UnionAllSelectTemplate = unionAllSelectTemplate;
            IsSynchronousAdoNetImplementation = isSynchronousAdoNetImplementation;
            SupportsStreamNatively = supportsStreamNatively;
            SupportsCommandCancellation = supportsCommandCancellation;
            DatabaseCommandInterceptor = commandInterceptor;
        }
    }
}
